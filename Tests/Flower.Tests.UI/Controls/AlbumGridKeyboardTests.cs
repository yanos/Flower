using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

using Flower.Controls;
using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Controls;

// Where the arrow keys go among the Albums grid's tiles (AlbumGridKeyboard).
// Seven albums four across:
//
//   0 1 2 3
//   4 5 6
public class AlbumGridKeyboardTests
{
    [Theory]
    [InlineData(Key.Right, 1, 2)]
    [InlineData(Key.Left, 1, 0)]
    // Running on from the end of one row to the start of the next, and back.
    [InlineData(Key.Right, 3, 4)]
    [InlineData(Key.Left, 4, 3)]
    [InlineData(Key.Down, 1, 5)]
    [InlineData(Key.Up, 5, 1)]
    // The row below is too short for this column: the last album.
    [InlineData(Key.Down, 3, 6)]
    public void An_arrow_moves_to_the_album_beside_it(Key key, int from, int expected)
    {
        Assert.Equal(expected, AlbumGridKeyboard.Target(key, from, count: 7, columns: 4));
    }

    [Theory]
    [InlineData(Key.Left, 0)]
    [InlineData(Key.Up, 2)]
    [InlineData(Key.Right, 6)]
    [InlineData(Key.Down, 5)]
    public void An_arrow_at_the_edge_goes_nowhere(Key key, int from)
    {
        Assert.Null(AlbumGridKeyboard.Target(key, from, count: 7, columns: 4));
    }

    [Fact]
    public void With_no_album_to_start_from_an_arrow_starts_at_the_first()
    {
        Assert.Equal(0, AlbumGridKeyboard.Target(Key.Down, -1, count: 7, columns: 4));
    }

    [Fact]
    public void Keys_that_are_not_arrows_go_nowhere()
    {
        Assert.Null(AlbumGridKeyboard.Target(Key.A, 1, count: 7, columns: 4));
    }

    private static List<AlbumTileViewModel> Tiles(int count) =>
        Enumerable.Range(0, count).Select(i => new AlbumTileViewModel
        {
            Name = $"Album {i}",
            RepresentativeTrack = new Track { Album = $"Album {i}" },
            Tracks = [],
        }).ToList();

    // Two selected, by a Shift+click say: the keys carry on from where they
    // last went.
    [Fact]
    public void The_keys_carry_on_from_the_album_they_last_moved_to()
    {
        var tiles = Tiles(4);
        tiles[0].IsSelected = tiles[2].IsSelected = true;

        Assert.Equal(2, AlbumGridKeyboard.CurrentIndex(tiles, lastMovedTo: tiles[2]));
    }

    // A click elsewhere since: that album is no longer selected, so the keys
    // start from what is.
    [Fact]
    public void An_album_the_keys_moved_to_and_that_is_no_longer_selected_is_not_where_they_start()
    {
        var tiles = Tiles(4);
        tiles[3].IsSelected = true;

        Assert.Equal(3, AlbumGridKeyboard.CurrentIndex(tiles, lastMovedTo: tiles[1]));
    }

    [Fact]
    public void The_open_album_comes_before_the_first_selected_one()
    {
        var tiles = Tiles(4);
        tiles[1].IsSelected = true;
        tiles[2].IsExpanded = true;

        Assert.Equal(2, AlbumGridKeyboard.CurrentIndex(tiles, lastMovedTo: null));
    }

    [Fact]
    public void Nothing_selected_or_open_is_nowhere_to_start_from()
    {
        Assert.Equal(-1, AlbumGridKeyboard.CurrentIndex(Tiles(4), lastMovedTo: null));
    }
}
