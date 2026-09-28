using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.Services;

// One album on the Continue Playing shelf, resolved against the library: its
// songs in the order they play, which of them the tape is at, and how far
// through the whole album that is.
public sealed record ContinuePlayingShelfItem(
    AlbumProgressEntry Entry,
    AlbumTileViewModel Tile,
    IReadOnlyList<Track> Tracks,
    int TrackIndex,
    TimeSpan Elapsed,
    TimeSpan Total);

// What the Home screen's three shelves hold, worked out from the library and
// the album-progress shelf. Pure, so the rules - what counts, in what order,
// how many - can be tested without a view.
//
// Albums are the library snapshot's (CatalogIdentity's album artist + album),
// the grouping AlbumProgressTracker keys on, so an album on the Continue
// Playing shelf and the same album on Recently Played are recognisably one
// album and the second can leave it out.
public static class HomeShelves
{
    public static List<ContinuePlayingShelfItem> ContinuePlaying(
        IEnumerable<AlbumProgressEntry> entries, LibrarySnapshot library, int limit)
    {
        var items = new List<ContinuePlayingShelfItem>();
        foreach (var entry in entries)
        {
            if (items.Count == limit)
                break;

            // An album since removed from the library, or a song since removed
            // from the album, has nowhere to pick up from. Left on the shelf
            // rather than dropped from it, in case it is only a rescan away.
            if (library.Album(entry.AlbumId) is not { } album || IsUntitled(album))
                continue;

            var tracks = album.Tracks;
            var index = -1;
            for (var i = 0; i < tracks.Length && index < 0; i++)
            {
                if (tracks[i].Id == entry.TrackId)
                    index = i;
            }

            if (index < 0)
                continue;

            var before = TimeSpan.FromTicks(tracks.Take(index).Sum(t => t.Duration.Ticks));
            var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
            items.Add(new ContinuePlayingShelfItem(entry, TileFor(album), tracks, index, before + entry.Position, total));
        }

        return items;
    }

    // Most recently played first, by the last time any of an album's songs
    // started - leaving out albums already on the Continue Playing shelf,
    // which says the same thing about them and more.
    public static List<AlbumTileViewModel> RecentlyPlayed(LibrarySnapshot library, IReadOnlySet<string> exclude, int limit) =>
        library.Albums
            .Where(a => !IsUntitled(a) && !exclude.Contains(a.Id))
            .Select(a => (Album: a, LastPlayed: a.Tracks.Max(t => t.LastPlayedAt)))
            .Where(a => a.LastPlayed != null)
            .OrderByDescending(a => a.LastPlayed)
            .Take(limit)
            .Select(a => TileFor(a.Album))
            .ToList();

    public static List<AlbumTileViewModel> RecentlyAdded(LibrarySnapshot library, int limit) =>
        library.Albums
            .Where(a => !IsUntitled(a))
            .OrderByDescending(a => a.NewestDateAdded)
            .Take(limit)
            .Select(TileFor)
            .ToList();

    // Songs with no album tag group together under an empty one, which is not
    // an album anybody listened to.
    private static bool IsUntitled(AlbumEntry album) => string.IsNullOrWhiteSpace(album.Summary.Album);

    // The same tile the album grids build, art from the most recently added
    // song, so a tile already loaded in one of them is the same image here.
    private static AlbumTileViewModel TileFor(AlbumEntry album) =>
        new()
        {
            Name = album.Summary.Album!,
            Artist = album.Summary.AlbumArtist,
            RepresentativeTrack = album.Tracks.MaxBy(t => t.DateAdded),
            MostRecentlyAdded = album.NewestDateAdded,
            Tracks = album.Tracks,
        };
}
