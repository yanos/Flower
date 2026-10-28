using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Flower.Services;

// Which of Deezer's artists is the one in the library, for their picture -
// the matching half of OnlineMetadataEngine.ArtistPictureAsync, kept apart
// from the network so the tests can reach it.
//
// A search by name finds everyone with that name and everyone whose name
// shares a word with it, and a picture of the wrong person is the most visible
// mistake an artist page can make. So a name is not enough: asked for
// "Nirvana", Deezer answers with Nirvana and with Nirvana (UK), whose names
// normalise alike, and for "Ohmme" with two different acts both called Ohmme.
// What tells them apart is the records: the artist kept is the one whose
// albums on Deezer include albums the library has by them (Choose).
//
// Read by hand rather than deserialized, so the trimmed NativeAOT build needs
// no contract for it - the same as OnlineCoverArt's parsers.
public static class DeezerArtistSearch
{
    // How many same-named artists are looked into, each costing a request
    // for its albums. Two people sharing a name is common; four is not.
    public const int MaxCandidates = 3;

    public sealed record Artist(long Id, string Name, string? PictureUrl);

    // The artists a search answered with, each with its largest picture
    // (picture_xl, 1000x1000) - or none, for an artist Deezer has no photo
    // of. Deezer still sends a URL then, to a grey silhouette; it is the one
    // whose image hash is empty, or the hash of nothing.
    public static IReadOnlyList<Artist> ParseArtists(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ThrowIfRefused(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<Artist>();
        foreach (var artist in data.EnumerateArray())
        {
            var name = StringOf(artist, "name");
            if (name is null || !artist.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var id))
                continue;
            found.Add(new Artist(id, name, RealPicture(StringOf(artist, "picture_xl"))));
        }
        return found;
    }

    // The titles of an artist's albums, from /artist/<id>/albums.
    public static IReadOnlyList<string> ParseAlbumTitles(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ThrowIfRefused(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Select(album => StringOf(album, "title"))
            .OfType<string>()
            .ToList();
    }

    // The ones worth looking into: named as the library names them, once
    // normalised (MetadataNames.Normalize - case, accents, "The" kept, a
    // bracketed "(UK)" dropped), and with a picture to show. In Deezer's
    // order, which is roughly by how many people follow them.
    public static IReadOnlyList<Artist> Candidates(IReadOnlyList<Artist> found, string artist)
    {
        var wanted = MetadataNames.Normalize(artist);
        if (wanted.Length == 0)
            return [];

        return found
            .Where(a => a.PictureUrl != null && MetadataNames.Normalize(a.Name) == wanted)
            .Take(MaxCandidates)
            .ToList();
    }

    // The candidate whose Deezer albums share the most with the library's
    // albums by this artist (candidateAlbums[i] being candidates[i]'s), or
    // null when none shares any - a picture of somebody else with the same
    // name is worse than the cover collage the page shows without one.
    //
    // The one exception is an artist the library has no album by at all,
    // only songs on none: there is nothing to compare, and a single artist
    // of that exact name is taken. Two are not - there is no telling which.
    public static Artist? Choose(
        IReadOnlyList<Artist> candidates, IReadOnlyList<IReadOnlyList<string>> candidateAlbums,
        IReadOnlyCollection<string> libraryAlbums)
    {
        var wanted = libraryAlbums
            .Select(MetadataNames.Normalize)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return candidates.Count == 1 ? candidates[0] : null;

        return candidates
            .Select((candidate, order) => (
                candidate,
                order,
                shared: order < candidateAlbums.Count ? candidateAlbums[order].Count(title => SameAlbum(title, wanted)) : 0))
            .Where(c => c.shared > 0)
            .OrderByDescending(c => c.shared)
            .ThenBy(c => c.order)
            .Select(c => c.candidate)
            .FirstOrDefault();
    }

    // One of the library's albums, by its normalised name, or one whose name
    // holds the other's - "OK Computer OKNOTOK 1997 2017" is OK Computer. Not
    // for a name too short to mean anything inside another one: "Up" is in a
    // great many titles.
    private const int ShortestContainedName = 4;

    private static bool SameAlbum(string title, HashSet<string> wanted)
    {
        var name = MetadataNames.Normalize(title);
        if (name.Length == 0)
            return false;
        if (wanted.Contains(name))
            return true;

        return wanted.Any(w =>
            Math.Min(w.Length, name.Length) >= ShortestContainedName
            && (w.Contains(name, StringComparison.Ordinal) || name.Contains(w, StringComparison.Ordinal)));
    }

    // The empty image hash, and the MD5 of nothing: the two ways Deezer has
    // addressed the silhouette it shows for an artist with no photo.
    private static string? RealPicture(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || url.Contains("/artist//", StringComparison.Ordinal)
            || url.Contains("d41d8cd98f00b204e9800998ecf8427e", StringComparison.Ordinal))
            return null;

        return url;
    }

    // Deezer refuses with an HTTP 200 carrying {"error": {...}} - over its
    // quota of fifty requests in five seconds, most often. That is not the
    // artist being unknown, and must not be remembered as if it were.
    private static void ThrowIfRefused(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            throw new DeezerRefusedException(StringOf(error, "message") ?? error.ToString());
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class DeezerRefusedException(string message) : Exception($"Deezer refused the request: {message}");
