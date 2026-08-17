using System;
using System.Collections.Generic;
using System.Linq;
using Flower.Models;
using Flower.ViewModels;

namespace Flower.Services;

public static class TrackListBuilder
{
    public static List<TrackRowViewModel> Build(
        IEnumerable<Track> tracks,
        string? filterText,
        string sortColumn,
        bool sortAscending,
        Track? currentlyPlayingTrack = null,
        bool sortArtistAlbumsByYear = false,
        string? pairedServerFingerprint = null,
        bool pairedServerReachable = false)
    {
        var filtered = Filter(tracks, filterText).ToList();
        var sorted   = Sort(filtered, sortColumn, sortAscending, sortArtistAlbumsByYear).ToList();

        return BuildRows(sorted, currentlyPlayingTrack, pairedServerFingerprint, pairedServerReachable);
    }

    // Public so MainViewModel can also filter the Albums/Recently Added tile
    // grids by the same text (see RebuildRowsAsync) - those aren't built from
    // Rows/TrackRowViewModel at all, so they'd otherwise never see FilterText.
    public static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return tracks;
        return tracks.Where(t =>
            t.Title?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Artists?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
            t.Album?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Genre?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true);
    }

    // Descending inverts the *primary* key only, via Order(..., asc); the
    // ThenBy chains after it always stay ascending. It used to be
    // `asc ? ordered : ordered.Reverse()`, and Enumerable.Reverse reverses ties
    // too - so sorting by Album descending also reversed disc/track order
    // within every album, listing each one back to front.
    private static IEnumerable<Track> Sort(IEnumerable<Track> tracks, string col, bool asc, bool sortArtistAlbumsByYear) =>
        col switch
        {
            "PlaylistOrder" => tracks,
            "TrackNumber" => Order(tracks, t => SortKey(t.Album), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Title"       => Order(tracks, t => SortKey(t.Title), asc),
            "Artist"      => SortByArtist(tracks, asc, sortArtistAlbumsByYear),
            "Album"       => Order(tracks, t => SortKey(t.Album), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Year"        => Order(tracks, t => SortKey(t.Year), asc),
            "Genre"       => Order(tracks, t => SortKey(t.Genre), asc),
            "DateAdded"   => Order(tracks, t => t.DateAdded, asc),
            "LastPlayed"  => Order(tracks, t => t.LastPlayedAt, asc),
            "Duration"    => Order(tracks, t => t.Duration, asc),
            // Sort by the same combined total the column displays (see
            // Track.TotalPlayCount/TrackRowViewModel.PlayCountDisplay), not just
            // Flower's own count.
            "PlayCount"   => Order(tracks, t => t.TotalPlayCount, asc),
            _             => Order(tracks, t => SortKey(t.Album), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
        };

    private static IOrderedEnumerable<Track> Order<TKey>(IEnumerable<Track> tracks, Func<Track, TKey> keySelector, bool ascending) =>
        ascending ? tracks.OrderBy(keySelector) : tracks.OrderByDescending(keySelector);

    // Each artist's albums are ordered alphabetically by default; with the
    // option on, by year instead - falling back to alphabetical for albums
    // that share a year. Either way, disc/track number order still applies
    // within an album, and only the artist name flips on a descending sort.
    private static IOrderedEnumerable<Track> SortByArtist(IEnumerable<Track> tracks, bool asc, bool sortAlbumsByYear) =>
        sortAlbumsByYear
            ? Order(tracks, t => SortKey(t.Artists), asc).ThenBy(t => SortKey(t.Year)).ThenBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            : Order(tracks, t => SortKey(t.Artists), asc).ThenBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber);

    // Strips everything but letters/digits before comparing, so punctuation,
    // symbols, and spacing differences (leading quotes/brackets, "&" vs
    // "and", double vs. no space, etc.) don't affect sort order.
    private static string SortKey(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    // Groups runs of consecutive tracks that share an album so the row list can
    // render a single spanning album-art cell for the whole run, no matter what
    // produced the adjacency (an explicit album sort, or another sort/column
    // whose secondary keys happen to keep an album's tracks together).
    private static List<TrackRowViewModel> BuildRows(
        List<Track> tracks,
        Track? currentlyPlaying,
        string? pairedServerFingerprint,
        bool pairedServerReachable)
    {
        var result = new List<TrackRowViewModel>(tracks.Count);

        int i = 0;
        while (i < tracks.Count)
        {
            var albumKey = tracks[i].Album ?? "";
            int j = i;
            while (j < tracks.Count && (tracks[j].Album ?? "") == albumKey) j++;
            int groupSize = j - i;

            for (int k = i; k < j; k++)
            {
                result.Add(new TrackRowViewModel
                {
                    Track              = tracks[k],
                    IsFirstInAlbumGroup = k == i,
                    AlbumGroupSize     = groupSize,
                    // tracks[k].Path == currentlyPlaying?.Path alone is wrong
                    // whenever nothing is playing (currentlyPlaying == null):
                    // null == null is true, so every not-yet-downloaded track
                    // (Path == null too) matched "currently playing" and got
                    // the bold/accent-color styling (Button.trackRow.playing)
                    // meant for an actual playing row.
                    IsCurrentlyPlaying = tracks[k].Path != null && currentlyPlaying != null && tracks[k].Path == currentlyPlaying.Path,
                    // See TrackAvailability.IsAvailable - computed here so a
                    // freshly-built row is correct from the moment it exists,
                    // rather than starting from a default and waiting for a
                    // separate post-build pass to catch up.
                    IsAvailable = TrackAvailability.IsAvailable(tracks[k], pairedServerFingerprint, pairedServerReachable),
                });
            }
            i = j;
        }

        return result;
    }
}
