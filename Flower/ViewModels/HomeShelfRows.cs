using System.Collections.Generic;
using System.Linq;

using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// Home's three shelves as one list of rows - a shelf's heading, then its
// covers (or Recently Played's songs) so many to a row, the shelves in the
// user's order (HomeViewModel.
// ShelfOrder) - for a virtualizing list to show, the way the album
// grids show theirs (AlbumGridView, AlbumGridRow). Only the rows on screen are
// built: Recently Added is months of albums, and on a library scanned within
// that window it is the whole library, which laid out cover by cover made
// every step of a window resize a layout of every cover in it.
//
// Columns is the head's to work out from its width (HomeShelfRowsHost); a
// short last row keeps the full count so its covers are as wide as the rest.
public abstract class HomeShelfRow;

public sealed class HomeShelfHeaderRow(string title, bool isFirst, HomeViewModel? historyLink = null) : HomeShelfRow
{
    public string Title { get; } = title;

    // The first heading sits at the top of the page; the others leave room
    // above them for the shelf they follow.
    public bool IsFirst { get; } = isFirst;

    // Recently Played's "See full history", on a head with a History page
    // to go to (HomeViewModel.ShowHistoryCommand) - null for none.
    public HomeViewModel? HistoryLink { get; } = historyLink;

    public bool ShowsHistoryLink => HistoryLink != null;
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

// Recently Played's songs, so many to a row - two side by side on the
// desktop, one on a phone.
public sealed class HomeSongRow(IReadOnlyList<TrackRowViewModel> songs, int columns) : HomeShelfRow
{
    public IReadOnlyList<TrackRowViewModel> Songs { get; } = songs;
    public int Columns { get; } = columns;
}

public static class HomeShelfRows
{
    // songColumns: how many of Recently Played's songs go side by side.
    // historyLink: whether the head has a History page for Recently Played's
    // heading to lead to.
    public static List<HomeShelfRow> Build(HomeViewModel home, int columns, int songColumns = 1, bool historyLink = false)
    {
        var rows = new List<HomeShelfRow>();
        if (columns < 1)
            columns = 1;
        if (songColumns < 1)
            songColumns = 1;

        foreach (var shelf in home.ShelfOrder)
        {
            switch (shelf)
            {
                case HomeShelf.ContinuePlaying when home.HasContinuePlaying:
                    rows.Add(new HomeShelfHeaderRow(HomeShelfOrder.Title(shelf), rows.Count == 0));
                    foreach (var chunk in home.ContinuePlaying.Chunk(columns))
                        rows.Add(new HomeContinuePlayingRow(chunk, columns));
                    break;
                case HomeShelf.RecentlyPlayed when home.HasRecentlyPlayed:
                    rows.Add(new HomeShelfHeaderRow(HomeShelfOrder.Title(shelf), rows.Count == 0, historyLink ? home : null));
                    foreach (var chunk in home.RecentlyPlayed.Chunk(songColumns))
                        rows.Add(new HomeSongRow(chunk, songColumns));
                    break;
                case HomeShelf.RecentlyAdded when home.HasRecentlyAdded:
                    rows.Add(new HomeShelfHeaderRow(HomeShelfOrder.Title(shelf), rows.Count == 0));
                    foreach (var chunk in home.RecentlyAdded.Chunk(columns))
                        rows.Add(new HomeAlbumRow(chunk, columns));
                    break;
            }
        }
        return rows;
    }
}
