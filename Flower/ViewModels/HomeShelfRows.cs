using System.Collections.Generic;
using System.Linq;

using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// Home's three shelves as one list of rows - a shelf's heading, then its
// covers so many to a row - for a virtualizing list to show, the way the album
// grids show theirs (AlbumGridView, AlbumGridRow). Only the rows on screen are
// built: Recently Added is months of albums, and on a library scanned within
// that window it is the whole library, which laid out cover by cover made
// every step of a window resize a layout of every cover in it.
//
// Columns is the head's to work out from its width (HomeShelfRowsHost); a
// short last row keeps the full count so its covers are as wide as the rest.
public abstract class HomeShelfRow;

public sealed class HomeShelfHeaderRow(string title, bool isFirst) : HomeShelfRow
{
    public string Title { get; } = title;

    // The first heading sits at the top of the page; the others leave room
    // above them for the shelf they follow.
    public bool IsFirst { get; } = isFirst;
}

public sealed class HomeContinuePlayingRow(IReadOnlyList<ContinuePlayingItemViewModel> items, int columns) : HomeShelfRow
{
    public IReadOnlyList<ContinuePlayingItemViewModel> Items { get; } = items;
    public int Columns { get; } = columns;
}

public sealed class HomeAlbumRow(IReadOnlyList<AlbumTileViewModel> tiles, int columns) : HomeShelfRow
{
    public IReadOnlyList<AlbumTileViewModel> Tiles { get; } = tiles;
    public int Columns { get; } = columns;
}

public static class HomeShelfRows
{
    public static List<HomeShelfRow> Build(HomeViewModel home, int columns)
    {
        var rows = new List<HomeShelfRow>();
        if (columns < 1)
            columns = 1;

        if (home.HasContinuePlaying)
        {
            rows.Add(new HomeShelfHeaderRow("Continue Playing", rows.Count == 0));
            foreach (var chunk in home.ContinuePlaying.Chunk(columns))
                rows.Add(new HomeContinuePlayingRow(chunk, columns));
        }

        AddShelf(rows, "Recently Played", home.RecentlyPlayed, columns);
        AddShelf(rows, "Recently Added", home.RecentlyAdded, columns);
        return rows;
    }

    private static void AddShelf(List<HomeShelfRow> rows, string title, IReadOnlyList<AlbumTileViewModel> tiles, int columns)
    {
        if (tiles.Count == 0)
            return;

        rows.Add(new HomeShelfHeaderRow(title, rows.Count == 0));
        foreach (var chunk in tiles.Chunk(columns))
            rows.Add(new HomeAlbumRow(chunk, columns));
    }
}
