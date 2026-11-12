using System;

using Flower.Audio;
using Flower.Tests.Decoding.Scenarios;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Audio;

// The graphic EQ as it runs: Equalizer.BuildFrom's coefficients, filtered by
// libflowerbridge's render callback. Measured through the whole output stage
// at 24 bits, at unity gain with no fades, where the requantisation is far
// below anything asserted here - so what these see is the filter.
// GaplessAudioManagerTests covers the forwarding plumbing (ApplyEqualizer
// reaching the sink).
public class EqualizerTests
{
    private const uint SampleRate = GaplessFormat.DefaultSampleRate;

    private static readonly AudioTimingSettings NoFades = new() { GainRampMs = 0, DeclickFadeMs = 0, TransportFadeMs = 0 };

    private static float[] GenerateSine(double frequencyHz, int frameCount, float amplitude = 0.25f, uint sampleRate = SampleRate)
    {
        var samples = new float[frameCount * (int)GaplessFormat.Channels];
        for (var i = 0; i < frameCount; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
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

    // A stage running the curve from the start. The swap onto it is
    // crossfaded over the first buffer, so that buffer is silence, rendered
    // and thrown away, and the declick goes with it.
    private static OutputStageHarness Stage(EqualizerSettings settings, uint sampleRate = SampleRate)
    {
        var stage = new OutputStageHarness(DeviceSampleFormat.S24, NoFades, sampleRate)
        {
            Equalizer = Equalizer.BuildFrom(settings, sampleRate),
        };
        stage.RenderNothing(480);
        return stage;
    }

    // Back to normalised float, for measuring.
    private static float[] Normalised(byte[] s24)
    {
        var samples = OutputStageHarness.Unpack(DeviceSampleFormat.S24, s24);
        var normalised = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            normalised[i] = samples[i] / 8_388_608f;

        return normalised;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));

        return peak;
    }

    // The steady-state gain the filter actually applies at one frequency,
    // measured rather than derived: drive it with a tone, discard the first
    // half so the biquads have settled, and compare RMS in to RMS out.
    private static double MeasuredGainDb(EqualizerSettings settings, double toneHz)
    {
        using var stage = Stage(settings);
        var input = GenerateSine(toneHz, 24000);
        var output = Normalised(stage.Render(input));

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
    // asked for - the whole point of a graphic EQ.
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

    // The delay lines have to carry across callbacks: the device hands over
    // one period at a time, and a filter that restarted from zero state on
    // each of them would put a discontinuity at every period boundary - about
    // ten clicks a second.
    //
    // Asserted by rendering the same tone once as a single callback and again
    // as uneven ones, and requiring the device to be handed the same bytes. A
    // per-callback reset passes every other test in this file, because they
    // all render one buffer.
    [Fact]
    public void Filter_state_carries_across_callbacks_of_different_lengths()
    {
        const int frames = 8000;
        var tone = GenerateSine(1000, frames);

        using var whole = Stage(WithBand(1000, 9));
        var expected = whole.Render(tone);

        using var chunked = Stage(WithBand(1000, 9));
        var actual = new byte[expected.Length];
        var bytesPerFrame = DeviceSampleFormat.S24.BytesPerFrame();
        var offset = 0;
        var chunkFrames = 137; // deliberately uneven, and not a divisor of frames
        while (offset < frames)
        {
            var length = Math.Min(chunkFrames, frames - offset);
            chunked.Render(tone.AsSpan(offset * 2, length * 2)).CopyTo(actual, offset * bytesPerFrame);
            offset += length;
            chunkFrames = chunkFrames == 137 ? 401 : 137;
        }

        Pcm.AssertBitExact(expected, actual);
    }

    // The EQ runs in float with no clamp of its own, so a boost past full
    // scale mid-chain is not clipped there: turning the volume down afterwards
    // brings back a clean, quieter tone. It used to round and hard-clamp to
    // S16 inside the filter, flat-topping the waveform before any later gain
    // could pull it back.
    [Fact]
    public void A_boost_past_full_scale_is_not_clipped_before_the_volume_brings_it_down()
    {
        var settings = FlatSettings();
        settings.PreampDb = 12;
        Array.Fill(settings.BandGainsDb, 12.0);

        // Around +30dB at 1kHz with every band and the preamp up: 0.9 in is
        // some 25 times full scale before the volume, and 40dB down from
        // there lands back inside it.
        const float volume = 0.01f;
        using var stage = Stage(settings);
        stage.TargetGain = volume;
        var output = Normalised(stage.Render(GenerateSine(1000, 24000, amplitude: 0.9f)));

        // A clamp at full scale before the volume would have held the peak
        // to the volume itself.
        var peak = Peak(output.AsSpan(output.Length / 2));
        Assert.True(peak > volume * 4, $"peaked at {peak}, as though clipped before the volume");
        Assert.True(peak < 0.9f, $"peaked at {peak}: the volume did not bring it back inside full scale");
    }

    // A band at or past Nyquist used to build a biquad with its poles on or
    // outside the unit circle - at 22050 Hz the 16 kHz band diverged into
    // full-scale noise within milliseconds. Every band boosted, every rate a
    // device might open at: the output has to stay the size of the input,
    // rather than run up to the clamp.
    [Theory]
    [InlineData(48000u)]
    [InlineData(44100u)]
    [InlineData(32000u)]
    [InlineData(22050u)]
    [InlineData(16000u)]
    [InlineData(8000u)]
    public void Every_band_boosted_stays_stable_at_any_device_rate(uint sampleRate)
    {
        var settings = FlatSettings();
        Array.Fill(settings.BandGainsDb, 12.0);
        using var stage = Stage(settings, sampleRate);

        // A second of quiet white noise, so every band - and Nyquist - is
        // driven, with room for every boost to stack up under full scale.
        var random = new Random(1);
        var samples = new float[(int)sampleRate * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)((random.NextDouble() * 2 - 1) * 0.02);

        var peak = Peak(Normalised(stage.Render(samples)));
        Assert.True(peak < 0.5f, $"peak {peak} at {sampleRate} Hz");
    }
}
