using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// An artist by name, as the Artists lists name them (Track.Artists): one
// artist whatever the case it is spelled in, which is how the catalog already
// counts them (CatalogIdentity folds case before it hashes an id). Grouping
// by the exact text instead listed "Add N to (X)" and "Add N To (X)" as two
// artists, one per album, and renaming one album's songs to match the other
// left the rest of the first album's still apart.
public static class ArtistNames
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) => Comparer.Equals(a, b);

    // Each artist once, under the spelling most of their songs carry - the
    // first in ordinal order on a tie, so a list is the same on every
    // rebuild - in the lists' alphabetical order.
    public static List<string> Of(IEnumerable<Track> tracks) =>
        tracks
            .Select(t => t.Artists)
            .Where(a => !string.IsNullOrEmpty(a))
            .GroupBy(a => a!, Comparer)
            .Select(g => g
                .GroupBy(a => a!, StringComparer.Ordinal)
                .OrderByDescending(spelling => spelling.Count())
                .ThenBy(spelling => spelling.Key, StringComparer.Ordinal)
                .First().Key)
            .OrderBy(a => a)
            .ToList();
}
