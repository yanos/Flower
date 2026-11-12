using System;
using System.Runtime.InteropServices;

using Flower.Audio;

using Miniaudio;

namespace Flower.Tests.Decoding.Scenarios;

// The output stage Flower runs - the C in libflowerbridge's render callback -
// driven from a test, with no audio device. Float frames in, the bytes the
// device would be handed out.
//
// The bridge uses the ma_device pointer only as a key to find itself and
// never dereferences it, so the callback is called directly, with a dummy
// one. What runs is the compiled callback in the library the app ships, on
// the machine's own CPU: FlowerBridge.props's build on a desktop, the
// framework on the iOS simulator, the APK's .so on Android.
//
// Fed and controlled the way MiniaudioSink and AudioFeeder feed and control
// it: the same calls, in the same order, so a test of this is a test of what
// plays. Each Render is one device callback for all its frames; past the
// bridge's 4096-frame chunk the C processes it in pieces, which only moves
// where an EQ crossfade ends.
public sealed unsafe class OutputStageHarness : IDisposable
{
    private readonly NativeAudioBridge _bridge;
    private readonly ma_device* _device;

    // Two seconds of 48kHz audio fits whole, more than any test hands it at
    // once.
    public const int DefaultCapacityFrames = 1 << 17;

    // Primed from the start, as the feeder primes a bridge once its prebuffer
    // is in. The first primed callback arms the declick, so the first
    // DeclickFadeMs rendered fade in: a test measuring something else renders
    // past it first.
    public OutputStageHarness(
        DeviceSampleFormat format,
        AudioTimingSettings? timing = null,
        uint sampleRate = 48000,
        int capacityFrames = DefaultCapacityFrames)
    {
        if (!NativeAudioBridge.IsAvailable)
            throw new InvalidOperationException(
                $"no {NativeAudioBridge.LibraryName} library loaded, or one older than this build - it is the only output stage there is");

        Format = format;
        SampleRate = sampleRate;
        Timing = (timing ?? new AudioTimingSettings()).Clamped();
        _bridge = NativeAudioBridge.TryCreate(capacityFrames, format, sampleRate)
            ?? throw new InvalidOperationException($"flower_audio_bridge_create refused {capacityFrames} frames at {format}, {sampleRate}Hz");

        _device = (ma_device*)NativeMemory.AllocZeroed(1);
        if (!_bridge.AttachTo(_device))
        {
            _bridge.Dispose();
            NativeMemory.Free(_device);
            throw new InvalidOperationException("flower_audio_bridge_attach found every slot taken");
        }

        // As MiniaudioSink.PushTiming converts them.
        _bridge.SetTiming(Frames(Timing.GainRampMs), Frames(Timing.DeclickFadeMs));
        _bridge.SetPrimed(true);
    }

    public DeviceSampleFormat Format { get; }

    public uint SampleRate { get; }

    public AudioTimingSettings Timing { get; }

    // Linear, 0..1, ramped to over GainRampMs.
    public float TargetGain
    {
        set => _bridge.SetTargetGain(value);
    }

    // null is a true bypass. A change is crossfaded in over the next buffer.
    public Equalizer? Equalizer
    {
        set => _bridge.SetEqualizer(value);
    }

    // Queues the interleaved stereo frames and renders exactly that many in
    // one callback, so the output is those frames through the stage and
    // nothing else.
    public byte[] Render(ReadOnlySpan<float> interleaved)
    {
        var bytes = MemoryMarshal.AsBytes(interleaved);
        var written = _bridge.Write(bytes);
        if (written != bytes.Length)
            throw new InvalidOperationException($"the bridge took {written} of {bytes.Length} bytes");

        return Call(interleaved.Length / (int)GaplessFormat.Channels);
    }

    // A callback with nothing queued: silence padding, run through the stage
    // like any other, as a starved callback is.
    public byte[] RenderNothing(int frames) => Call(frames);

    // A seek or a skip, as AudioFeeder carries one: the request un-primes the
    // bridge, the flush drops what was queued, and the feeder primes it again
    // once audio from the far side is in. What renders next fades in.
    public void Flush()
    {
        _bridge.RequestFlush();
        _bridge.FlushNow();
        _bridge.SetPrimed(true);
    }

    // The output ran dry and is waiting for enough to play through, which no
    // flush marks: silence while un-primed, and a fade-in after.
    public byte[] Rebuffer(int silentFrames)
    {
        _bridge.SetPrimed(false);
        var silence = Call(silentFrames);
        _bridge.SetPrimed(true);
        return silence;
    }

    // A pause or a stop, which MiniaudioSink waits out before stopping the
    // device: the transport envelope walks down over TransportFadeMs.
    public void BeginFadeOut() => _bridge.BeginFadeOut(Frames(Timing.TransportFadeMs));

    public bool FadeOutCompleted => _bridge.FadeOutCompleted;

    public AudioCallbackTimingSnapshot TakeTiming() => _bridge.TakeSnapshot().Timing;

    public void ResetTiming() => _bridge.ResetTiming();

    // Pre-filled with a pattern no stage writes as a whole buffer, so a
    // callback that did nothing at all is told apart from one that rendered.
    private byte[] Call(int frames)
    {
        var output = new byte[frames * Format.BytesPerFrame()];
        output.AsSpan().Fill(0xA5);
        fixed (byte* destination = output)
        {
            NativeAudioBridge.RenderCallback(_device, destination, null, (uint)frames);
        }

        if (frames > 0 && output.AsSpan().IndexOfAnyExcept((byte)0xA5) < 0)
            throw new InvalidOperationException("the callback wrote nothing: the bridge did not find itself by its device pointer");

        return output;
    }

    private int Frames(int milliseconds) => (int)(milliseconds * (long)SampleRate / 1000);

    // Unpacks the device's bytes into integers, sign-extending S24.
    public static int[] Unpack(DeviceSampleFormat format, ReadOnlySpan<byte> bytes)
    {
        if (format == DeviceSampleFormat.S16)
        {
            var shorts = MemoryMarshal.Cast<byte, short>(bytes);
            var widened = new int[shorts.Length];
            for (var i = 0; i < shorts.Length; i++)
                widened[i] = shorts[i];

            return widened;
        }

        var samples = new int[bytes.Length / 3];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | ((sbyte)bytes[i * 3 + 2] << 16);

        return samples;
    }

    public void Dispose()
    {
        _bridge.Dispose();
        NativeMemory.Free(_device);
    }
}
