using System;

namespace Flower.Audio
{
    // A 10-band graphic EQ curve: RBJ-cookbook peaking/bell biquads, one per
    // band, plus a preamp. This computes the coefficients; the filter itself
    // runs in C, in the bridge's render callback (flower_audio_bridge.c, "The
    // output stage"), over interleaved stereo float with no clipping of its
    // own - the one requantisation to the device's format clamps, at the end.
    //
    // A fresh Equalizer is built on every settings change and handed to the
    // bridge whole. The swap is not heard: the callback crossfades from the
    // outgoing filter's output to the incoming one's across a single buffer,
    // rather than dropping in new coefficients on a zeroed delay line, which
    // was once an accepted click (AUDIO-QUALITY-PLAN.md, audiophile feature
    // §1).
    public sealed class Equalizer
    {
        public const int BandCount = 10;

        public static readonly double[] CenterFrequenciesHz =
            { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        private const double MaxGainDb = 12.0;
        private const double QFactor = 1.41; // ~1 octave bandwidth per band, matches the spacing above

        // A band is left out when its centre is this close to Nyquist. At or
        // past it the peaking formula's w0 reaches pi and the biquad's poles
        // land on or outside the unit circle: at 22050 Hz the 16 kHz band
        // diverged by forty-three orders of magnitude in nine milliseconds,
        // which the requantiser's clamp turned into full-scale noise. A device can
        // open at 22050 or 32000 (an Android route, an ALSA setting), and a
        // band whose frequency the device cannot carry has nothing to shape
        // anyway - so it is bypassed, not squeezed under the limit.
        private const double MaxCenterToSampleRate = 0.45;

        private readonly BiquadCoefficients[] _coefficients;
        private readonly float _preampLinear;

        private Equalizer(BiquadCoefficients[] coefficients, double preampDb)
        {
            _coefficients = coefficients;
            _preampLinear = (float)Math.Pow(10.0, preampDb / 20.0);
        }

        public static Equalizer BuildFrom(EqualizerSettings settings, uint sampleRateHz)
        {
            var coefficients = new BiquadCoefficients[UsableBandCount(sampleRateHz)];
            for (var i = 0; i < coefficients.Length; i++)
            {
                var gainDb = Math.Clamp(settings.BandGainsDb[i], -MaxGainDb, MaxGainDb);
                coefficients[i] = BiquadCoefficients.PeakingEq(CenterFrequenciesHz[i], QFactor, gainDb, sampleRateHz);
            }

            return new Equalizer(coefficients, Math.Clamp(settings.PreampDb, -MaxGainDb, MaxGainDb));
        }

        // The bands are in ascending order, so the usable ones are a prefix.
        private static int UsableBandCount(uint sampleRateHz)
        {
            var count = 0;
            while (count < BandCount && CenterFrequenciesHz[count] < MaxCenterToSampleRate * sampleRateHz)
                count++;
            return count;
        }

        // The curve as flower_audio_bridge_set_equalizer takes it: five
        // coefficients per band in b0 b1 b2 a1 a2 order, and the preamp as
        // linear gain. Bands too close to Nyquist are left out, so the active
        // ones are a prefix of the ten.
        internal int ActiveBandCount => _coefficients.Length;

        internal float PreampLinear => _preampLinear;

        internal const int CoefficientsPerBand = 5;

        internal void CopyCoefficientsTo(Span<float> destination)
        {
            for (var band = 0; band < _coefficients.Length; band++)
                _coefficients[band].CopyTo(destination.Slice(band * CoefficientsPerBand, CoefficientsPerBand));
        }
    }

    // Biquad coefficients (RBJ Audio EQ Cookbook peaking/bell filter),
    // normalized (a0 = 1) at construction so the filter that runs them is a
    // plain 5-multiply-4-add per sample with no per-sample division.
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

        public void CopyTo(Span<float> destination)
        {
            destination[0] = _b0;
            destination[1] = _b1;
            destination[2] = _b2;
            destination[3] = _a1;
            destination[4] = _a2;
        }
    }
}
