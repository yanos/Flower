using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// A cover found on the internet for an album that has none, or the wrong one -
// the phone Track Info's Find Online. Three searches at once, none needing a
// key or an account:
//
//   - Apple's iTunes Search API, in this device's own country's store and in
//     the US one. It serves artwork at any size up to the original, so one
//     search answers "which album is this" and "where is a large picture of
//     it" both - but only for what that store sells, and each country's store
//     sells a different catalogue: a Québec record, an out-of-print one or an
//     indie one is often in none of them.
//   - MusicBrainz, whose catalogue is what people have entered rather than
//     what anyone sells, checked against the Cover Art Archive for a front
//     cover. That is where the albums the stores lack were found: asked for
//     forty albums from a real library, iTunes alone came back empty for
//     eleven, and MusicBrainz had a cover for most of those.
//
// The results are ranked together (Rank), so a source only adds candidates
// that name the album. Null only when every search failed (no connection), as
// against finding nothing.
//
// Nothing found here is written anywhere by this class: it hands back
// candidates, and the sheet shows each one full screen for the user to take or
// pass over before AlbumArtEditor embeds it. A search by name will sometimes
// find the wrong album, and a cover is written into every song of it.
public sealed partial class OnlineCoverArt(ILogger logger)
{
    // One for the process, as HttpClient is meant to be used; these are plain
    // public GETs, so none of PeerHttpClient's signing applies.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // How long one source is waited for. The searches run side by side, so a
    // slow one holds up the answer by this much at most - and the others'
    // results are still shown.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    // MusicBrainz asks every client to say who it is, and refuses requests
    // that do not.
    private static readonly string UserAgent = $"Flower/{AppVersion.Display} ( https://github.com/yanos/Flower )";

    // The size asked of Apple's image server. The original is served whenever
    // it is smaller, so this is a ceiling, not an upscale: 1400 is what a
    // modern release's art is delivered at, and much more is megabytes
    // embedded into every file of the album for nothing a screen shows.
    public const int RequestedPixels = 1400;

    private const int MaxCandidates = 5;

    public sealed record Candidate(string Album, string Artist, string ArtworkUrl);

    // Searched as an album when there is an album name, and otherwise as the
    // song - whose result still carries its album's art. MusicBrainz is asked
    // only for an album: its covers belong to releases, not songs.
    public async Task<IReadOnlyList<Candidate>?> SearchAsync(string album, string artist, string title, CancellationToken ct = default)
    {
        var byAlbum = !string.IsNullOrWhiteSpace(album);
        var name = byAlbum ? album : title;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        var searches = Storefronts()
            .Select(country => SearchITunesAsync(country, name, artist, byAlbum, ct))
            .ToList();
        if (byAlbum)
            searches.Add(SearchMusicBrainzAsync(name, artist, ct));

        var answers = await Task.WhenAll(searches);
        ct.ThrowIfCancellationRequested();
        if (answers.All(found => found is null))
            return null;

        var all = answers.SelectMany(found => found ?? []).ToList();
        var ranked = Rank(all, name, artist);
        logger.LogInformation("Cover search for {Album} by {Artist}: {Found} results, {Kept} naming the album",
            name, artist, all.Count, ranked.Count);
        return ranked;
    }

    public async Task<byte[]> DownloadAsync(Candidate candidate, CancellationToken ct = default) =>
        await Http.GetByteArrayAsync(candidate.ArtworkUrl, ct);

