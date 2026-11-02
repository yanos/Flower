using System;
using System.Linq;

using Flower.SemanticAudio;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// What laion_clap does to a clip before the encoder sees it. The decoded
// half, against Python's own output, is ClipWaveformReferenceTests.
public class ClipWaveformTests
{
    [Theory]
    [InlineData(0.5f, 16383)]     // truncated, not rounded
    [InlineData(-0.5f, -16383)]
    [InlineData(1.5f, 32767)]     // clamped first
    [InlineData(-1f, -32767)]
    [InlineData(0.00002f, 0)]
    public void Round_trips_through_int16_the_way_laion_clap_does(float sample, short expected)
    {
        Assert.Equal(expected, ClipWaveform.ToInt16(sample));
    }

    [Fact]
    public void Keeps_a_full_window_as_it_is_apart_from_the_round_trip()
    {
        var window = Enumerable.Range(0, Clap.ClipSamples).Select(i => (i % 200 - 100) / 100f).ToArray();

        var prepared = ClipWaveform.Prepare(window);

        Assert.Equal(Clap.ClipSamples, prepared.Length);
        Assert.Equal(ClipWaveform.ToInt16(window[12_345]) / 32767f, prepared[12_345]);
    }

    [Fact]
    public void Repeats_a_short_window_whole_then_pads_with_silence()
    {
        // 7 samples go into 480,000 68,571 times, leaving 3 zeros.
        float[] window = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f];

        var prepared = ClipWaveform.Prepare(window);

        Assert.Equal(prepared[0], prepared[7 * 68_570]);
        Assert.Equal(prepared[6], prepared[7 * 68_571 - 1]);
        Assert.Equal([0f, 0f, 0f], prepared[^3..]);
        Assert.NotEqual(0f, prepared[^4]);
    }

    [Fact]
    public void Pads_a_four_second_tail_to_two_copies_and_silence()
    {
        var tail = Enumerable.Repeat(0.25f, 4 * Clap.SampleRate).ToArray();

        var prepared = ClipWaveform.Prepare(tail);

        Assert.All(prepared[..(8 * Clap.SampleRate)], s => Assert.Equal(8191 / 32767f, s));
        Assert.All(prepared[(8 * Clap.SampleRate)..], s => Assert.Equal(0f, s));
    }
}
