using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// A cover found on the internet for an album that has none, or the wrong one -
// the phone Track Info's Find Online. Asks Apple's public iTunes Search API,
// which needs no key or account and serves its artwork at any size up to the
// original, so one search answers "which album is this" and "where is a large
// picture of it" both.
//
// Nothing found here is written anywhere by this class: it hands back
// candidates, and the sheet shows each one full screen for the user to take or
// pass over before AlbumArtEditor embeds it. A search by name will sometimes
// find the wrong album, and a cover is written into every song of it.
public sealed partial class OnlineCoverArt(ILogger logger)
{
    // One for the process, as HttpClient is meant to be used; the search is a
    // plain public GET, so none of PeerHttpClient's signing applies.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // The size asked of Apple's image server. The original is served whenever
    // it is smaller, so this is a ceiling, not an upscale: 1400 is what a
    // modern release's art is delivered at, and much more is megabytes
    // embedded into every file of the album for nothing a screen shows.
    public const int RequestedPixels = 1400;

    private const int MaxCandidates = 5;

    public sealed record Candidate(string Album, string Artist, string ArtworkUrl);

    // Searched as an album when there is an album name, and otherwise as the
    // song - whose result still carries its album's art. Null when the search
    // itself failed (no connection, a timeout), as against finding nothing.
    public async Task<IReadOnlyList<Candidate>?> SearchAsync(string album, string artist, string title, CancellationToken ct = default)
    {
        var byAlbum = !string.IsNullOrWhiteSpace(album);
        var name = byAlbum ? album : title;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        var term = Uri.EscapeDataString($"{artist} {name}".Trim());
        var url = $"https://itunes.apple.com/search?media=music&entity={(byAlbum ? "album" : "song")}&limit=25&term={term}";
        try
        {
            var json = await Http.GetStringAsync(url, ct);
            return Rank(Parse(json, byAlbum), name, artist);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not search online for the cover of {Album} by {Artist}", name, artist);
            return null;
        }
    }

    public async Task<byte[]> DownloadAsync(Candidate candidate, CancellationToken ct = default) =>
        await Http.GetByteArrayAsync(candidate.ArtworkUrl, ct);

    // The results the search answered with, as candidates: each one's name
    // (the album's, or for a song search the song's own, which is what it is
    // matched on) and its artwork at RequestedPixels. Read by hand rather than
    // deserialized, so the trimmed NativeAOT build needs no contract for it.
    public static IReadOnlyList<Candidate> Parse(string json, bool byAlbum)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<Candidate>();
        foreach (var result in results.EnumerateArray())
        {
            var art = StringOf(result, "artworkUrl100") ?? StringOf(result, "artworkUrl60");
            var name = StringOf(result, byAlbum ? "collectionName" : "trackName");
            if (art is null || name is null)
                continue;
            found.Add(new Candidate(name, StringOf(result, "artistName") ?? "", LargeArtworkUrl(art)));
        }
        return found;
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Apple's thumbnail URLs name their size in the last path segment
    // (".../source/100x100bb.jpg"), and the image server renders whatever size
    // that segment asks for.
    public static string LargeArtworkUrl(string thumbnailUrl) =>
        SizeSegment().Replace(thumbnailUrl, $"/{RequestedPixels}x{RequestedPixels}bb", 1);

    [GeneratedRegex(@"/\d+x\d+bb(?=[.\-])", RegexOptions.RightToLeft)]
    private static partial Regex SizeSegment();

    // The results that name the album being looked for, best first; a search
    // by name answers with everything that shares a word with it, and none of
    // those are this album's cover. Matching on the name alone, and then on
    // the artist too, so a remaster of the same album comes before a different
    // artist's album of the same name. Duplicate artwork - an album released
    // as both clean and explicit - is shown once.
    public static IReadOnlyList<Candidate> Rank(IReadOnlyList<Candidate> found, string name, string artist)
    {
        var wantedName = Normalize(name);
        var wantedArtist = Normalize(artist);
        if (wantedName.Length == 0)
            return [];

        return found
            .Select((candidate, order) => (candidate, order, score: Score(candidate, wantedName, wantedArtist)))
            .Where(c => c.score > 0)
            .OrderByDescending(c => c.score)
            .ThenBy(c => c.order)
            .Select(c => c.candidate)
            .DistinctBy(c => c.ArtworkUrl)
            .Take(MaxCandidates)
            .ToList();
    }

    private static int Score(Candidate candidate, string wantedName, string wantedArtist)
    {
        var name = Normalize(candidate.Album);
        var nameScore = name == wantedName ? 2
            : name.Contains(wantedName, StringComparison.Ordinal) || wantedName.Contains(name, StringComparison.Ordinal) ? 1
            : 0;
        if (nameScore == 0 || name.Length == 0)
            return 0;

        var artist = Normalize(candidate.Artist);
        var artistScore = wantedArtist.Length > 0 && artist.Length > 0
            && (artist.Contains(wantedArtist, StringComparison.Ordinal) || wantedArtist.Contains(artist, StringComparison.Ordinal))
            ? 2
            : 0;
        return nameScore + artistScore;
    }

    // Lower case, accents off, and what a store adds to a name that a library
    // usually lacks - "(Deluxe Edition)", "[Remastered]", " - Single" - taken
    // off, then nothing but letters and digits: "Rumours (Super Deluxe)" and
    // "rumours" are one album.
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var stripped = StoreSuffix().Replace(Bracketed().Replace(text, ""), "");
        var builder = new StringBuilder(stripped.Length);
        foreach (var c in stripped.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        // A name that is nothing but a bracket - "[Untitled]" - keeps its own
        // letters rather than matching every other such name as "".
        return builder.Length > 0 || stripped.Length == text.Length ? builder.ToString() : Normalize(text.Replace("(", "").Replace("[", ""));
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+-\s+(Single|EP)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreSuffix();
}
