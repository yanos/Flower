using System;
using System.Runtime.InteropServices;

using Flower.Audio;
using Flower.Tests.Decoding.Scenarios;

using Miniaudio;

namespace Flower.Tests.Audio;

// The canonical PCM format, float from the decoder to the output stage, and
// the one conversion out of it: requantisation to whichever integer format
// the device was opened in, in libflowerbridge's render callback.
//
// What float has to be held to is that it costs an integer source nothing.
// A 16- or 24-bit sample is exact in a float's 24-bit mantissa, and scaling it
// by a power of two is an exponent change, so a unity-gain pass must hand back
// the integers that went in - at 24 bits too, which is the widest integer
// format that survives this exactly, and why DeviceSampleFormat stops there.
// OutputStageTests holds the S16 path to the same bar.
//
// No device and no decoder: OutputStageHarness calls the callback directly.
public class CanonicalFormatTests
{
    private const int Channels = (int)GaplessFormat.Channels;

    // Deliberately not derived from DeviceSampleFormat: the point of these is
    // to fail if the layout ever stops being packed three-byte little-endian,
    // which is what miniaudio's ma_format_s24 means by S24.
    private const int S24Max = 8_388_607;
    private const int S24Min = -8_388_608;

    private static AudioTimingSettings Silent() => new()
    {
        GainRampMs = 0,
        DeclickFadeMs = 0,
        TransportFadeMs = 0,
        FadeOutWaitMs = 0,
        PrebufferMs = 0,
    };

    // An integer source of the given width, as the decoder hands it to the
    // pipeline: normalised by 2^(bits-1), exactly as FFmpeg converts it.
    private static float[] Normalised(ReadOnlySpan<int> samples, int bits)
    {
        var scale = (float)(1 << (bits - 1));
        var pcm = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            pcm[i] = samples[i] / scale;

        return pcm;
    }

    // Past the declick fade-in that the first audio rendered gets, so a test
    // measuring something else is not measuring that.
    private static OutputStageHarness Stage(DeviceSampleFormat format)
    {
        var stage = new OutputStageHarness(format, Silent());
        stage.RenderNothing(4800);
        return stage;
    }

    private static int[] Render(OutputStageHarness stage, float[] pcm) =>
        OutputStageHarness.Unpack(stage.Format, stage.Render(pcm));

    [Fact]
    public void At_24_bits_a_unity_gain_pass_is_bit_identical()
    {
        using var stage = Stage(DeviceSampleFormat.S24);

        var samples = new int[4800 * Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            // A ramp that reaches full scale in both directions and carries
            // meaningful data in its bottom eight bits - the bits that only
            // exist at all above 16.
            samples[i] = (int)(S24Max * Math.Sin(2 * Math.PI * 997 * i / (double)samples.Length)) + (i % 251) - 125;
            samples[i] = Math.Clamp(samples[i], S24Min, S24Max);
        }

        Assert.Equal(samples, Render(stage, Normalised(samples, 24)));
    }

    // The classic packed-S24 bug: three bytes carry no sign bit in the 32-bit
    // sense, so a negative sample written or read without care comes back as
    // a large positive one - full-scale positive noise in place of quiet
    // audio. Full scale itself is here too: -1.0 is exactly S24Min, and the
    // largest positive sample is one LSB short of +1.0.
    [Fact]
    public void Negative_24_bit_samples_survive_the_round_trip()
    {
        using var stage = Stage(DeviceSampleFormat.S24);

        int[] samples = [S24Min, -1, -256, -8_388_607, 0, 1, 256, S24Max];

        Assert.Equal(samples, Render(stage, Normalised(samples, 24)));
    }

    // What 24 bits are actually for, stated as the thing a listener would
    // notice: signal quiet enough to fall off the bottom of 16 bits is still
    // there at 24.
    //
    // -96dBFS is one LSB of S16 - a tone at that level is a square wave of
    // +-1 count at best, and anything below it is silence. The same tone at 24
    // bits is a couple of hundred counts of real waveform.
    [Fact]
    public void A_tone_below_the_16_bit_noise_floor_survives_at_24()
    {
        using var stage = Stage(DeviceSampleFormat.S24);

        const double amplitude = S24Max / 65536.0; // -96dBFS, in 24-bit counts
        var samples = new int[2400 * Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * 440 * (i / Channels) / 48000.0));

        var output = Render(stage, Normalised(samples, 24));
        Assert.Equal(samples, output);

        // And it is a real waveform rather than a couple of counts of
        // dither - roughly +-128, which at 16 bits would all round to zero.
        var peak = 0;
        foreach (var sample in output)
            peak = Math.Max(peak, Math.Abs(sample));

