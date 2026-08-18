using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Direct coverage of SyncHttpServer.ParseSingleByteRange's forms. The common
// paths (a bounded range, an open-ended resume, a start past the end) are
// asserted end-to-end over a real socket in SyncHttpServerRoundTripTests
// instead; these are the shapes that are awkward to provoke through an
// HttpClient, which normalizes or rejects most malformed headers before they
// reach the wire.
public class RangeHeaderParsingTests
{
    [Fact]
    public void No_range_header_at_all_means_serve_the_whole_body()
    {
        Assert.Null(SyncHttpServer.ParseSingleByteRange(null, 100));
    }

    // "the last N bytes", the one form where the numbers aren't offsets.
    [Theory]
    [InlineData("bytes=-30", 70, 99)]
    [InlineData("bytes=-100", 0, 99)]  // exactly the whole thing
    [InlineData("bytes=-500", 0, 99)]  // more than exists: clamped, not refused
    public void A_suffix_range_counts_back_from_the_end(string header, long start, long end)
    {
        Assert.Equal((start, end), SyncHttpServer.ParseSingleByteRange(header, 100));
    }

    [Fact]
    public void An_end_past_the_end_is_clamped_rather_than_refused()
    {
        Assert.Equal((90L, 99L), SyncHttpServer.ParseSingleByteRange("bytes=90-4000", 100));
    }

    [Fact]
    public void A_zero_length_suffix_is_unsatisfiable_rather_than_an_empty_body()
    {
        Assert.Equal(SyncHttpServer.UnsatisfiableRange, SyncHttpServer.ParseSingleByteRange("bytes=-0", 100));
    }

    [Fact]
    public void A_suffix_range_against_an_empty_file_is_unsatisfiable()
    {
        Assert.Equal(SyncHttpServer.UnsatisfiableRange, SyncHttpServer.ParseSingleByteRange("bytes=-10", 0));
    }

    // RFC 9110 14.2: a recipient that cannot interpret a Range header ignores
    // it and serves the whole representation - strictly better than failing,
    // and the reason these are null (serve everything) rather than 416.
    [Theory]
    [InlineData("items=0-10")]     // a unit we don't speak
    [InlineData("bytes=0-10,20-30")] // multipart: legal, just unsupported
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=50")]       // no dash at all
    [InlineData("bytes=")]
    [InlineData("bytes=80-20")]    // end before start
    [InlineData("bytes=-5-10")]    // negative start
    public void An_uninterpretable_range_is_ignored_rather_than_refused(string header)
    {
        Assert.Null(SyncHttpServer.ParseSingleByteRange(header, 100));
    }
}
