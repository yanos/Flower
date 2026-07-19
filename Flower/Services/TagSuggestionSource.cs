using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Distinct existing Artist/Album values across the library, for autocomplete
// suggestions in TrackInfoWindow's Artist/Album/Album Artist fields - lets
// retyping an existing name reuse the exact existing spelling instead of
// accidentally introducing a near-duplicate (e.g. "Beatles" vs "The
// Beatles"), which would otherwise silently fragment album/artist grouping
// elsewhere in the app (AlbumGridBuilder, RecentlyAddedAlbumsBuilder, sync).
public static class TagSuggestionSource
{
    // Union of Artists + AlbumArtists, not just one or the other - both fields
    // draw from the same real-world "who performed/owns this" namespace, so a
    // name that only ever appears as a track Artist should still suggest when
    // typing into AlbumArtist (e.g. promoting a track artist into the
    // AlbumArtist field for a compilation), and vice versa.
    public static List<string> DistinctArtists(IEnumerable<Track> tracks) =>
        tracks.SelectMany(t => new[] { t.Artists, t.AlbumArtists })
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .Distinct()
            .OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static List<string> DistinctAlbums(IEnumerable<Track> tracks) =>
        tracks.Select(t => t.Album)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .Distinct()
            .OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
