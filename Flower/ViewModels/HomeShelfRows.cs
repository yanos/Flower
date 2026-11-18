using System;
using System.Collections.Generic;
using System.Linq;

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

    // The same heading in a rebuilt list is equal to this one, so the list
    // (ShelfRowPanel) still knows its height before it comes back into view.
    public override bool Equals(object? obj) =>
        obj is HomeShelfHeaderRow other && other.Title == Title && other.IsFirst == IsFirst && other.ShowsHistoryLink == ShowsHistoryLink;

    public override int GetHashCode() => HashCode.Combine(Title, IsFirst, ShowsHistoryLink);
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

    // The album opened under this row on the desktop (AlbumExpansionCard),
    // as an Albums grid row opens one - see HomeViewModel.ExpandedAlbumKey.
    // The phone opens an album's own screen instead, and never reads it.
    public AlbumGridRowViewModel Album { get; } = new() { Tiles = tiles, Columns = columns };

    // A row of the same albums in a rebuilt list is equal to this one, so the
    // list (ShelfRowPanel) still knows how tall an open album makes it while
    // it is out of view - the tiles themselves may be new.
    public override bool Equals(object? obj) =>
        obj is HomeAlbumRow other && other.Columns == Columns && other.Tiles.Select(t => t.Key).SequenceEqual(Tiles.Select(t => t.Key));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Columns);
        foreach (var tile in Tiles)
            hash.Add(tile.Key);
        return hash.ToHashCode();
    }
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
    // heading to lead to. previous: the rows built last time, whose rows of
    // Recently Added covers are handed back where they hold the same covers -
    // Home rebuilds on every song that starts, and a new row object for an
    // open album is a card shown again from nothing, its songs' selection
    // gone, and a row the list (ShelfRowPanel) no longer knows the height of.
    public static List<HomeShelfRow> Build(HomeViewModel home, int columns, int songColumns = 1, bool historyLink = false,
        IReadOnlyList<HomeShelfRow>? previous = null)
    {
        var reusable = previous?.OfType<HomeAlbumRow>().Where(r => r.Columns == columns).ToList() ?? [];
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
                        rows.Add(reusable.FirstOrDefault(r => r.Tiles.SequenceEqual(chunk)) ?? new HomeAlbumRow(chunk, columns));
                    break;
            }
        }
        ApplyExpansion(rows, home);
        ApplyPlaying(rows, home);
        return rows;
    }

    // Opens the album Home has open (HomeViewModel.ExpandedAlbumKey) under
    // the row holding it, and closes every other row's. A row closing keeps
    // its album and songs to fold away with (AlbumGridRowViewModel
    // .ExpandedTile), as in the Albums grid (AlbumGridView.ApplyExpansion).
    public static void ApplyExpansion(IEnumerable<HomeShelfRow> rows, HomeViewModel home)
    {
        var key = home.ExpandedAlbumKey;
        foreach (var tile in home.RecentlyAdded)
            tile.IsExpanded = key != null && tile.Key == key;

        foreach (var row in rows.OfType<HomeAlbumRow>())
        {
            var album = row.Album;
            var tile = key == null ? null : row.Tiles.FirstOrDefault(t => t.Key == key);
            album.IsExpanded = tile != null;
            // Before the songs, which read it as their rows are built.
            album.Availability = home.Availability;
            if (tile == null)
                continue;
            album.ExpandedTile = tile;
            // Only when they differ: handing the same songs again rebuilds
            // every one of their rows.
            if (!album.ExpandedTracks.SequenceEqual(home.ExpandedAlbumTracks))
                album.ExpandedTracks = home.ExpandedAlbumTracks;
        }
    }

    public static void ApplyAvailability(IEnumerable<HomeShelfRow> rows, HomeViewModel home)
    {
        foreach (var row in rows.OfType<HomeAlbumRow>())
            row.Album.Availability = home.Availability;
    }

    public static void ApplyPlaying(IEnumerable<HomeShelfRow> rows, HomeViewModel home)
    {
        foreach (var row in rows.OfType<HomeAlbumRow>())
            row.Album.CurrentlyPlayingTrackId = home.CurrentlyPlayingTrackId;
    }
}
