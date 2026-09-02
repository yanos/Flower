using System;
using System.Runtime.InteropServices;

using Flower.Audio;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The render path's arithmetic: float widening, EQ, gain ramping, the declick
// envelope and the one dithered requantisation back to S16. Everything here is
// pure math over spans, so none of it needs an audio device.
//
// These are the assertions the reported symptoms actually reduce to. A click
// is a step discontinuity, so the ramp and envelope tests measure steps
// directly rather than checking that a method was called; quantisation
// distortion is measurable as THD, so the dither test measures that.
public class OutputStageTests
{
    private const int Channels = (int)GaplessFormat.Channels;

    private static AudioTimingSettings Timing(
        int gainRampMs = 20, int declickMs = 8, int transportMs = 15, int fadeOutWaitMs = 0) => new()
        {
            GainRampMs = gainRampMs,
            DeclickFadeMs = declickMs,
            TransportFadeMs = transportMs,
            FadeOutWaitMs = fadeOutWaitMs,
            PrebufferMs = 0,
        };

    private static byte[] Sine(double frequencyHz, int frameCount, float amplitude = 0.5f)
    {
        var bytes = new byte[frameCount * GaplessFormat.BytesPerFrame];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var i = 0; i < frameCount; i++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / GaplessFormat.SampleRate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return bytes;
    }

    // A constant, so the only variation in the output is what the stage did to
    // it - any step at all is the stage's doing.
    private static byte[] Constant(short value, int frameCount)
    {
        var bytes = new byte[frameCount * GaplessFormat.BytesPerFrame];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        samples.Fill(value);
        return bytes;
    }

    private static OutputStage Stage(AudioTimingSettings? timing = null)
    {
        var stage = new OutputStage(GaplessFormat.SampleRate) { Timing = timing ?? Timing() };

        // Past the declick fade-in the first buffer of a new generation always
        // gets, so a test measuring something else isn't measuring that.
        var warmup = Constant(0, 4800);
        stage.Process(warmup, generation: 1);
        return stage;
    }

    // The bar the whole float path has to clear: the source is already S16,
    // so at unity gain with no EQ and no envelope running, every sample must
    // come back out exactly as it went in. Widening to float, filtering,
    // ramping and requantising is only worth doing if it is transparent when
    // it is asked to do nothing - and this is what a soft-knee limiter, which
    // an earlier version of Requantise had, cannot satisfy.
    [Fact]
    public void At_unity_gain_with_no_processing_the_output_is_bit_identical()
    {
        var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 1f;

        var input = Sine(997, 4800, amplitude: 1.0f);
        var buffer = (byte[])input.Clone();
        stage.Process(buffer, generation: 1);

        Pcm.AssertBitExact(input, buffer);
    }

    [Fact]
    public void The_volume_curve_is_perceptual_not_raw_linear_percent()
    {
        Assert.Equal(1f, OutputStage.GainForVolumePercent(100));
        Assert.Equal(0f, OutputStage.GainForVolumePercent(0));

        // Monotonic, and a long way below the linear percent it replaced -
        // which spent most of the slider's travel in a range the ear barely
        // separates.
        var half = 20 * Math.Log10(OutputStage.GainForVolumePercent(50));
        Assert.InRange(half, -19.0, -17.0);
        Assert.True(OutputStage.GainForVolumePercent(70) > OutputStage.GainForVolumePercent(30));
    }

    // Zipper noise: the master volume used to be a flat per-buffer multiply
    // applied by miniaudio, so a slider drag stepped once per device period -
    // ten audible steps a second at the conservative profile's 100ms.
    [Fact]
    public void A_step_change_in_gain_is_ramped_rather_than_applied_at_once()
    {
        var stage = Stage(Timing(gainRampMs: 20));
        stage.TargetGain = 1f;

        var settle = Constant(10000, 4800);
        stage.Process(settle, generation: 1);

        stage.TargetGain = 0.1f;
        var buffer = Constant(10000, 4800); // 100ms, five times the ramp
        stage.Process(buffer, generation: 1);

        // 20ms to travel 0.9 of full scale on a 10000-amplitude signal is
        // about 9 units per frame; anything near a step would be thousands.
        Pcm.AssertContinuous(buffer, maxStep: 40, "the gain change was not ramped");

        // And it did arrive: the tail of the buffer is at the new gain.
        var samples = Pcm.AsSamples(buffer);
        Assert.InRange(samples[^1], 900, 1100);
    }

    [Fact]
    public void A_flush_fades_the_new_stream_in_from_silence()
    {
        var stage = Stage();
        stage.TargetGain = 1f;

        var buffer = Constant(short.MaxValue, 4800);

        // A different generation is what a flush looks like to the callback -
        // a seek, a manual skip, a fresh start. Without the envelope the first
        // sample of the new stream lands at full scale from silence.
        stage.Process(buffer, generation: 2);

        var samples = Pcm.AsSamples(buffer);
        Assert.InRange(samples[0], 0, 200);
        Pcm.AssertContinuous(buffer, maxStep: 200, "the post-flush fade-in was not smooth");

        // Back to the untouched signal well before the end of the buffer -
        // the envelope is 8ms, this is 100ms. Within a dither LSB, since the
        // envelope's last frames are a hair under unity.
        Assert.InRange(samples[^1], short.MaxValue - 2, short.MaxValue);
    }

