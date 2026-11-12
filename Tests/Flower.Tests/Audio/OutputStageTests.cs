using System;
using System.Runtime.InteropServices;

using Flower.Audio;
using Flower.Tests.Decoding.Scenarios;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Audio;

// The render path's arithmetic: EQ, gain ramping, the declick envelope and the
// one dithered requantisation to the device's format. It runs in C, in
// libflowerbridge's render callback, and OutputStageHarness drives that
// callback directly, so none of this needs an audio device.
//
// Written in S16 on both sides - a 16-bit source on a 16-bit device - so a
// sample means the same thing going in as coming out.
//
// These are the assertions the reported symptoms actually reduce to. A click
// is a step discontinuity, so the ramp and envelope tests measure steps
// directly rather than checking that a control was set; quantisation
// distortion is measurable as THD, so the dither test measures that.
public class OutputStageTests
{
    private static AudioTimingSettings Timing(int gainRampMs = 20, int declickMs = 8, int transportMs = 15) => new()
    {
        GainRampMs = gainRampMs,
        DeclickFadeMs = declickMs,
        TransportFadeMs = transportMs,
        FadeOutWaitMs = 0,
        PrebufferMs = 0,
    };

    private static byte[] Sine(double frequencyHz, int frameCount, float amplitude = 0.5f)
    {
        var bytes = new byte[frameCount * DeviceSampleFormat.S16.BytesPerFrame()];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var i = 0; i < frameCount; i++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / PcmFormat.Default.SampleRate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return bytes;
    }

    // A constant, so the only variation in the output is what the stage did to
    // it - any step at all is the stage's doing.
    private static byte[] Constant(short value, int frameCount)
    {
        var bytes = new byte[frameCount * DeviceSampleFormat.S16.BytesPerFrame()];
        MemoryMarshal.Cast<byte, short>(bytes.AsSpan()).Fill(value);
        return bytes;
    }

    // The 16-bit samples in s16, as the pipeline carries them, through the
    // stage, coming back as what an S16 device is handed.
    private static byte[] Render(OutputStageHarness stage, byte[] s16) =>
        stage.Render(MemoryMarshal.Cast<byte, float>(Pcm.FromS16(s16).AsSpan()));

    private static OutputStageHarness Stage(AudioTimingSettings? timing = null)
    {
        var stage = new OutputStageHarness(DeviceSampleFormat.S16, timing ?? Timing());

        // Past the declick fade-in the first audio always gets, so a test
        // measuring something else isn't measuring that.
        Render(stage, Constant(0, 4800));
        return stage;
    }

