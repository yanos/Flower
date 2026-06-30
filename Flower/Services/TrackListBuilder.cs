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
        Track? currentlyPlayingTrack = null)
    {
        var filtered = Filter(tracks, filterText).ToList();
        var sorted   = Sort(filtered, sortColumn, sortAscending).ToList();

        bool groupByAlbum = string.Equals(sortColumn, "Album", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(sortColumn, "TrackNumber", StringComparison.OrdinalIgnoreCase);

        return BuildRows(sorted, groupByAlbum, currentlyPlayingTrack);
    }

    private static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return tracks;
        return tracks.Where(t =>
            t.Title?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Artists?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
            t.Album?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true ||
            t.Genre?.Contains(text,   StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IEnumerable<Track> Sort(IEnumerable<Track> tracks, string col, bool asc)
    {
        IEnumerable<Track> ordered = col switch
        {
            "TrackNumber" => tracks.OrderBy(t => t.Album ?? "").ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Title"       => tracks.OrderBy(t => t.Title   ?? ""),
            "Artist"      => tracks.OrderBy(t => t.Artists ?? ""),
            "Album"       => tracks.OrderBy(t => t.Album   ?? "").ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Year"        => tracks.OrderBy(t => t.Year    ?? ""),
            "Genre"       => tracks.OrderBy(t => t.Genre   ?? ""),
            "Duration"    => tracks.OrderBy(t => t.Duration),
            _             => tracks.OrderBy(t => t.Album   ?? "").ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
        };
        return asc ? ordered : ordered.Reverse();
    }

    private static List<TrackRowViewModel> BuildRows(
        List<Track> tracks,
        bool groupByAlbum,
        Track? currentlyPlaying)
    {
        var result = new List<TrackRowViewModel>(tracks.Count);

        if (groupByAlbum)
        {
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
        }
        else
        {
            foreach (var track in tracks)
            {
                result.Add(new TrackRowViewModel
                {
                    Track              = track,
                    IsFirstInAlbumGroup = true,
                    AlbumGroupSize     = 1,
                    IsCurrentlyPlaying = track.Path == currentlyPlaying?.Path,
                });
            }
        }

        return result;
    }
}
