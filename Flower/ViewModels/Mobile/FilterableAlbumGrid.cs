using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Flower.Services;

namespace Flower.ViewModels.Mobile;

// One of mobile's album grids: every tile it holds, and the rows of whichever
// of those the screen's filter lets through (see
// MobileMainViewModel.ScreenFilter).
//
// The tiles are kept whole underneath the filter rather than rebuilt from it,
// so a tile filtered out and back in is the same instance - with its art
// already loaded and any download it is running still attached - and so a
// library change or a rotation re-chunks against the full set and applies the
// filter again, instead of quietly un-filtering the grid.
public sealed class FilterableAlbumGrid
{
    public ObservableCollection<AlbumGridRow> Rows { get; } = new();

    // Every tile, filtered out or not - what availability is re-marked on and
    // what a rebuild merges against.
    public List<AlbumTileViewModel> Tiles { get; private set; } = new();

    // The filter the rows were last cut with, already normalized (see
    // MobileMainViewModel.ActiveScreenFilter) - null for none.
    public string? Filter { get; private set; }

    public void Replace(List<AlbumTileViewModel> tiles, int columns)
    {
        Tiles = tiles;
        Rechunk(columns);
    }

    // False when the grid was already cut with this filter, so switching back
    // to a screen does not rebuild rows that are already right.
    public bool ApplyFilter(string? filter, int columns)
    {
        if (Filter == filter)
            return false;
        Filter = filter;
        Rechunk(columns);
        return true;
    }

    public void Rechunk(int columns)
    {
        var shown = Filter == null ? Tiles : Tiles.Where(t => Matches(t, Filter)).ToList();
        Rows.Clear();
        foreach (var row in AlbumGridRow.Chunk(shown, columns))
            Rows.Add(row);
    }

    // An album is found by its own name and artist, and by anything any of its
    // songs would be found by - typing a song's title finds the album it is on.
    public static bool Matches(AlbumTileViewModel tile, string filter) =>
        SearchText.Contains(tile.Name, filter) ||
        SearchText.Contains(tile.Artist, filter) ||
        tile.Tracks.Any(t => TrackListBuilder.Matches(t, filter));
}
