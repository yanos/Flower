using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using Flower.SemanticAudio;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Decoding.SemanticAudio;

// The decoded half of a clip against Python's (docs/VIBE-PLAYLIST-PLAN.md,
// "Preprocessing: the part that must be exact"): decoded to 48 kHz mono by
// FFAudio, cut on the grid and round-tripped through int16, every window of
// both fixtures must match what make-references.py got from the ffmpeg
// command line, sample by sample. Wrong here is an embedding that is subtly
// off and a playlist that is just worse, with nothing failing anywhere else.
//
// The 44.1 kHz fixtures mean the resampler is in it; the chord's second
// window is a 4 s tail, which ClipWaveform then repeat-pads.
[Trait("Category", "RequiresFfmpeg")]
public class ClipWaveformReferenceTests : IClassFixture<SemanticAudioFixtures>
{
    // 3 of 32767 is 1e-4, the plan's bound: two builds of FFmpeg's resampler
    // may round a sample differently, never audibly.
    private const int ToleranceLsb = 3;

    private readonly SemanticAudioFixtures _fixtures;

    public ClipWaveformReferenceTests(SemanticAudioFixtures fixtures) => _fixtures = fixtures;

    public static IEnumerable<TheoryDataRow<string, int>> Windows()
    {
        using var document = JsonDocument.Parse(SemanticAudioReferences.Read("embeddings.json"));
        foreach (var clip in document.RootElement.GetProperty("clips").EnumerateArray())
            yield return new(clip.GetProperty("fixture").GetString()!, clip.GetProperty("window").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void Every_window_matches_python_sample_for_sample(string fixture, int window)
    {
        var expected = MemoryMarshal.Cast<byte, short>(
            SemanticAudioReferences.ReadGzipped($"{System.IO.Path.GetFileNameWithoutExtension(fixture)}.window{window}.prepared.i16.gz"));

        using var reader = new FfmpegClipSource().Open(_fixtures.PathOf(fixture));
        var samples = reader.ReadWindow(window);

        Assert.Equal(expected.Length, samples.Length);
        var worst = 0;
        var at = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var difference = Math.Abs(ClipWaveform.ToInt16(samples[i]) - expected[i]);
            if (difference > worst)
            {
                worst = difference;
                at = i;
            }
        }

        Assert.True(worst <= ToleranceLsb, $"sample {at} is {worst} LSB from Python's");
    }

    [Fact]
    public void The_grid_matches_pythons_window_count()
    {
        using var reader = new FfmpegClipSource().Open(_fixtures.PathOf("chord-44k.wav"));

        Assert.Equal(2, ClipGrid.WindowCount(reader.Duration!.Value));
        Assert.Empty(reader.ReadWindow(2));
    }
}
