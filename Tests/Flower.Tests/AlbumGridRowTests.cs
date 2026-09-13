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
        Tracks = [],
    };

    private static AlbumTileViewModel[] Tiles(int count) =>
        Enumerable.Range(0, count).Select(i => Tile(((char)('A' + i)).ToString())).ToArray();

    [Fact]
    public void Chunk_fills_rows_to_the_column_count()
    {
        var rows = AlbumGridRow.Chunk(Tiles(4), columns: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["A", "B"], rows[0].Tiles.Select(t => t.Name));
        Assert.Equal(["C", "D"], rows[1].Tiles.Select(t => t.Name));
    }

    [Fact]
    public void Chunk_leaves_a_trailing_row_short()
    {
        var rows = AlbumGridRow.Chunk(Tiles(7), columns: 5);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Tiles.Count);
        Assert.Equal(["F", "G"], rows[1].Tiles.Select(t => t.Name));
    }

    // A short trailing row still has to size its tiles like a full one, so the
    // last two albums in a five-column grid don't render half the screen wide.
    [Fact]
    public void Chunk_gives_every_row_the_full_column_count()
    {
        var rows = AlbumGridRow.Chunk(Tiles(7), columns: 5);

        Assert.All(rows, row => Assert.Equal(5, row.Columns));
    }

    [Fact]
    public void Chunk_returns_empty_for_no_tiles()
    {
        Assert.Empty(AlbumGridRow.Chunk([], columns: 2));
    }

    [Theory]
    // A phone in portrait, the layout the grids were designed at.
    [InlineData(366, 2)]
    // The same phone turned sideways - the case that motivated all of this,
    // where two columns made the art absurdly large.
    [InlineData(820, 5)]
    // A tablet.
    [InlineData(1156, 7)]
    public void ColumnsFor_fits_as_many_tiles_as_the_width_allows(double width, int expected)
    {
        Assert.Equal(expected, AlbumGridRow.ColumnsFor(width));
    }

    // Two is the floor whatever the measurement says: a one-column album grid
    // is just a list, and an unmeasured grid reports zero width.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(120)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ColumnsFor_never_goes_below_two(double width)
    {
        Assert.Equal(2, AlbumGridRow.ColumnsFor(width));
    }
}