    [Fact]
    public void A_fade_out_reaches_exactly_silence_and_stays_there()
    {
        var stage = Stage(Timing(transportMs: 15, fadeOutWaitMs: 0));
        stage.TargetGain = 1f;

        var settle = Constant(short.MaxValue, 4800);
        stage.Process(settle, generation: 1);

        stage.FadeOutAndWait();

        var buffer = Constant(short.MaxValue, 4800); // 100ms, well past the 15ms fade
        stage.Process(buffer, generation: 1);

        var samples = Pcm.AsSamples(buffer);
        Assert.Equal(0, samples[^1]);
        Pcm.AssertContinuous(buffer, maxStep: 200, "the fade-out was not smooth");

        // Still silent on the next buffer - a stop that faded must not let a
        // sample back through before the device actually stops.
        var after = Constant(short.MaxValue, 480);
        stage.Process(after, generation: 1);
        Assert.All(Pcm.AsSamples(after).ToArray(), sample => Assert.Equal(0, sample));
    }

    // Truncation toward zero (miniaudio's `(ma_int16)(x * factor)`) biases
    // every sample downward and correlates the error with the signal, which is
    // audible as distortion rather than noise. Dithered rounding removes the
    // bias; the residual is a flat noise floor.
    [Fact]
    public void Quantisation_is_dithered_rather_than_truncated()
    {
        var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 0.5f;

        var buffer = Sine(1000, 48000, amplitude: 0.9f);
        stage.Process(buffer, generation: 1);

        // A clean gain stage leaves a 1kHz tone a 1kHz tone. Truncation at
        // this level lifts THD by more than an order of magnitude over this.
        Assert.True(Pcm.Thd(buffer, 1000) < 0.002, $"THD was {Pcm.Thd(buffer, 1000):P3}");
    }

    [Fact]
    public void Dither_stays_within_one_lsb()
    {
        // Exactly representable at this gain, so any deviation is the dither
        // and nothing else.
        var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 1f;

        var buffer = Constant(1000, 4800);
        stage.Process(buffer, generation: 1);

        foreach (var sample in Pcm.AsSamples(buffer))
            Assert.InRange(sample, 999, 1001);
    }

    // The real-time contract: miniaudio's data callback runs on a thread the
    // GC must not have to stop, so the steady state has to be allocation-free.
    // The float scratch buffers are grow-only, so only the first callback at a
    // given size allocates.
    [Fact]
    public void Steady_state_processing_allocates_nothing()
    {
        var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 0.75f;
        stage.Equalizer = Equalizer.BuildFrom(
            new EqualizerSettings { Enabled = true, PreampDb = 3, BandGainsDb = new double[Equalizer.BandCount] },
            GaplessFormat.SampleRate);

        var buffer = Sine(1000, 480);
        for (var i = 0; i < 4; i++)
            stage.Process(buffer, generation: 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
            stage.Process(buffer, generation: 1);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Swapping the EQ used to drop in new coefficients on a zeroed delay line,
    // documented as an accepted click. Crossfading across one buffer makes the
    // change continuous instead.
    [Fact]
    public void An_equalizer_swap_is_crossfaded_rather_than_stepped()
    {
        var stage = Stage(Timing(gainRampMs: 0, declickMs: 0));
        stage.TargetGain = 1f;

        var settings = new EqualizerSettings
        {
            Enabled = true,
            PreampDb = 0,
            BandGainsDb = new double[Equalizer.BandCount],
        };
        settings.BandGainsDb[Array.IndexOf(Equalizer.CenterFrequenciesHz, 1000.0)] = 12;

        var buffer = Sine(1000, 4800, amplitude: 0.4f);
        stage.Process(buffer, generation: 1);

        stage.Equalizer = Equalizer.BuildFrom(settings, GaplessFormat.SampleRate);

        var swapped = Sine(1000, 4800, amplitude: 0.4f);
        stage.Process(swapped, generation: 1);

        // Compared against the same signal through a stage that has been
        // running the boosted curve all along, rather than an absolute step
        // limit: a 1kHz tone boosted 12dB has a steep natural slope of its
        // own, and the question is only whether the swap added anything to it.
        var reference = Stage(Timing(gainRampMs: 0, declickMs: 0));
        reference.TargetGain = 1f;
        reference.Equalizer = Equalizer.BuildFrom(settings, GaplessFormat.SampleRate);
        reference.Process(Sine(1000, 4800, amplitude: 0.4f), generation: 1);
        var settled = Sine(1000, 4800, amplitude: 0.4f);
        reference.Process(settled, generation: 1);

        var limit = (int)(Pcm.MaxStep(settled) * 1.2);
        Pcm.AssertContinuous(swapped, limit, "the equalizer swap stepped instead of crossfading");
    }
}
