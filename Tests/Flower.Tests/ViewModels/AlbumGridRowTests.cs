using System;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Tests.ViewModels;

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
    public void Chunk_fills_rows_and_keeps_a_short_trailing_row()
    {
        var rows = AlbumGridRow.Chunk(Tiles(7), columns: 5);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, rows[0].Tiles.Count);
        Assert.Equal(["F", "G"], rows[1].Tiles.Select(t => t.Name));
        Assert.Empty(AlbumGridRow.Chunk([], columns: 2));
    }
}
