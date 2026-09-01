using System;
using System.Collections.Generic;
using System.Linq;
using Flower.Models;
using Flower.ViewModels;

namespace Flower.Services;

public static class TrackListBuilder
{
    // Builds a standalone row list with nothing to reuse. Callers on the hot
    // rebuild path (LibraryBrowserViewModel.RebuildRowsAsync) call Plan on a
    // background thread and TrackRowMerge.Apply on the UI thread instead, so
    // the rows already on screen survive the rebuild - see TrackRowMerge.
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
        var plan = Plan(tracks, filterText, sortColumn, sortAscending, currentlyPlayingTrack,
            sortArtistAlbumsByYear, pairedServerFingerprint, pairedServerReachable);

        return TrackRowMerge.Apply(null, plan, out _);
    }

    // The whole filter/sort/group pass, stopping just short of any view-model:
    // safe to run off the UI thread even while the rows it will be merged into
    // are live and bound, because it touches none of them.
    public static List<TrackRowPlan> Plan(
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

        return PlanRows(sorted, currentlyPlayingTrack, pairedServerFingerprint, pairedServerReachable);
    }

    // Public so MainViewModel can also filter the Albums/Recently Added tile
    // grids by the same text (see RebuildRowsAsync) - those aren't built from
    // Rows/TrackRowViewModel at all, so they'd otherwise never see FilterText.
    public static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return tracks;
        // Accent-insensitive as well as case-insensitive - see SearchText for
        // why the fold is written out rather than left to the framework.
        return tracks.Where(t =>
            SearchText.Contains(t.Title,   text) ||
            SearchText.Contains(t.Artists, text) ||
            SearchText.Contains(t.Album,   text) ||
            SearchText.Contains(t.Genre,   text));
    }

    // Sorts on each field's "sort as" value rather than the field itself - the
    // tag's own sort override when it has one, the displayed text otherwise
    // (see Track.SortAs, and Track Info's Options tab, where the overrides are
    // edited). This is the whole reason those tags exist: "The Beatles" filed
    // under B, "David Bowie" under Bowie. Only the sort key changes - every
    // list still *displays* Title/Artists/Album.
    //
    // Descending inverts the *primary* key only, via Order(..., asc); the
    // ThenBy chains after it always stay ascending. It used to be
    // `asc ? ordered : ordered.Reverse()`, and Enumerable.Reverse reverses ties
    // too - so sorting by Album descending also reversed disc/track order
    // within every album, listing each one back to front.
    private static IEnumerable<Track> Sort(IEnumerable<Track> tracks, string col, bool asc, bool sortArtistAlbumsByYear) =>
        col switch
        {
            "PlaylistOrder" => tracks,
            "TrackNumber" => Order(tracks, t => SortKey(t.AlbumSortValue), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Title"       => Order(tracks, t => SortKey(t.TitleSortValue), asc),
            "Artist"      => SortByArtist(tracks, asc, sortArtistAlbumsByYear),
            "Album"       => Order(tracks, t => SortKey(t.AlbumSortValue), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Year"        => Order(tracks, t => SortKey(t.Year), asc),
            "Genre"       => Order(tracks, t => SortKey(t.Genre), asc),
            "DateAdded"   => Order(tracks, t => t.DateAdded, asc),
            "LastPlayed"  => Order(tracks, t => t.LastPlayedAt, asc),
            "Duration"    => Order(tracks, t => t.Duration, asc),
            // Sort by the same combined total the column displays (see
            // Track.TotalPlayCount/TrackRowViewModel.PlayCountDisplay), not just
            // Flower's own count.
            "PlayCount"   => Order(tracks, t => t.TotalPlayCount, asc),
            // The off-by-default columns (see ColumnManager.BuildDefaults) -
            // sortable like any other, since a column that cannot be sorted on
            // is half a column. The four "sort as" ones sort on the override
            // itself rather than on SortAs: the point of showing that column is
            // to see which tracks carry an override and group the ones that do.
            "Composer"         => Order(tracks, t => SortKey(t.Composers), asc),
            "Encoding"         => Order(tracks, t => SortKey(t.EncoderProfile), asc),
            "SortTitle"        => Order(tracks, t => SortKey(t.TitleSort), asc),
            "SortArtist"       => Order(tracks, t => SortKey(t.ArtistsSort), asc),
            "SortAlbum"        => Order(tracks, t => SortKey(t.AlbumSort), asc),
            "SortComposer"     => Order(tracks, t => SortKey(t.ComposersSort), asc),
            "Compilation"      => Order(tracks, t => t.IsCompilation, asc),
            "RememberPosition" => Order(tracks, t => t.RememberPlaybackPosition, asc),
            "ResumePosition"   => Order(tracks, t => t.ResumePosition ?? TimeSpan.Zero, asc),
            "SkipInShuffle"    => Order(tracks, t => t.IgnoreWhenShuffling, asc),
            "VolumeAdjustment" => Order(tracks, t => t.VolumeAdjustment, asc),
            _             => Order(tracks, t => SortKey(t.AlbumSortValue), asc).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
        };

    private static IOrderedEnumerable<Track> Order<TKey>(IEnumerable<Track> tracks, Func<Track, TKey> keySelector, bool ascending) =>
        ascending ? tracks.OrderBy(keySelector) : tracks.OrderByDescending(keySelector);

    // Each artist's albums are ordered alphabetically by default; with the
    // option on, by year instead - falling back to alphabetical for albums
    // that share a year. Either way, disc/track number order still applies
    // within an album, and only the artist name flips on a descending sort.
    private static IOrderedEnumerable<Track> SortByArtist(IEnumerable<Track> tracks, bool asc, bool sortAlbumsByYear) =>
        sortAlbumsByYear
            ? Order(tracks, t => SortKey(t.ArtistsSortValue), asc).ThenBy(t => SortKey(t.Year)).ThenBy(t => SortKey(t.AlbumSortValue)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            : Order(tracks, t => SortKey(t.ArtistsSortValue), asc).ThenBy(t => SortKey(t.AlbumSortValue)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber);

    // Strips everything but letters/digits before comparing, so punctuation,
    // symbols, and spacing differences (leading quotes/brackets, "&" vs
    // "and", double vs. no space, etc.) don't affect sort order.
    //
    // Hand-rolled rather than the obvious LINQ one-liner
    // (new string(s.Where(char.IsLetterOrDigit).ToArray())): this is a sort
    // key selector, so it runs O(n log n) times over the whole library on every
    // filter, sort and view change, and that version allocated an enumerator,
    // a growing intermediate char[] and the result string on every single call.
    // This counts first, then fills exactly once - and returns the input
    // untouched when there was nothing to strip, which allocates nothing at
    // all. See docs/ARCHITECTURE-REVIEW.md Tier 1.5.
    private static string SortKey(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        var kept = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
                kept++;
        }

        if (kept == s.Length)
            return s;
        if (kept == 0)
            return "";

        return string.Create(kept, s, static (span, source) =>
        {
            var i = 0;
            foreach (var c in source)
            {
                if (char.IsLetterOrDigit(c))
                    span[i++] = c;
            }
        });
    }

    // Groups runs of consecutive tracks that share an album so the row list can
    // render a single spanning album-art cell for the whole run, no matter what
    // produced the adjacency (an explicit album sort, or another sort/column
    // whose secondary keys happen to keep an album's tracks together).
    private static List<TrackRowPlan> PlanRows(
        List<Track> tracks,
        Track? currentlyPlaying,
        string? pairedServerFingerprint,
        bool pairedServerReachable)
    {
        var result = new List<TrackRowPlan>(tracks.Count);

        int i = 0;
        while (i < tracks.Count)
        {
            var albumKey = tracks[i].Album ?? "";
            int j = i;
            while (j < tracks.Count && (tracks[j].Album ?? "") == albumKey) j++;
            int groupSize = j - i;

            // One answer for the whole run, since the art cell it greys out
            // belongs to the run rather than to any one row - an album whose
            // first track happens to be an unreachable placeholder is still
            // fully listenable if any of the rows below it is not. See
            // TrackAvailability.IsAlbumUnavailable, the same rule the album
            // grids' tiles use.
            // (Spelled out rather than calling TrackAvailability.IsAlbumUnavailable
            // over a slice - this runs on every keystroke across the whole
            // library, and a per-group GetRange copy is exactly the kind of
            // allocation Tier 1.5 spent its effort removing. IsPlayable, the
            // rule itself, is still the shared one.)
            var groupUnavailable = true;
            for (int k = i; k < j && groupUnavailable; k++)
                groupUnavailable = !TrackAvailability.IsPlayable(tracks[k], pairedServerFingerprint, pairedServerReachable);

            for (int k = i; k < j; k++)
            {
                result.Add(new TrackRowPlan(
                    Track: tracks[k],
                    IsFirstInAlbumGroup: k == i,
                    AlbumGroupSize: groupSize,
                    // Compared by Track.Id, not by Path. Path is not an
                    // identity: it is null for every not-yet-downloaded
                    // placeholder, so `a.Path == b.Path` made all of them equal
                    // to each other and - with nothing playing at all - equal to
                    // "currently playing" too, putting the play indicator on
                    // every placeholder row at once. Id also survives the
                    // transient stream-URL copy a streamed placeholder plays as
                    // (Track.Clone keeps it - see
                    // PlaylistControlViewModel.ResolveForPlaybackAsync), which a
                    // path comparison never matched at all.
                    IsCurrentlyPlaying: currentlyPlaying != null && tracks[k].Id == currentlyPlaying.Id,
                    // See TrackAvailability.IsAvailable - computed here so a
                    // freshly-built row is correct from the moment it exists,
                    // rather than starting from a default and waiting for a
                    // separate post-build pass to catch up.
                    IsAvailable: TrackAvailability.IsAvailable(tracks[k], pairedServerFingerprint, pairedServerReachable),
                    IsAlbumGroupUnavailable: groupUnavailable));
            }
            i = j;
        }

        return result;
    }
}
