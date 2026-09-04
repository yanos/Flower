using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The framing behind "give me the art for these albums in one request", which
// exists because one request per tile is what an album grid naturally does and
// what a server cannot afford to be asked - see CoverArtBatch's own header for
// the album that stopped playing over it.
//
// A frame is bytes on a wire between two versions of this app, so what is
// pinned here is the round trip and, more importantly, that a *bad* frame is
// answered with null rather than an exception or - far worse - a plausible
// dictionary built from misread lengths. The reader runs on whatever a peer
// sent, and a peer is not always the peer it claims to be.
public class CoverArtBatchTests
{
    private static byte[] Blob(int length, byte seed) =>
        Enumerable.Range(0, length).Select(i => (byte)(i + seed)).ToArray();

    [Fact]
    public void What_goes_in_comes_back_out()
    {
        List<(string, byte[])> entries =
        [
            ("al-one", Blob(1024, 1)),
            ("al-two", Blob(4096, 2)),
            ("al-three", Blob(7, 3)),
        ];

        var read = CoverArtBatch.Read(CoverArtBatch.Write(entries));

        Assert.NotNull(read);
        Assert.Equal(3, read.Count);
        foreach (var (id, bytes) in entries)
        {
            Assert.Equal(bytes, read[id]);
        }
    }

    // An album with no picture is an answer, not an omission. The client tells
    // the two apart - a zero-length entry is "this server has no art for that
    // album", a missing one is "the response was truncated at its byte cap,
    // ask again" - and it can only do that because the writer includes every
    // id it was asked about.
    [Fact]
    public void An_album_with_no_art_comes_back_empty_rather_than_absent()
    {
        var read = CoverArtBatch.Read(CoverArtBatch.Write([("al-bare", [])]));

        Assert.NotNull(read);
        Assert.True(read.ContainsKey("al-bare"));
        Assert.Empty(read["al-bare"]);
    }

    [Fact]
    public void An_empty_batch_round_trips()
    {
        var read = CoverArtBatch.Read(CoverArtBatch.Write([]));

        Assert.NotNull(read);
        Assert.Empty(read);
    }

    [Fact]
    public void Ids_are_carried_as_UTF8_rather_than_ASCII()
    {
        var read = CoverArtBatch.Read(CoverArtBatch.Write([("al-Ångström-café-日本", Blob(16, 9))]));

        Assert.NotNull(read);
        Assert.True(read.ContainsKey("al-Ångström-café-日本"));
    }

    // Truncation is the realistic corruption: a connection cut mid-response.
    // Every prefix of a valid frame that is not the whole frame has to be
    // refused, because the alternative is a reader that walks off the end of
    // one blob and into the next and hands the caller an image made of two
    // halves of different pictures.
    [Fact]
    public void Every_truncation_of_a_good_frame_is_refused()
    {
        var frame = CoverArtBatch.Write([("al-one", Blob(64, 1)), ("al-two", Blob(64, 2))]);

        for (var cut = 0; cut < frame.Length; cut++)
        {
            Assert.Null(CoverArtBatch.Read(frame.AsSpan(0, cut)));
        }

        Assert.NotNull(CoverArtBatch.Read(frame));
    }

    // A length field is the one thing in the frame an attacker controls
    // directly, so it is the one that must not be trusted: a negative length,
    // or one longer than the frame, has to be refused rather than allocated.
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    public void A_length_the_frame_cannot_honour_is_refused(int claimed)
    {
        var frame = CoverArtBatch.Write([("al-one", Blob(64, 1))]);

        // The blob length: four bytes for the count, four for the id length,
        // then the id itself.
        var offset = 4 + 4 + Encoding.UTF8.GetByteCount("al-one");
        BitConverter.TryWriteBytes(frame.AsSpan(offset), claimed);

        Assert.Null(CoverArtBatch.Read(frame));
    }

    // The count is a capacity hint before it is a loop bound, so a huge one is
    // an allocation an attacker gets to name. Bounded by the same cap the
    // request is bounded by.
    [Fact]
    public void A_count_beyond_the_cap_is_refused_before_anything_is_allocated()
    {
        var frame = CoverArtBatch.Write([("al-one", Blob(4, 1))]);
        BitConverter.TryWriteBytes(frame.AsSpan(0), CoverArtBatch.MaxIds + 1);

        Assert.Null(CoverArtBatch.Read(frame));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        var frame = CoverArtBatch.Write([("al-one", Blob(4, 1))]);
        BitConverter.TryWriteBytes(frame.AsSpan(0), -1);

        Assert.Null(CoverArtBatch.Read(frame));
    }

    [Fact]
    public void Nothing_at_all_is_refused()
    {
        Assert.Null(CoverArtBatch.Read([]));
    }
}
