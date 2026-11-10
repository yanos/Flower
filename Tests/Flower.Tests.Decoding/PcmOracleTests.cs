using System;
using System.Runtime.InteropServices;

using Flower.Audio;
using Flower.Tests.Decoding.Scenarios;

using Xunit;

namespace Flower.Tests.Decoding;

// The oracle the device checks are decided by, and so the one thing in them
// that cannot be taken on trust: an oracle that answers "fine" to everything
// turns twenty-one green results into twenty-one that mean nothing. These
// hand it the failures it exists to catch and insist it complains.
public class PcmOracleTests
{
    private const double ToneHz = 440.0;

    // Pipeline PCM, as a decoder writes it: float.
    private static byte[] Pipeline(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

    private static byte[] Tone(double hz, TimeSpan duration, float amplitude = 0.5f)
    {
        var frames = (int)(duration.TotalSeconds * PcmFormat.Default.SampleRate);
        var samples = new float[frames * (int)GaplessFormat.Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * hz * frame / PcmFormat.Default.SampleRate));
            for (var channel = 0; channel < (int)GaplessFormat.Channels; channel++)
                samples[frame * (int)GaplessFormat.Channels + channel] = sample;
        }

        return Pipeline(samples);
    }

    [Fact]
    public void The_tone_it_was_given_passes() =>
        Assert.Null(PcmOracle.ToneMismatch(Tone(ToneHz, TimeSpan.FromSeconds(2)), ToneHz));

    // The album that played nothing.
    [Fact]
    public void Silence_is_caught() =>
        Assert.NotNull(PcmOracle.ToneMismatch(new byte[PcmFormat.Default.SampleRate * PcmFormat.Default.BytesPerFrame], ToneHz));

    [Fact]
    public void No_audio_at_all_is_caught() =>
        Assert.NotNull(PcmOracle.ToneMismatch([], ToneHz));

    // Another track's audio, or a resampled one - right length, right
    // loudness, wrong sound. This is the failure a byte-count assertion and a
    // silence check both wave through.
    [Theory]
    [InlineData(220.0)]
    [InlineData(880.0)]
    [InlineData(1320.0)]
    public void A_different_tone_at_the_same_loudness_is_caught(double actualHz) =>
        Assert.NotNull(PcmOracle.ToneMismatch(Tone(actualHz, TimeSpan.FromSeconds(2)), ToneHz));

    [Fact]
    public void Noise_is_caught()
    {
        var random = new Random(1);
        var samples = new float[PcmFormat.Default.SampleRate * GaplessFormat.Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(random.NextDouble() - 0.5);

        Assert.NotNull(PcmOracle.ToneMismatch(Pipeline(samples), ToneHz));
    }

    // What a float pipeline can carry that an integer one could not. Every
    // comparison against a NaN is false, so without a check of its own the
    // oracle would find nothing anywhere as loud as the tone, and pass it.
    [Fact]
    public void Samples_that_are_not_numbers_are_caught()
    {
        var pcm = Tone(ToneHz, TimeSpan.FromSeconds(2));
        MemoryMarshal.Cast<byte, float>(pcm.AsSpan())[1000] = float.NaN;

        Assert.NotNull(PcmOracle.ToneMismatch(pcm, ToneHz));
    }
}