    // This device's country's store first, then the US one, which is what the
    // search answers from when no country is named and the largest catalogue.
    // Just the US one when the region is the US or cannot be told.
    private static IEnumerable<string> Storefronts()
    {
        string? region;
        try
        {
            region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (Exception)
        {
            region = null;
        }

        if (region is { Length: 2 } && char.IsAsciiLetter(region[0]) && char.IsAsciiLetter(region[1])
            && !region.Equals("US", StringComparison.OrdinalIgnoreCase))
            yield return region.ToLowerInvariant();
        yield return "us";
    }

    private async Task<IReadOnlyList<Candidate>?> SearchITunesAsync(
        string country, string name, string artist, bool byAlbum, CancellationToken ct)
    {
        // The store's search wants every word it is given, so a name that
        // carries "(Disc 1)" or "[Remastered]" finds nothing; Rank still
        // tells the editions apart afterwards.
        var term = $"{artist} {BareName(name)}".Trim();
        var url = $"https://itunes.apple.com/search?media=music&entity={(byAlbum ? "album" : "song")}&limit=25&country={country}&term={Uri.EscapeDataString(term)}";
        return await FetchAsync($"the {country.ToUpperInvariant()} iTunes store", term, name, artist, ct,
            async token => Parse(await GetStringAsync(url, token), byAlbum));
    }

    // Release groups (an album across all its editions) named like this one
    // by this artist; then, for the few that rank as this album, whether the
    // Cover Art Archive has a front cover - which most do, and a candidate
    // without one would only be an empty screen.
    private async Task<IReadOnlyList<Candidate>?> SearchMusicBrainzAsync(string album, string artist, CancellationToken ct)
    {
        var query = MusicBrainzQuery(album, artist);
        var url = $"https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=10&query={Uri.EscapeDataString(query)}";
        return await FetchAsync("MusicBrainz", query, album, artist, ct, async token =>
        {
            var listed = ParseMusicBrainz(await GetStringAsync(url, token));
            var groups = Rank(listed, album, artist);
            var covers = await Task.WhenAll(groups.Select(group => FrontCoverAsync(group, ct, token)));
            logger.LogInformation("MusicBrainz listed {Listed} release groups for {Query}, {Covers} of {Matching} matching with a front cover",
                listed.Count, query, covers.Count(c => c is not null), groups.Count);
            return [.. covers.OfType<Candidate>()];
        });
    }

    // Each listing on its own: one the archive is slow to answer, or fails,
    // costs that candidate and not the albums MusicBrainz found beside it.
    private async Task<Candidate?> FrontCoverAsync(Candidate group, CancellationToken ct, CancellationToken limit)
    {
        try
        {
            using var response = await Http.GetAsync(group.ArtworkUrl, limit);
            // 404 is the archive's answer for a release group nobody has
            // uploaded a cover for.
            if (!response.IsSuccessStatusCode)
                return null;

            return ParseCoverArtListing(await response.Content.ReadAsStringAsync(limit)) is { } image
                ? group with { ArtworkUrl = image }
                : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not ask the Cover Art Archive about {Album}", group.Album);
            return null;
        }
    }

    // One source's search, under its own time limit; null - logged - when it
    // could not be asked, so the others still answer. What it was asked
    // (`query`, as sent) and how much came back are logged either way, since a
    // search that finds nothing is otherwise indistinguishable from one that
    // asked the wrong question.
    private async Task<IReadOnlyList<Candidate>?> FetchAsync(
        string source, string query, string name, string artist, CancellationToken ct,
        Func<CancellationToken, Task<IReadOnlyList<Candidate>>> search)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SourceTimeout);
        try
        {
            var found = await search(limit.Token);
            logger.LogInformation("Searched {Source} for {Query}: {Count} results", source, query, found.Count);
            return found;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not search {Source} for the cover of {Album} by {Artist}", source, name, artist);
            return null;
        }
    }

    // MusicBrainz answers 503 to a client asking more than once a second,
    // which two searches in quick succession can - so that is waited out
    // once.
    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.TryParseAdd(UserAgent);
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.1), ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

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

    // A MusicBrainz search: the release group's name, its credited artists as
    // written ("Hangedup & Tony Conrad"), and - standing in for the artwork
    // until FrontCoverAsync asks - the Cover Art Archive's listing for it.
    public static IReadOnlyList<Candidate> ParseMusicBrainz(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<Candidate>();
        foreach (var group in groups.EnumerateArray())
        {
            var id = StringOf(group, "id");
            var name = StringOf(group, "title");
            if (id is null || name is null)
                continue;

            var artist = new StringBuilder();
            if (group.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in credits.EnumerateArray())
                    artist.Append(StringOf(credit, "name")).Append(StringOf(credit, "joinphrase"));
            }
            found.Add(new Candidate(name, artist.ToString(), $"https://coverartarchive.org/release-group/{id}"));
        }
        return found;
    }

    // The Cover Art Archive's listing for a release group: its front cover, at
    // the largest of the sizes the archive renders that is not the original
    // upload (which can be a 30MB scan). Over HTTPS, which the listing does not
    // always say and a phone insists on.
    public static string? ParseCoverArtListing(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("front", out var front) || front.ValueKind != JsonValueKind.True)
                continue;

            var url = image.TryGetProperty("thumbnails", out var sizes) && sizes.ValueKind == JsonValueKind.Object
                ? StringOf(sizes, "1200") ?? StringOf(sizes, "large") ?? StringOf(image, "image")
                : StringOf(image, "image");
            if (url is null)
                continue;

            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url["http://".Length..] : url;
        }
        return null;
    }

    // The album's name as a phrase, and the first of its artists as another:
    // MusicBrainz indexes a credit word by word without its "&", so "Hangedup
    // and Tony Conrad" as a phrase matches nothing, while "Hangedup" matches
    // the credit it leads. The album phrase is what narrows it.
    public static string MusicBrainzQuery(string album, string artist)
    {
        var query = $"releasegroup:\"{Phrase(BareName(album))}\"";
        var lead = LeadArtist(artist);
        return lead.Length > 0 ? $"{query} AND artist:\"{Phrase(lead)}\"" : query;
    }

    public static string LeadArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return "";

        var bare = Bracketed().Replace(artist, "");
        return ArtistSeparator().Split(bare).Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
    }

    private static string Phrase(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex(@"\s*(?:[,;&/+]|\bfeat\b\.?|\bft\b\.?|\bfeaturing\b|\bwith\b|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Where a cover comes from, to say beside it: the artwork's host without
    // the server in front of the domain - "mzstatic.com" rather than
    // "is1-ssl.mzstatic.com", "coverartarchive.org". Null for an address that
    // has no host.
    public static string? SourceDomain(string artworkUrl)
    {
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;

        var labels = uri.Host.Split('.');
        return labels.Length <= 2 || uri.HostNameType != UriHostNameType.Dns
            ? uri.Host
            : string.Join('.', labels[^2..]);
    }

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

    // Lower case, accents off, the editions off (BareName), then nothing but
    // letters and digits: "Rumours (Super Deluxe)" and "rumours" are one
    // album, and so are "69 Love Songs Disc 1" and "69 Love Songs".
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var bare = BareName(text);
        var builder = new StringBuilder(bare.Length);
        foreach (var c in bare.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    // A name without what a store or a rip adds to it that a library's tags
    // usually lack, or the other way round - "(Deluxe Edition)",
    // "[Remastered]", " - Single", "Disc 2". A name that is nothing but those -
    // "[Untitled]" - keeps its own words, out of their brackets, rather than
    // matching every other such name as nothing.
    public static string BareName(string text)
    {
        var bare = DiscSuffix().Replace(StoreSuffix().Replace(Bracketed().Replace(text, ""), ""), "").Trim();
        return bare.Length > 0 ? bare : Brackets().Replace(text, "").Trim();
    }

    [GeneratedRegex(@"[\s,:\-]*\b(?:Disc|Disk|CD)\s*\d+(?:\s*(?:of|/)\s*\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscSuffix();

    [GeneratedRegex(@"[\(\)\[\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+-\s+(Single|EP)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreSuffix();
}
