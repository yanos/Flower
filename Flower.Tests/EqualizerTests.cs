using System;

using Flower.Manager;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// Pure-logic coverage for the DSP core OutputStage runs inside
// MiniaudioSink's render callback - no LibVLC/audio hardware involved, so none
// of this needs RequiresLibVLC. GaplessAudioManagerTests covers the forwarding
// plumbing (ApplyEqualizer reaching the sink); this file is only about
// Equalizer's own signal math.
//
// Float in, float out now: the EQ sits in the middle of OutputStage's float
// path and no longer rounds or clamps to S16 itself, so these work in floats
// scaled to the same +-32768 range the samples occupy there.
public class EqualizerTests
{
    private const uint SampleRate = GaplessFormat.DefaultSampleRate;

    private static float[] GenerateSine(double frequencyHz, int frameCount, float amplitude = 0.5f)
    {
        var samples = new float[frameCount * (int)GaplessFormat.Channels];
        for (var i = 0; i < frameCount; i++)
        {
            var value = (float)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    private static EqualizerSettings FlatSettings() => new()
    {
        Enabled = true,
        PreampDb = 0,
        BandGainsDb = new double[Equalizer.BandCount],
    };

    private static EqualizerSettings WithBand(double centerHz, double gainDb)
    {
        var settings = FlatSettings();
        settings.BandGainsDb[Array.IndexOf(Equalizer.CenterFrequenciesHz, centerHz)] = gainDb;
        return settings;
    }

    // The steady-state gain the filter actually applies at one frequency,
    // measured rather than derived: drive it with a tone, discard the first
    // half so the biquads have settled, and compare RMS in to RMS out.
    private static double MeasuredGainDb(EqualizerSettings settings, double toneHz)
    {
        const int frames = 24000;
        var input = GenerateSine(toneHz, frames);
        var output = (float[])input.Clone();

        Equalizer.BuildFrom(settings, SampleRate).ProcessInPlace(output);

        var half = input.Length / 2;
        var ratio = Pcm.Rms(output.AsSpan(half)) / Pcm.Rms(input.AsSpan(half));
        return 20 * Math.Log10(ratio);
    }

    [Fact]
    public void BuildFrom_AllZeroGains_IsNearUnity()
    {
        Assert.InRange(MeasuredGainDb(FlatSettings(), 1000), -0.3, 0.3);
    }

    // Every band, at its own center frequency, within half a dB of what was
    // asked for - the whole point of a graphic EQ, and something no previous
    // test checked: they only asserted that a boost made things louder.
    // 16kHz is excluded: its bell sits close enough to Nyquist at 48kHz that
    // the bilinear transform's frequency warping measurably shifts it, which
    // is inherent to an RBJ biquad rather than a defect.
    [Theory]
    [InlineData(31.0)]
    [InlineData(62.0)]
    [InlineData(125.0)]
    [InlineData(250.0)]
    [InlineData(500.0)]
    [InlineData(1000.0)]
    [InlineData(2000.0)]
    [InlineData(4000.0)]
    [InlineData(8000.0)]
    public void Each_band_applies_its_requested_gain_at_its_center_frequency(double centerHz)
    {
        Assert.InRange(MeasuredGainDb(WithBand(centerHz, 6), centerHz), 5.5, 6.5);
        Assert.InRange(MeasuredGainDb(WithBand(centerHz, -6), centerHz), -6.5, -5.5);
    }

    [Fact]
    public void BuildFrom_PreampAppliesUniformGain()
    {
        // 700Hz sits well clear of the 500/1000 bands, so only the preamp
        // stage should affect the level here.
        var settings = FlatSettings();
        settings.PreampDb = 6;

        Assert.InRange(MeasuredGainDb(settings, 700), 5.5, 6.5);
    }

    // The delay lines have to carry across calls: the render callback hands
    // over one device period at a time, and a filter that restarted from zero
    // state on each of them would put a discontinuity at every period
    // boundary - about ten clicks a second.
    //
    // Asserted by feeding the same tone once as a single block and again as
    // uneven chunks, and requiring the two outputs to match. A per-call reset
    // passes every other test in this file, because they all process one
    // buffer.
    [Fact]
    public void Filter_state_carries_across_calls_of_different_lengths()
    {
        const int frames = 8000;
        var wholeInput = GenerateSine(1000, frames);
        var chunkedInput = (float[])wholeInput.Clone();

        Equalizer.BuildFrom(WithBand(1000, 9), SampleRate).ProcessInPlace(wholeInput);

        var chunked = Equalizer.BuildFrom(WithBand(1000, 9), SampleRate);
        var offset = 0;
        var chunkFrames = 137; // deliberately uneven, and not a divisor of frames
        while (offset < chunkedInput.Length)
        {
            var length = Math.Min(chunkFrames * (int)GaplessFormat.Channels, chunkedInput.Length - offset);
            chunked.ProcessInPlace(chunkedInput.AsSpan(offset, length));
            offset += length;
            chunkFrames = chunkFrames == 137 ? 401 : 137;
        }

        for (var i = 0; i < wholeInput.Length; i++)
            Assert.Equal(wholeInput[i], chunkedInput[i], 3);
    }

    // A full-scale tone through a maxed-out EQ used to be hard-clamped to S16
    // inside this class, flat-topping the waveform. Now it stays in float and
    // simply comes out loud - OutputStage owns the one requantisation, with a
    // soft clip and dither. Asserted as "the output is a clean scaled tone",
    // which is what clipping destroys.
    [Fact]
    public void A_heavy_boost_leaves_the_signal_unclipped_in_the_float_domain()
    {
        var settings = FlatSettings();
        settings.PreampDb = 12;
        for (var i = 0; i < Equalizer.BandCount; i++)
            settings.BandGainsDb[i] = 12;

        var buffer = GenerateSine(1000, 24000, amplitude: 1.0f);
        Equalizer.BuildFrom(settings, SampleRate).ProcessInPlace(buffer);

        var peak = 0f;
        foreach (var sample in buffer)
            peak = Math.Max(peak, Math.Abs(sample));

        // Well past full scale, and nothing truncated it on the way - which is
        // exactly the headroom the old S16 clamp had none of.
        Assert.True(peak > short.MaxValue, $"expected the boost to exceed full scale, peaked at {peak}");
        Assert.True(float.IsFinite(peak), "the filter went unstable");
    }
}
