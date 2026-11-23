using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// UF-8: the list on screen can be sorted, so a row's position is not its place
// in the playlist; for a song in it twice, which row it is says which entry.
public class PlaylistRowSlotsTests
{
    private static Track T(string title) => new() { Title = title };

    private static TrackRowViewModel Row(Track track) => new() { Track = track };

    [Fact]
    public void Each_row_of_a_repeated_song_is_its_own_entry()
    {
        var (a, b) = (T("A"), T("B"));
        var rows = new[] { Row(a), Row(b), Row(a) };

        Assert.Equal([2], PlaylistRowSlots.SlotsOf([a, b, a], rows, [rows[2]]));
        Assert.Equal([0], PlaylistRowSlots.SlotsOf([a, b, a], rows, [rows[0]]));
    }

    [Fact]
    public void Rows_in_another_order_still_find_their_entries()
    {
        var (a, b, c) = (T("A"), T("B"), T("C"));
        var rows = new[] { Row(c), Row(b), Row(a) };

        Assert.Equal([1, 0], PlaylistRowSlots.SlotsOf([a, b, c], rows, [rows[1], rows[2]]));
    }

    [Fact]
    public void A_row_that_is_not_in_the_list_has_no_slot()
    {
        var a = T("A");

        Assert.Empty(PlaylistRowSlots.SlotsOf([a], [Row(a)], [Row(a)]));
    }
}
