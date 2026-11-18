using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Tests.ViewModels.Mobile;

public class FilterableAlbumGridTests
{
    [Fact]
    public void The_grid_filters_by_album_name_artist_or_song_and_clearing_restores_the_same_tiles()
    {
        var grid = new FilterableAlbumGrid();
        var tiles = AlbumGridBuilder.Build([
            new Track { Title = "Karma Police", Album = "OK Computer", Artists = "Radiohead" },
            new Track { Title = "Heroes", Album = "Heroes", Artists = "David Bowie" },
        ]);
        grid.Replace(tiles, columns: 2);
        var computer = tiles.Single(t => t.Name == "OK Computer");
        grid.ApplyFilter("computer", columns: 2);
        Assert.Same(computer, Assert.Single(Assert.Single(grid.Rows).Tiles));

        grid.ApplyFilter("radiohead", columns: 2);
        Assert.Same(computer, Assert.Single(Assert.Single(grid.Rows).Tiles));
        grid.ApplyFilter("karma", columns: 2);
        Assert.Same(computer, Assert.Single(Assert.Single(grid.Rows).Tiles));
        grid.ApplyFilter(null, columns: 2);
        Assert.Equal(tiles, Assert.Single(grid.Rows).Tiles);
    }
}
