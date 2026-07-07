using System;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.Tests;

public class AlbumGridRowTests
{
    private static AlbumTileViewModel Tile(string name) => new()
    {
        Name = name,
        RepresentativeTrack = new Track { Title = "Song", Album = name, DateAdded = DateTimeOffset.UtcNow },
    };

    [Fact]
    public void Chunk_pairs_tiles_two_per_row()
    {
        var tiles = new[] { Tile("A"), Tile("B"), Tile("C"), Tile("D") };

        var rows = AlbumGridRow.Chunk(tiles);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("A", "B"), (rows[0].First.Name, rows[0].Second?.Name));
        Assert.Equal(("C", "D"), (rows[1].First.Name, rows[1].Second?.Name));
    }

    [Fact]
    public void Chunk_leaves_second_null_on_an_odd_trailing_tile()
    {
        var tiles = new[] { Tile("A"), Tile("B"), Tile("C") };

        var rows = AlbumGridRow.Chunk(tiles);

        Assert.Equal(2, rows.Count);
        Assert.Equal("C", rows[1].First.Name);
        Assert.Null(rows[1].Second);
    }

    [Fact]
    public void Chunk_returns_empty_for_no_tiles()
    {
        Assert.Empty(AlbumGridRow.Chunk(Array.Empty<AlbumTileViewModel>()));
    }
}
