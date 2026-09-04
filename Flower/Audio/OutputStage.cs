using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Flower.Audio
{
    // Everything that happens to PCM between the ring buffer and the sound
    // card, in one place and in float.
    //
    // The pipeline used to hand the ring's S16 bytes straight to miniaudio and
    // let it apply the master volume on the way out. That path had three
    // separate audible faults: miniaudio's volume stage is
    // `(ma_int16)(sample * factor)` - truncating, unrounded and undithered, so
    // it adds quantisation distortion and a downward bias at every volume
    // below full; nothing anywhere ramped a gain, so a slider drag stepped
    // once per device period (100ms on the conservative profile - zipper
    // noise) and a per-track VolumeAdjustment stepped exactly at the gapless
    // seam; and every discontinuity - pause, stop, a flush on seek or skip, an
    // underrun - cut the waveform at whatever amplitude it happened to be at,
    // which is the definition of a click.
    //
    // So: widen to float once, do all the arithmetic there at full precision,
    // and requantise exactly once, at the end, with dither. miniaudio's own
    // master volume is pinned at 1.0 by MiniaudioSink so its integer path is
    // never used.
    //
    // Real-time contract, same as the ring's Read(): called only from
    // MiniaudioSink.DataCallback, allocates nothing in steady state (the float
    // scratch buffers are grow-only and reach their size within the first
    // callback or two), takes no lock, and blocks on nothing. Everything the
    // UI thread changes arrives through a volatile field or an Interlocked
    // exchange and is picked up on the next callback.
    public sealed class OutputStage
    {
        private readonly uint _sampleRate;

        // Cached rather than read from GaplessFormat per callback: this runs
        // on the render path, and the format is frozen for the session anyway
        // (see GaplessFormat). Holding it here is also what lets a test build
        // a stage in either format without configuring a process-wide static.
        private readonly PcmSampleFormat _sampleFormat;
        private readonly int _bytesPerSample;

        // Full-scale for the format being requantised to, as the float
        // magnitude one LSB below it. The dither and the clamp are both in
        // native integer units - see Requantise.
        private readonly float _positiveFullScale;
        private readonly float _negativeFullScale;

        // The two float scratch buffers. _samples carries the signal;
        // _crossfade only ever holds the *other* EQ's output for the one
        // callback that spans an EQ change (see Process).
        private float[] _samples = [];
        private float[] _crossfade = [];

        // Live tuning, replaced wholesale rather than field by field so the
        // callback always sees a coherent set.
        private volatile AudioTimingSettings _timing = new AudioTimingSettings().Clamped();

        // Linear gain the ramp is heading for, as a bit pattern because
        // Interlocked has no float overload for a plain read. Owned by
        // whoever sets Volume; the callback only reads it.
        private int _targetGainBits = BitConverter.SingleToInt32Bits(1f);

        // Where the ramp is right now. Callback-owned.
        private float _currentGain = 1f;

        private volatile Equalizer? _equalizer;

        // The EQ the last callback actually ran. Callback-owned; compared
        // against _equalizer to detect a swap that needs crossfading.
        private Equalizer? _activeEqualizer;

        // Frames left in the declick fade-in, and its total length - the
        // envelope is (total - remaining) / total, so it reaches 1.0 exactly.
        private int _fadeInRemaining;
        private int _fadeInLength;

        // Fade-out state. _fadeOutRequested is set by a transport command on
        // another thread; the callback picks it up, runs the envelope down and
        // signals _fadeOutComplete when it has reached (and held) zero.
        private volatile bool _fadeOutRequested;
        private int _fadeOutRemaining;
        private int _fadeOutLength;
        private readonly ManualResetEventSlim _fadeOutComplete = new(false);
        private bool _fadeOutSignalled;

        // The ring generation this stage last rendered. A change means a
        // flush landed - a seek, a manual skip, a fresh start - so the next
        // audio is from somewhere else entirely and gets faded in.
        private int _lastGeneration;
        private bool _hasRendered;

        // TPDF dither state: two independent uniform draws summed give a
        // triangular distribution, which is the standard choice for
        // requantisation - it decorrelates the error from the signal (no
        // quantisation distortion, just a constant low-level noise floor) and,
        // unlike a single rectangular draw, leaves no noise modulation. Its
        // own PRNG rather than Random.Shared: this runs on the real-time
        // thread and Random.Shared's per-thread instance is a managed
        // allocation on first touch.
        private uint _ditherState = 0x9E3779B9;
        private float _previousDither;

        public OutputStage(uint sampleRate, PcmSampleFormat sampleFormat = GaplessFormat.DefaultSampleFormat)
        {
            _sampleRate = sampleRate;
            _sampleFormat = sampleFormat;
            _bytesPerSample = GaplessFormat.BytesPerSampleOf(sampleFormat);

            var bits = _bytesPerSample * 8;
            _negativeFullScale = -(1 << (bits - 1));
            _positiveFullScale = (1 << (bits - 1)) - 1;
        }

        // What this stage was built for. MiniaudioSink compares them against
        // the negotiated format to decide whether reopening a device needs a
        // new stage or can keep the one carrying the user's EQ and volume.
        public uint SampleRate => _sampleRate;

        public PcmSampleFormat SampleFormat => _sampleFormat;

        public AudioTimingSettings Timing
        {
            get => _timing;
            set => _timing = value.Clamped();
        }

        // Linear amplitude, 0..1. Ramped to over GainRampMs rather than
        // applied at once - see the class remarks.
        public float TargetGain
        {
            get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _targetGainBits));
            set => Interlocked.Exchange(ref _targetGainBits, BitConverter.SingleToInt32Bits(Math.Clamp(value, 0f, 1f)));
        }

        // null is a true bypass, not an all-zero-dB filter. A swap is
        // crossfaded over one callback (see Process) rather than dropped in
        // with a zeroed delay line, which used to be an accepted click.
        public Equalizer? Equalizer
        {
            get => _equalizer;
            set => _equalizer = value;
        }

        // Maps the 0-100 slider onto amplitude. Cubic rather than the raw
        // linear percent it used to be: linear amplitude spends most of the
        // slider's travel in a range the ear barely separates, so the useful
        // adjustment all happens in the bottom fifth. This is roughly -18dB at
        // half and -60dB at a tenth, with no discontinuity at either end.
        public static float GainForVolumePercent(int percent)
        {
            var normalized = Math.Clamp(percent, 0, 100) / 100f;
            return normalized * normalized * normalized;
        }

        // Asks for a fade to silence, for a pause/stop/flush that is about to
        // happen. Returns once the callback has faded out, or once
        // FadeOutWaitMs has passed - the callback may never run again (the
        // device is already stopping, or there is no device), and a transport
        // command must not hang the UI thread on that.
        public void FadeOutAndWait()
        {
            _fadeOutComplete.Reset();
            _fadeOutRequested = true;
            _fadeOutComplete.Wait(_timing.FadeOutWaitMs);
        }

        // Cancels a fade-out and arms the declick fade-in, for a resume. The
        // envelope starts from silence either way, so this is safe to call
        // whether or not a fade-out actually completed.
        public void BeginFadeIn()
        {
            _fadeOutRequested = false;
            _fadeOutRemaining = 0;
            _fadeOutLength = 0;
            _fadeOutSignalled = false;
            ArmFadeIn();
            _fadeOutComplete.Set();
        }

        // Processes one device buffer in place. buffer is the full interleaved
        // S16 stereo span miniaudio asked for, already silence-padded past
        // realBytes by the caller; generation is the ring's generation at the
        // moment those bytes were read.
        //
        // The silence padding is processed along with everything else, not
        // skipped: the EQ's delay lines have to keep advancing across a gap or
        // the filter resumes from pre-gap state on the other side, which is a
        // second discontinuity stacked on top of the first.
        public void Process(Span<byte> buffer, int generation)
        {
            var sampleCount = buffer.Length / _bytesPerSample;
            if (sampleCount == 0)
                return;

            var timing = _timing;

            if (!_hasRendered)
            {
                _lastGeneration = generation;
                _hasRendered = true;
                ArmFadeIn(timing);
            }
            else if (generation != _lastGeneration)
            {
                _lastGeneration = generation;
                ArmFadeIn(timing);
            }

            EnsureCapacity(sampleCount);
            var work = _samples.AsSpan(0, sampleCount);

            Widen(buffer, work);
            ApplyEqualizer(work, sampleCount);
            ApplyGainAndEnvelope(work, timing);
            Requantise(work, buffer);
        }

        // Interleaved PCM to float, in native integer units rather than
        // normalised to +-1.
        //
        // Units matter here: everything downstream - the dither's one-LSB
        // triangle, the clamp at full scale, and the "already an exact
        // integer, so do not dither it" test in Requantise - is expressed in
        // LSBs of the destination format, and staying in integer units is what
        // makes all three the same code at 16 bits and at 24. Both formats are
        // exact in a float (a mantissa holds 24 bits), so this direction never
        // loses anything and the round trip at unity gain is bit-identical.
        private void Widen(ReadOnlySpan<byte> source, Span<float> destination)
        {
            if (_sampleFormat == PcmSampleFormat.S16)
            {
                var samples = MemoryMarshal.Cast<byte, short>(source);
                for (var i = 0; i < destination.Length; i++)
                    destination[i] = samples[i];

                return;
            }

            for (var i = 0; i < destination.Length; i++)
            {
                var at = i * 3;
                var value = source[at] | (source[at + 1] << 8) | (source[at + 2] << 16);

                // Sign-extend out of 24 bits. Packed S24 carries no sign bit
                // of its own in the 32-bit sense, so a negative sample read
                // as-is comes back as a large positive one.
                if ((value & 0x00800000) != 0)
                    value |= unchecked((int)0xFF000000);

                destination[i] = value;
            }
        }

        // Runs the EQ, crossfading over exactly this buffer when it changed
        // since the last callback. A fresh Equalizer starts with zeroed delay
        // lines and different coefficients, so dropping it straight in makes
        // the output jump; blending from the old filter's output to the new
        // one's across one buffer (~a few ms) is inaudible, and by the end of
        // it the new filter's state has been driven by real signal.
        private void ApplyEqualizer(Span<float> work, int sampleCount)
        {
            var target = _equalizer;
            var active = _activeEqualizer;

            if (ReferenceEquals(target, active))
            {
                target?.ProcessInPlace(work);
                return;
            }

            var crossfade = _crossfade.AsSpan(0, sampleCount);
            work.CopyTo(crossfade);

            // work becomes the outgoing filter's output, crossfade the
            // incoming one's. Either may be null - a bypass on one side is
            // just the unfiltered signal.
            active?.ProcessInPlace(work);
            target?.ProcessInPlace(crossfade);

            for (var i = 0; i < sampleCount; i++)
            {
                var t = i / (float)sampleCount;
                work[i] += (crossfade[i] - work[i]) * t;
            }

            _activeEqualizer = target;
        }

        // One pass for both gains, because they multiply and there is no
        // reason to walk the buffer twice: the ramp toward the volume target,
        // and the declick/transport envelope on top of it.
        private void ApplyGainAndEnvelope(Span<float> work, AudioTimingSettings timing)
        {
            var frames = work.Length / (int)GaplessFormat.Channels;
            var target = TargetGain;
            var rampFrames = MillisecondsToFrames(timing.GainRampMs);
            if (rampFrames <= 0)
                _currentGain = target;

            var step = rampFrames <= 0 ? 0f : (target - _currentGain) / rampFrames;

            if (_fadeOutRequested && _fadeOutLength == 0)
            {
                _fadeOutLength = Math.Max(1, MillisecondsToFrames(timing.TransportFadeMs));
                _fadeOutRemaining = _fadeOutLength;
                _fadeOutSignalled = false;
                _fadeInRemaining = 0;
            }

            for (var frame = 0; frame < frames; frame++)
            {
                if (_currentGain != target)
                {
                    _currentGain += step;
                    if ((step > 0 && _currentGain > target) || (step < 0 && _currentGain < target))
                        _currentGain = target;
                }

                var envelope = 1f;

                if (_fadeOutLength > 0)
                {
                    envelope = _fadeOutRemaining <= 0 ? 0f : _fadeOutRemaining / (float)_fadeOutLength;
                    if (_fadeOutRemaining > 0)
                        _fadeOutRemaining--;
                }
                else if (_fadeInRemaining > 0)
                {
                    envelope = (_fadeInLength - _fadeInRemaining) / (float)_fadeInLength;
                    _fadeInRemaining--;
                }

                var gain = _currentGain * envelope;
                var offset = frame * (int)GaplessFormat.Channels;
                for (var channel = 0; channel < (int)GaplessFormat.Channels; channel++)
                    work[offset + channel] *= gain;
            }

            // Signalled once, on the transition to silence, so a caller that
            // waited really did get its silence before it stopped the device.
            // Once only because Set() takes a monitor, and this runs on the
            // real-time thread - the same reason the ring buffer's Read() no
            // longer signals anything at all.
            if (_fadeOutLength > 0 && _fadeOutRemaining <= 0 && !_fadeOutSignalled)
            {
                _fadeOutSignalled = true;
                _fadeOutComplete.Set();
            }
        }

        // Float -> the canonical integer format, once, at the end.
        //
        // Deliberately a hard clamp and not a soft-knee limiter, having tried
        // one: the source is already in this format, so a unity-gain pass with
        // no EQ has to come back out bit-identical, and any knee that starts
        // below full scale attenuates real signal to buy headroom nothing here
        // needs. (A knee that starts *at* full scale is arithmetically the
        // same as a clamp - a monotone map that is the identity on [-1,1] and
        // bounded by 1 has nowhere else to go.) Clipping is now reachable only
        // by a deliberate EQ boost past full scale, which is the user asking
        // for it; every internal stage before this one runs in float and can no
        // longer clip on its own, which is the actual fix - the EQ used to
        // round and hard-clamp to S16 itself, mid-chain, with no headroom.
        //
        // Full scale is the destination format's, not a constant. Both are
        // what the widening is worth: the dither is still +-0.5 LSB, but an
        // LSB is 256 times smaller relative to full scale, so the
        // requantisation noise floor drops by 48dB - and a clamp left at
        // +-32767 would have hard-limited every 24-bit sample above -48dBFS.
        private void Requantise(Span<float> work, Span<byte> destination)
        {
            var s16 = _sampleFormat == PcmSampleFormat.S16;
            var shorts = s16 ? MemoryMarshal.Cast<byte, short>(destination) : default;

            for (var i = 0; i < work.Length; i++)
            {
                var value = work[i];

                // Dither only what actually needs rounding. A sample that is
                // already an exact integer is not being requantised - nothing
                // upstream changed it - and adding noise to it would break
                // the transparency the float path is supposed to preserve: at
                // unity gain with no EQ this stage must hand back exactly the
                // S16 it was given. It also keeps digital silence silent, and
                // lets a fade-out actually reach zero.
                if (value != MathF.Round(value))
                {
                    // TPDF: successive rectangular draws differenced, giving a
                    // triangular distribution one LSB wide - the standard
                    // choice, because it decorrelates the quantisation error
                    // from the signal (noise instead of distortion) and leaves
                    // no noise modulation.
                    var dither = NextDither();
                    value += dither - _previousDither;
                    _previousDither = dither;
                }

                var quantised = (int)Math.Clamp(MathF.Round(value), _negativeFullScale, _positiveFullScale);

                if (s16)
                {
                    shorts[i] = (short)quantised;
                    continue;
                }

                var at = i * 3;
                destination[at] = (byte)quantised;
                destination[at + 1] = (byte)(quantised >> 8);
                destination[at + 2] = (byte)(quantised >> 16);
            }
        }

        // xorshift32 - a handful of instructions, no allocation, and plenty
        // of quality for a dither source. Scaled to +-0.5 LSB.
        private float NextDither()
        {
            var x = _ditherState;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _ditherState = x;
            return (x / (float)uint.MaxValue) - 0.5f;
        }

        private void ArmFadeIn() => ArmFadeIn(_timing);

        private void ArmFadeIn(AudioTimingSettings timing)
        {
            _fadeInLength = Math.Max(1, MillisecondsToFrames(timing.DeclickFadeMs));
            _fadeInRemaining = _fadeInLength;
            _fadeOutLength = 0;
            _fadeOutRemaining = 0;
        }

        private int MillisecondsToFrames(int milliseconds) => (int)(milliseconds * _sampleRate / 1000);

        // Grows to whatever miniaudio asks for and never shrinks, so after the
        // first callback or two this allocates nothing. A device period can
        // change size across a reopen (SetOutputDevice), which is why it grows
        // here rather than being sized once at Start.
        private void EnsureCapacity(int sampleCount)
        {
            if (_samples.Length >= sampleCount)
                return;

            _samples = new float[sampleCount];
            _crossfade = new float[sampleCount];
        }
    }
}
