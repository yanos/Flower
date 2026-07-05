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
        bool sortArtistAlbumsByYear = false)
    {
        var filtered = Filter(tracks, filterText).ToList();
        var sorted   = Sort(filtered, sortColumn, sortAscending, sortArtistAlbumsByYear).ToList();

        return BuildRows(sorted, currentlyPlayingTrack);
    }

    private static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return tracks;
        return tracks.Where(t =>
            t.Title?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Artists?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
            t.Album?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Genre?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IEnumerable<Track> Sort(IEnumerable<Track> tracks, string col, bool asc, bool sortArtistAlbumsByYear)
    {
        if (col == "PlaylistOrder")
            return tracks;

        IEnumerable<Track> ordered = col switch
        {
            "TrackNumber" => tracks.OrderBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Title"       => tracks.OrderBy(t => SortKey(t.Title)),
            "Artist"      => SortByArtist(tracks, sortArtistAlbumsByYear),
            "Album"       => tracks.OrderBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Year"        => tracks.OrderBy(t => SortKey(t.Year)),
            "Genre"       => tracks.OrderBy(t => SortKey(t.Genre)),
            "DateAdded"   => tracks.OrderBy(t => t.DateAdded),
            "Duration"    => tracks.OrderBy(t => t.Duration),
            // Sort by the same combined total the column displays (see
            // TrackRowViewModel.PlayCountDisplay), not just Flower's own count.
            "PlayCount"   => tracks.OrderBy(t => t.PlayCount + t.ImportedPlayCount),
            _             => tracks.OrderBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
        };
        return asc ? ordered : ordered.Reverse();
    }

    // Each artist's albums are ordered alphabetically by default; with the
    // option on, by year instead - falling back to alphabetical for albums
    // that share a year. Either way, disc/track number order still applies
    // within an album.
    private static IOrderedEnumerable<Track> SortByArtist(IEnumerable<Track> tracks, bool sortAlbumsByYear) =>
        sortAlbumsByYear
            ? tracks.OrderBy(t => SortKey(t.Artists)).ThenBy(t => SortKey(t.Year)).ThenBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            : tracks.OrderBy(t => SortKey(t.Artists)).ThenBy(t => SortKey(t.Album)).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber);

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
        Track? currentlyPlaying)
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
                    IsCurrentlyPlaying = tracks[k].Path == currentlyPlaying?.Path,
                });
            }
            i = j;
        }

        return result;
    }
}
