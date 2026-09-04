using Flower.Audio;
using Flower.Models;

using Xunit;

namespace Flower.Tests;

// What LibVLC is told to demux a stream with. The hint exists because a stream
// URL carries no file extension for it to guess from, and iOS's build guesses
// wrong for AAC - see TrackDecoder.DemuxHintFor.
public class TrackDecoderDemuxHintTests
{
    private static Track Streaming(string? suffix) => new()
    {
        Title = "A track",
        Path = "http://server:4533/rest/stream?id=abc",
        OriginFileExtension = suffix,
    };

    [Theory]
    [InlineData("m4a")]
    [InlineData("M4A")]
    [InlineData(".m4a")]
    [InlineData("mp4")]
    [InlineData("m4b")]
    [InlineData("alac")]
    public void An_mp4_container_is_named_outright(string suffix) =>
        Assert.Equal("mp4", TrackDecoder.DemuxHintFor(Streaming(suffix)));

    // mp3 streams perfectly well on the probe, and a forced demuxer that turns
    // out to be the wrong one is worse than no hint at all.
    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("wav")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_left_to_LibVLC(string? suffix) =>
        Assert.Null(TrackDecoder.DemuxHintFor(Streaming(suffix)));

    // A local file has the extension in its path; the catalog's own record of
    // it is only there for tracks that arrived as placeholders.
    [Fact]
    public void A_local_path_answers_from_its_own_extension() =>
        Assert.Equal("mp4", TrackDecoder.DemuxHintFor(new Track { Title = "A track", Path = "/music/01 Fingerbib.m4a" }));

    [Fact]
    public void A_local_mp3_still_gets_no_hint() =>
        Assert.Null(TrackDecoder.DemuxHintFor(new Track { Title = "A track", Path = "/music/01 Husbands.mp3" }));
}
