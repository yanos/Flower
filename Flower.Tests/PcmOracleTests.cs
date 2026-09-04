using System;

using Flower.Audio;
using Flower.DeviceChecks;

using Xunit;

namespace Flower.Tests;

// The oracle the device checks are decided by, and so the one thing in them
// that cannot be taken on trust: an oracle that answers "fine" to everything
// turns twenty-one green results into twenty-one that mean nothing. These
// hand it the failures it exists to catch and insist it complains.
public class PcmOracleTests
{
    private const double ToneHz = 440.0;

    private static byte[] Tone(double hz, TimeSpan duration, short amplitude = 16384)
    {
        var frames = (int)(duration.TotalSeconds * GaplessFormat.SampleRate);
        var pcm = new byte[frames * GaplessFormat.BytesPerFrame];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * frame / GaplessFormat.SampleRate));
            for (var channel = 0; channel < (int)GaplessFormat.Channels; channel++)
                BitConverter.TryWriteBytes(pcm.AsSpan((frame * (int)GaplessFormat.Channels + channel) * 2), sample);
        }

        return pcm;
    }

    [Fact]
    public void The_tone_it_was_given_passes() =>
        Assert.Null(PcmOracle.ToneMismatch(Tone(ToneHz, TimeSpan.FromSeconds(2)), ToneHz));

    // The album that played nothing.
    [Fact]
    public void Silence_is_caught() =>
        Assert.NotNull(PcmOracle.ToneMismatch(new byte[GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame], ToneHz));

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
        var pcm = new byte[GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame];
        random.NextBytes(pcm);

        Assert.NotNull(PcmOracle.ToneMismatch(pcm, ToneHz));
    }
}
