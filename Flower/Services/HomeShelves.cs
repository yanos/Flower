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
// how many or how far back - can be tested without a view.
//
// Albums are the library snapshot's (CatalogIdentity's album artist + album),
// the grouping AlbumProgressTracker keys on. Recently Played is songs rather
// than albums.
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

    // Songs, most recently played first - every song with a play, whatever
    // album it is from, so an album on the Continue Playing shelf still has
    // its songs here - leaving out those taken off the shelf (`hidden`, song
    // id to when it was last played at the time) that have not been played
    // since.
    public static List<Track> RecentlyPlayed(LibrarySnapshot library, int limit,
        IReadOnlyDictionary<string, DateTimeOffset>? hidden = null) =>
        library.Tracks
            .Where(t => t.LastPlayedAt is { } at && !IsHidden(t, at, hidden))
            .OrderByDescending(t => t.LastPlayedAt)
            .Take(limit)
            .ToList();

    // Newest first, every album with a song added since then - a time window
    // rather than a count, so a big import shows whole and a quiet season
    // shows little.
    public static List<AlbumTileViewModel> RecentlyAdded(LibrarySnapshot library, DateTimeOffset since) =>
        library.Albums
            .Where(a => !IsUntitled(a) && a.NewestDateAdded >= since)
            .OrderByDescending(a => a.NewestDateAdded)
            .Select(TileFor)
            .ToList();

    private static bool IsHidden(Track track, DateTimeOffset lastPlayed, IReadOnlyDictionary<string, DateTimeOffset>? hidden) =>
        hidden != null && hidden.TryGetValue(track.Id.ToKey(), out var at) && lastPlayed <= at;

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
