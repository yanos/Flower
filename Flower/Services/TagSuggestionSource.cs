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
// elsewhere in the app (AlbumGridBuilder, CatalogIdentity, sync).
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

    public static List<string> DistinctComposers(IEnumerable<Track> tracks) =>
        tracks.Select(t => t.Composers)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct()
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // For the phone's Track Info genre menu, the whole list behind a chevron,
    // and the suggestions under its box.
    public static List<string> DistinctGenres(IEnumerable<Track> tracks) =>
        tracks.Select(t => t.Genre)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!)
            .Distinct()
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // What the phone shows under a box as it is typed in: names starting with
    // the text first, then names containing it, never the text itself, at
    // most a handful - a row of them, not a list to scroll.
    public static List<string> Matching(IReadOnlyList<string> pool, string? typed, int limit = 6)
    {
        var text = typed?.Trim();
        if (string.IsNullOrEmpty(text))
            return [];

        var starts = pool.Where(p => p.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        var contains = pool.Where(p => !p.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)
                                       && p.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        return starts.Concat(contains)
            .Where(p => !string.Equals(p, text, StringComparison.CurrentCulture))
            .Take(limit)
            .ToList();
    }
}
