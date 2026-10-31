using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests.MusicLibrary;

public class DiscHeadersTests
{
    private static List<Track> Discs(params uint[] discs) =>
        discs.Select((disc, i) => new Track { Title = $"S{i}", DiscNumber = disc }).ToList();

    [Fact]
    public void An_album_of_several_discs_starts_each_with_its_number()
    {
        Assert.Equal(new[] { 1, 0, 0, 2, 0, 3 }, DiscHeaders.Starts(Discs(1, 1, 1, 2, 2, 3)));
    }

    [Fact]
    public void One_disc_gets_no_header()
    {
        Assert.All(DiscHeaders.Starts(Discs(1, 1, 1)), start => Assert.Equal(0, start));
    }

    // Sorted on something other than the disc, the runs are no discs at all.
    [Fact]
    public void Songs_out_of_disc_order_get_no_headers()
    {
        Assert.All(DiscHeaders.Starts(Discs(1, 2, 1, 2)), start => Assert.Equal(0, start));
    }

    [Fact]
    public void A_song_with_no_disc_gets_the_album_no_headers()
    {
        Assert.All(DiscHeaders.Starts(Discs(1, 1, 0, 2)), start => Assert.Equal(0, start));
    }
}