        Assert.InRange(peak, 100, 130);
        Assert.True(peak / 256 <= 1, "the same peak is at most one LSB once narrowed to 16 bits");
    }

    // Clipping has to happen at the destination format's full scale, not at a
    // constant. A clamp left at +-32767 would hard-limit every 24-bit sample
    // above -48dBFS - the widening would make the output worse, not better.
    //
    // Reached the only way it can be reached, since nothing before the
    // requantiser can clip on its own and the gain is clamped to unity: an
    // EQ boost, which is the user asking for it.
    [Fact]
    public void A_boost_past_full_scale_clamps_at_24_bit_full_scale()
    {
        var boosted = new EqualizerSettings { Enabled = true };
        Array.Fill(boosted.BandGainsDb, 12.0);

        using var stage = Stage(DeviceSampleFormat.S24);
        stage.Equalizer = Equalizer.BuildFrom(boosted, PcmFormat.Default.SampleRate);

        var samples = new int[4800 * Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (int)(S24Max * 0.95 * Math.Sin(2 * Math.PI * 997 * (i / Channels) / 48000.0));

        var output = Render(stage, Normalised(samples, 24));
        var peak = 0;
        foreach (var sample in output)
            peak = Math.Max(peak, Math.Abs(sample));

        // It clipped - so the clamp is doing something - and it clipped at 24
        // bits rather than at 16.
        Assert.Equal(S24Max + 1, peak);
        foreach (var sample in output)
            Assert.InRange(sample, S24Min, S24Max);
    }

    // The requantisation noise floor moves down with the format. Both widths
    // land within one and a half counts of the exact value - a half from the
    // rounding and a whole from the TPDF dither's one-LSB triangle, which is
    // the noise it trades the distortion for - but a count is 256 times
    // smaller at 24 bits, which is the whole of what a wider device buys.
    //
    // Parameterised over both formats rather than written for the new one:
    // the arithmetic is shared, so the assertion that S16 still behaves is
    // as much the point as the assertion that S24 does.
    [Theory]
    [InlineData(DeviceSampleFormat.S16, 32767)]
    [InlineData(DeviceSampleFormat.S24, S24Max)]
    public void Requantisation_error_is_at_most_one_lsb_of_the_destination_format(DeviceSampleFormat format, int fullScale)
    {
        using var stage = Stage(format);

        // A gain that cannot be represented as an exact division, so every
        // sample genuinely needs rounding rather than passing through the
        // "already an integer" shortcut.
        stage.TargetGain = 0.3f;

        var count = 4800 * Channels;
        var source = new int[count];
        for (var i = 0; i < count; i++)
            source[i] = (int)(fullScale * 0.9 * Math.Sin(2 * Math.PI * 997 * (i / Channels) / 48000.0));

        var bits = format.BytesPerSample() * 8;
        var output = Render(stage, Normalised(source, bits));

        var worst = 0.0;
        for (var i = 0; i < count; i++)
            worst = Math.Max(worst, Math.Abs(output[i] - source[i] * 0.3f));

        Assert.True(worst <= 1.5, $"worst requantisation error was {worst} counts");
    }

    // A device that refused 24 bits, playing a 24-bit file. The decoder no
    // longer knows what the device took, so the narrowing is the output stage's,
    // and it has to be a requantisation - dithered, within the same one and a
    // half counts - rather than the low byte simply dropped, which is a
    // truncation: biased downward by half an LSB and correlated with the
    // signal, i.e. distortion.
    [Fact]
    public void A_24_bit_source_on_a_16_bit_device_is_requantised_rather_than_truncated()
    {
        using var stage = Stage(DeviceSampleFormat.S16);

        var count = 4800 * Channels;
        var source = new int[count];
        for (var i = 0; i < count; i++)
            source[i] = (int)(S24Max * 0.5 * Math.Sin(2 * Math.PI * 997 * (i / Channels) / 48000.0)) + (i % 251) - 125;

        var output = Render(stage, Normalised(source, 24));

        var worst = 0.0;
        var bias = 0.0;
        for (var i = 0; i < count; i++)
        {
            var error = output[i] - source[i] / 256.0;
            worst = Math.Max(worst, Math.Abs(error));
            bias += error;
        }

        Assert.True(worst <= 1.5, $"worst requantisation error was {worst} counts");

        // A truncation's error averages about -0.5 counts; a rounding with
        // TPDF dither averages zero.
        Assert.InRange(bias / count, -0.1, 0.1);
    }

    // Packed three-byte little-endian on both sides of the P/Invoke, so
    // miniaudio converts nothing between the callback's buffer and the device.
    // The mapping is one line and exactly the kind of line that gets
    // "simplified".
    [Fact]
    public void The_device_format_maps_onto_miniaudio_without_conversion()
    {
        Assert.Equal(ma_format.ma_format_s16, MiniaudioSink.MiniaudioFormatFor(DeviceSampleFormat.S16));
        Assert.Equal(ma_format.ma_format_s24, MiniaudioSink.MiniaudioFormatFor(DeviceSampleFormat.S24));
    }
}
