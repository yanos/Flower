using System;

namespace Flower.Manager
{
    // Real-time-safe 10-band graphic EQ, run from OutputStage inside
    // MiniaudioSink's render callback. Pure C# RBJ-cookbook peaking/bell
    // biquads over interleaved stereo float.
    //
    // Float in, float out, and no clipping of its own: it sits in the middle
    // of OutputStage's float path, which requantises to S16 exactly once at
    // the very end, with dither and a soft clip. It used to read and write the
    // S16 buffer directly, rounding and hard-clamping every sample - so a
    // positive band gain clipped outright, with no headroom and no way for a
    // later stage to pull it back.
    //
    // A fresh Equalizer is still built (BuildFrom) on every settings change
    // and swapped in atomically, but the swap is no longer heard: OutputStage
    // crossfades from the outgoing filter's output to the incoming one's
    // across a single callback, rather than dropping in new coefficients on a
    // zeroed delay line. That was previously an accepted click (see
    // AUDIOPHILE-PLAN.md).
    public sealed class Equalizer
    {
        public const int BandCount = 10;

        public static readonly double[] CenterFrequenciesHz =
            { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        private const double MaxGainDb = 12.0;
        private const double QFactor = 1.41; // ~1 octave bandwidth per band, matches the spacing above

        private readonly BiquadCoefficients[] _coefficients;
        private readonly float _preampLinear;

        // Independent delay-line state per channel per band - stereo needs
        // separate z1/z2 per channel even though L and R share the same
        // coefficients (one gain knob affects both channels identically).
        private readonly BiquadState[] _left = new BiquadState[BandCount];
        private readonly BiquadState[] _right = new BiquadState[BandCount];

        private Equalizer(BiquadCoefficients[] coefficients, double preampDb)
        {
            _coefficients = coefficients;
            _preampLinear = (float)Math.Pow(10.0, preampDb / 20.0);
        }

        public static Equalizer BuildFrom(EqualizerSettings settings, uint sampleRateHz)
        {
            var coefficients = new BiquadCoefficients[BandCount];
            for (var i = 0; i < BandCount; i++)
            {
                var gainDb = Math.Clamp(settings.BandGainsDb[i], -MaxGainDb, MaxGainDb);
                coefficients[i] = BiquadCoefficients.PeakingEq(CenterFrequenciesHz[i], QFactor, gainDb, sampleRateHz);
            }

            return new Equalizer(coefficients, Math.Clamp(settings.PreampDb, -MaxGainDb, MaxGainDb));
        }

        // Processes interleaved stereo float PCM in place. Called only from
        // OutputStage on the real-time thread - no locking, no allocation,
        // operates directly on the caller's scratch span.
        //
        // The delay lines carry across calls, so callers must feed it every
        // sample they render, silence-padded underruns included: skipping a
        // silent region leaves the filter resuming from pre-gap state on the
        // other side of it, a second discontinuity on top of the gap.
        public void ProcessInPlace(Span<float> interleavedStereo)
        {
            var frameCount = interleavedStereo.Length / 2;

            for (var frame = 0; frame < frameCount; frame++)
            {
                var l = interleavedStereo[frame * 2] * _preampLinear;
                var r = interleavedStereo[frame * 2 + 1] * _preampLinear;

                for (var band = 0; band < BandCount; band++)
                {
                    l = _coefficients[band].Process(l, ref _left[band]);
                    r = _coefficients[band].Process(r, ref _right[band]);
                }

                interleavedStereo[frame * 2] = l;
                interleavedStereo[frame * 2 + 1] = r;
            }
        }
    }

    // Direct-Form I biquad coefficients (RBJ Audio EQ Cookbook peaking/bell
    // filter), normalized (a0 = 1) at construction so Process() is a plain
    // 5-multiply-4-add per sample with no per-sample division.
    internal readonly struct BiquadCoefficients
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;

        private BiquadCoefficients(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0;
            _b1 = b1;
            _b2 = b2;
            _a1 = a1;
            _a2 = a2;
        }

        public static BiquadCoefficients PeakingEq(double centerFreqHz, double q, double gainDb, uint sampleRateHz)
        {
            var a = Math.Pow(10, gainDb / 40.0);
            var w0 = 2 * Math.PI * centerFreqHz / sampleRateHz;
            var alpha = Math.Sin(w0) / (2 * q);
            var cosw0 = Math.Cos(w0);

            var b0 = 1 + alpha * a;
            var b1 = -2 * cosw0;
            var b2 = 1 - alpha * a;
            var a0 = 1 + alpha / a;
            var a1 = -2 * cosw0;
            var a2 = 1 - alpha / a;

            return new BiquadCoefficients(
                (float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0),
                (float)(a1 / a0), (float)(a2 / a0));
        }

        // Transposed Direct-Form II: y = b0*x + z1; z1' = b1*x - a1*y + z2; z2' = b2*x - a2*y.
        public float Process(float x, ref BiquadState state)
        {
            var y = _b0 * x + state.Z1;
            state.Z1 = _b1 * x - _a1 * y + state.Z2;
            state.Z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    // Transposed Direct-Form II delay-line state - two floats per band per
    // channel, mutated in place by BiquadCoefficients.Process.
    internal struct BiquadState
    {
        public float Z1;
        public float Z2;
    }
}