    // The bar the whole float path has to clear: for an S16 source on an S16
    // device, at unity gain with no EQ and no envelope running, every sample
    // must come back out exactly as it went in. Carrying it as float,
    // filtering, ramping and requantising is only worth doing if it is
    // transparent when it is asked to do nothing - and this is what a
    // soft-knee limiter, which an earlier version of the requantiser had,
    // cannot satisfy.
    [Fact]
    public void At_unity_gain_with_no_processing_the_output_is_bit_identical()
    {
        using var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));

        var input = Sine(997, 4800, amplitude: 1.0f);

        Pcm.AssertBitExact(input, Render(stage, input));
    }

    [Fact]
    public void The_volume_curve_is_perceptual_not_raw_linear_percent()
    {
        Assert.Equal(1f, VolumeCurve.GainForPercent(100));
        Assert.Equal(0f, VolumeCurve.GainForPercent(0));

        // Monotonic, and a long way below the linear percent it replaced -
        // which spent most of the slider's travel in a range the ear barely
        // separates.
        var half = 20 * Math.Log10(VolumeCurve.GainForPercent(50));
        Assert.InRange(half, -19.0, -17.0);
        Assert.True(VolumeCurve.GainForPercent(70) > VolumeCurve.GainForPercent(30));
    }

    // Zipper noise: the master volume used to be a flat per-buffer multiply
    // applied by miniaudio, so a slider drag stepped once per device period -
    // ten audible steps a second at the conservative profile's 100ms.
    [Fact]
    public void A_step_change_in_gain_is_ramped_rather_than_applied_at_once()
    {
        using var stage = Stage(Timing(gainRampMs: 20));
        Render(stage, Constant(10000, 4800));

        stage.TargetGain = 0.1f;
        var output = Render(stage, Constant(10000, 4800)); // 100ms, five times the ramp

        // 20ms to travel 0.9 of full scale on a 10000-amplitude signal is
        // about 9 units per frame; anything near a step would be thousands.
        Pcm.AssertContinuous(output, maxStep: 40, "the gain change was not ramped");

        // And it did arrive: the tail of the buffer is at the new gain.
        Assert.InRange(Pcm.AsSamples(output)[^1], 900, 1100);
    }

    [Fact]
    public void A_flush_fades_the_new_stream_in_from_silence()
    {
        using var stage = Stage();

        // A seek, a manual skip, a fresh start. Without the envelope the first
        // sample of the new stream lands at full scale from silence.
        stage.Flush();
        var output = Render(stage, Constant(short.MaxValue, 4800));

        var samples = Pcm.AsSamples(output);
        Assert.InRange(samples[0], 0, 200);
        Pcm.AssertContinuous(output, maxStep: 200, "the post-flush fade-in was not smooth");

        // Back to the untouched signal well before the end of the buffer -
        // the envelope is 8ms, this is 100ms. Within a dither LSB, since the
        // envelope's last frames are a hair under unity.
        Assert.InRange(samples[^1], short.MaxValue - 2, short.MaxValue);
    }

    // A rebuffer is a gap no flush marks - the output ran dry mid-track - and
    // what comes back after it would otherwise step in at full level.
    [Fact]
    public void Audio_after_a_rebuffer_fades_in_too()
    {
        using var stage = Stage();
        Render(stage, Constant(short.MaxValue, 4800));

        var silence = stage.Rebuffer(480);
        Assert.All(Pcm.AsSamples(silence).ToArray(), sample => Assert.Equal(0, sample));

        var output = Render(stage, Constant(short.MaxValue, 4800));
        Assert.InRange(Pcm.AsSamples(output)[0], 0, 200);
        Pcm.AssertContinuous(output, maxStep: 200, "the post-rebuffer fade-in was not smooth");
    }

    [Fact]
    public void A_fade_out_reaches_exactly_silence_and_stays_there()
    {
        using var stage = Stage(Timing(transportMs: 15));
        Render(stage, Constant(short.MaxValue, 4800));

        stage.BeginFadeOut();
        var output = Render(stage, Constant(short.MaxValue, 4800)); // 100ms, well past the 15ms fade

        Assert.Equal(0, Pcm.AsSamples(output)[^1]);
        Pcm.AssertContinuous(output, maxStep: 200, "the fade-out was not smooth");
        Assert.True(stage.FadeOutCompleted, "the fade-out never said it was done, so a pause would wait out its timeout");

        // Still silent on the next buffer - a stop that faded must not let a
        // sample back through before the device actually stops.
        var after = Render(stage, Constant(short.MaxValue, 480));
        Assert.All(Pcm.AsSamples(after).ToArray(), sample => Assert.Equal(0, sample));
    }

    // Truncation toward zero (miniaudio's `(ma_int16)(x * factor)`) biases
    // every sample downward and correlates the error with the signal, which is
    // audible as distortion rather than noise. Dithered rounding removes the
    // bias; the residual is a flat noise floor.
    [Fact]
    public void Quantisation_is_dithered_rather_than_truncated()
    {
        using var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 0.5f;

        var output = Render(stage, Sine(1000, 48000, amplitude: 0.9f));

        // A clean gain stage leaves a 1kHz tone a 1kHz tone. Truncation at
        // this level lifts THD by more than an order of magnitude over this.
        Assert.True(Pcm.Thd(output, 1000) < 0.002, $"THD was {Pcm.Thd(output, 1000):P3}");
    }

    [Fact]
    public void Dither_stays_within_one_lsb()
    {
        // Exactly representable at this gain, so any deviation is the dither
        // and nothing else.
        using var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));

        foreach (var sample in Pcm.AsSamples(Render(stage, Constant(1000, 4800))))
            Assert.InRange(sample, 999, 1001);
    }

    // Swapping the EQ used to drop in new coefficients on a zeroed delay line,
    // documented as an accepted click. Crossfading across one buffer makes the
    // change continuous instead.
    [Fact]
    public void An_equalizer_swap_is_crossfaded_rather_than_stepped()
    {
        var settings = new EqualizerSettings
        {
            Enabled = true,
            PreampDb = 0,
            BandGainsDb = new double[Equalizer.BandCount],
        };
        settings.BandGainsDb[Array.IndexOf(Equalizer.CenterFrequenciesHz, 1000.0)] = 12;

        using var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        Render(stage, Sine(1000, 4800, amplitude: 0.4f));
        stage.Equalizer = Equalizer.BuildFrom(settings, PcmFormat.Default.SampleRate);
        var swapped = Render(stage, Sine(1000, 4800, amplitude: 0.4f));

        // Compared against the same signal through a stage that has been
        // running the boosted curve all along, rather than an absolute step
        // limit: a 1kHz tone boosted 12dB has a steep natural slope of its
        // own, and the question is only whether the swap added anything to it.
        using var reference = Stage(Timing(gainRampMs: 0, declickMs: 0));
        reference.Equalizer = Equalizer.BuildFrom(settings, PcmFormat.Default.SampleRate);
        Render(reference, Sine(1000, 4800, amplitude: 0.4f));
        var settled = Render(reference, Sine(1000, 4800, amplitude: 0.4f));

        var limit = (int)(Pcm.MaxStep(settled) * 1.2);
        Pcm.AssertContinuous(swapped, limit, "the equalizer swap stepped instead of crossfading");
    }
}
