using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
// Track Info's Find Online. Several searches at once, none needing a key or
// an account:
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
//   - Bandcamp, which is where an independent label keeps its catalogue -
//     often its whole website, and otherwise the store it links to - and
//     where a self-released record is sold when it is sold anywhere. Its
//     search is the one the site's own search box uses, and finds an album
//     whether it sits on the label's page or the artist's. A label's own
//     website is not searched: there is no knowing where it is from the
//     Publisher tag, nor where on it an album's cover would be.
//   - Deezer, whose catalogue comes from the labels and distributors much as
//     Apple's does but is not the same one: it had "Oil & Blood", whose
//     MusicBrainz entry has no picture, and Mellon Collie's own covers, which
//     Apple's search does not answer with. Its covers are 1000 pixels.
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
    private static readonly string UserAgent = OnlineLookup.UserAgent;

    // The size asked of Apple's image server. The original is served whenever
    // it is smaller, so this is a ceiling, not an upscale: 1400 is what a
    // modern release's art is delivered at, and much more is megabytes
    // embedded into every file of the album for nothing a screen shows.
    public const int RequestedPixels = 1400;

    private const int MaxCandidates = 5;

    // Year is the release's, when the source says (iTunes and MusicBrainz do,
    // Bandcamp's search does not). Confidence is what Rank made of it, and
    // orders the downloads ahead of their size.
    public sealed record Candidate(string Album, string Artist, string ArtworkUrl, int? Year = null)
    {
        public int Confidence { get; init; }
    }

    // Searched as an album when there is an album name, and otherwise as the
    // song - whose result still carries its album's art. MusicBrainz and
    // Bandcamp are asked only for an album: their covers belong to releases,
    // not songs. Each store is also asked for the name alone, since its
    // search wants every word and a tag's album artist is not always the
    // store's: "Alastor" is John Zorn's in the tags and Eyvind Kang's at
    // Apple. The year only orders what matches.
    public async Task<IReadOnlyList<Candidate>?> SearchAsync(
        string album, string artist, string title, string? year = null, CancellationToken ct = default)
    {
        var byAlbum = !string.IsNullOrWhiteSpace(album);
        var name = byAlbum ? album : title;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        var searches = Storefronts()
            .Select(country => SearchITunesAsync(country, name, artist, byAlbum, ct))
            .ToList();
        if (!string.IsNullOrWhiteSpace(artist))
            searches.AddRange(Storefronts().Select(country => SearchITunesAsync(country, name, "", byAlbum, ct)));
        if (byAlbum)
        {
            searches.Add(SearchMusicBrainzAsync(name, artist, ct));
            searches.Add(SearchBandcampAsync(name, artist, ct));
            searches.Add(SearchDeezerAsync(name, artist, ct));
            if (!string.IsNullOrWhiteSpace(artist))
                searches.Add(SearchDeezerAsync(name, "", ct));
        }

        var answers = await Task.WhenAll(searches);
        ct.ThrowIfCancellationRequested();
        if (answers.All(found => found is null))
            return null;

        var all = answers.SelectMany(found => found ?? []).ToList();
        var ranked = Rank(all, name, artist, YearOf(year));
        logger.LogInformation("Cover search for {Album} by {Artist}: {Found} results, {Kept} naming the album",
            name, artist, all.Count, ranked.Count);
        return ranked;
    }

    public sealed record Download(Candidate Candidate, byte[] Bytes);

    // Every cover found, fetched at once and offered largest first (Largest):
    // which is biggest is only known once it has arrived, since a source's
    // size is a ceiling and the original is served when it is smaller. There
    // are at most MaxCandidates of them, so the wait is the slowest one's and
    // paging between them afterwards costs nothing. One that will not
    // download is left out rather than offered as an empty page.
    public async Task<IReadOnlyList<Download>> DownloadAllAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct = default)
    {
        var downloads = await Task.WhenAll(candidates.Select(candidate => TryDownloadAsync(candidate, ct)));
        ct.ThrowIfCancellationRequested();
        return Largest(downloads.OfType<Download>().ToList());
    }

    private async Task<Download?> TryDownloadAsync(Candidate candidate, CancellationToken ct)
    {
        try
        {
            return new Download(candidate, await Http.GetByteArrayAsync(candidate.ArtworkUrl, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not download the cover found for {Album}", candidate.Album);
            return null;
        }
    }

    // The best match first, and among equally good ones the most pixels, by
    // the image header alone; equal ones keep Rank's order, and a picture
    // whose size cannot be read goes last. Size alone put five albums that
    // merely shared "Mono No Aware"'s name ahead of the one it was.
    public static IReadOnlyList<Download> Largest(IReadOnlyList<Download> downloads) =>
        downloads
            .OrderByDescending(download => download.Candidate.Confidence)
            .ThenByDescending(download => PixelsIn(download.Bytes))
            .ToList();

    private static long PixelsIn(byte[] bytes) =>
        ArtworkDimensions.Of(bytes) is { IsNone: false } size ? (long)size.Width * size.Height : 0;

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
        var term = $"{artist} {MetadataNames.BareName(name)}".Trim();
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

    // Albums only, by the words a person would type into Bandcamp's own
    // search box - which is what this asks, through the endpoint that box
    // calls. It answers a POST of JSON, with its best few matches.
    private async Task<IReadOnlyList<Candidate>?> SearchBandcampAsync(string album, string artist, CancellationToken ct)
    {
        var term = $"{MetadataNames.LeadArtist(artist)} {MetadataNames.BareName(album)}".Trim();
        var body = BandcampSearchBody(term);
        return await FetchAsync("Bandcamp", term, album, artist, ct, async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BandcampSearchUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.TryParseAdd(UserAgent);
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return ParseBandcamp(await response.Content.ReadAsStringAsync(token));
        });
    }

    // Albums, by the words a person would type, as Bandcamp is asked; like
    // Apple's, its search finds little when the credit it is given is not
    // the one it has ("Jamie Saft Trio" for what it credits to John Zorn), so
    // the name alone is asked too. Its own query syntax (album:"...") wants
    // the name exactly as it has it, which is what failed elsewhere.
    private async Task<IReadOnlyList<Candidate>?> SearchDeezerAsync(string album, string artist, CancellationToken ct)
    {
        var term = $"{MetadataNames.LeadArtist(artist)} {MetadataNames.BareName(album)}".Trim();
        var url = $"https://api.deezer.com/search/album?limit=25&q={Uri.EscapeDataString(term)}";
        return await FetchAsync("Deezer", term, album, artist, ct,
            async token => ParseDeezer(await GetStringAsync(url, token)));
    }

    // What Bandcamp's search box sends: "a" for albums only, and no signed-in
    // fan whose purchases would otherwise come first. Written by hand, as the
    // answers are read, so the trimmed NativeAOT build needs no contract.
    private static string BandcampSearchBody(string term)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("search_text", term);
            json.WriteString("search_filter", "a");
            json.WriteBoolean("full_page", false);
            json.WriteNull("fan_id");
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private const string BandcampSearchUrl = "https://bandcamp.com/api/bcsearch_public_api/1/autocomplete_elastic";

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
    // and also whenever it is busy - a sixth of forty-odd searches spaced
    // well apart once - so that is waited out a few times, within the
    // source's own time limit.
    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.TryParseAdd(UserAgent);
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.1 * attempt), ct);
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
            found.Add(new Candidate(name, StringOf(result, "artistName") ?? "", LargeArtworkUrl(art), YearOf(StringOf(result, "releaseDate"))));
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
            found.Add(new Candidate(name, artist.ToString(), $"https://coverartarchive.org/release-group/{id}",
                YearOf(StringOf(group, "first-release-date"))));
        }
        return found;
    }

    // A Bandcamp search: each album's name, who it is by, and its cover at
    // 1200 pixels (the "_10" rendition). The "img" it answers with is a
    // 100-pixel thumbnail of the same picture, so the cover is addressed by
    // its art_id instead; the original ("_0") can be a large scan, as at the
    // Cover Art Archive.
    public static IReadOnlyList<Candidate> ParseBandcamp(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("auto", out var auto)
            || !auto.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<Candidate>();
        foreach (var result in results.EnumerateArray())
        {
            var name = StringOf(result, "name");
            if (StringOf(result, "type") != "a" || name is null
                || !result.TryGetProperty("art_id", out var art) || art.ValueKind != JsonValueKind.Number
                || !art.TryGetInt64(out var artId))
                continue;
            found.Add(new Candidate(name, StringOf(result, "band_name") ?? "", $"https://f4.bcbits.com/img/a{artId}_10.jpg"));
        }
        return found;
    }

    // A Deezer album search: each album's name, its main artist, and its
    // cover at 1000 pixels ("cover_xl"). Its image server will render other
    // sizes, but past the original it upscales, so the size it names is the
    // one taken. An answer that is an error (a quota, say) has no "data".
    public static IReadOnlyList<Candidate> ParseDeezer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<Candidate>();
        foreach (var album in data.EnumerateArray())
        {
            var name = StringOf(album, "title");
            var art = StringOf(album, "cover_xl") ?? StringOf(album, "cover_big");
            if (name is null || art is null)
                continue;

            var artist = album.TryGetProperty("artist", out var credit) && credit.ValueKind == JsonValueKind.Object
                ? StringOf(credit, "name") ?? ""
                : "";
            found.Add(new Candidate(name, artist, art));
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

    // The album's words, any of them, and the first of its artists as a
    // phrase: MusicBrainz indexes a credit word by word without its "&", so
    // "Hangedup and Tony Conrad" as a phrase matches nothing, while
    // "Hangedup" matches the credit it leads. The album went as a phrase
    // once, which found nothing whose words were in another order or form -
    // "Astaroth: Book of Angels, Volume 1" for "Book of Angels, Vol. 01 -
    // Astaroth" - and Rank already decides which answers are the album.
    // Compilations are credited to "Various Artists", which is no narrowing.
    public static string MusicBrainzQuery(string album, string artist)
    {
        // Lower case, so a word such as "And" or "Not" is not read as an operator.
        var words = string.Join(' ', QueryWord().Matches(MetadataNames.BareName(album)).Select(word => word.Value.ToLowerInvariant()));
        var query = words.Length > 0 ? $"releasegroup:({words})" : $"releasegroup:\"{Phrase(MetadataNames.BareName(album))}\"";
        var lead = MetadataNames.LeadArtist(artist);
        return lead.Length > 0 && !MetadataNames.IsVariousArtists(lead) ? $"{query} AND artist:\"{Phrase(lead)}\"" : query;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex QueryWord();

    // The year a date or a tag starts with ("1995-10-23", "1995").
    public static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 0
            ? year
            : null;

    private static string Phrase(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
    // artist's album of the same name; then on the year, so that of several
    // albums sharing a name and no artist the one from the tag's year - or
    // one whose year is not known - comes before those from another.
    // Duplicate artwork - an album released as both clean and explicit - is
    // shown once.
    public static IReadOnlyList<Candidate> Rank(IReadOnlyList<Candidate> found, string name, string artist, int? year = null)
    {
        var wanted = new Wanted(name, artist);
        if (wanted.Name.Length == 0)
            return [];

        return found
            .Select((candidate, order) => (candidate, order, score: Score(candidate, wanted)))
            .Where(c => c.score > 0)
            .Select(c => (c.candidate, c.order, confidence: c.score * 3 + YearScore(c.candidate.Year, year)))
            .OrderByDescending(c => c.confidence)
            .ThenBy(c => c.order)
            .Select(c => c.candidate with { Confidence = c.confidence })
            .DistinctBy(c => c.ArtworkUrl)
            .Take(MaxCandidates)
            .ToList();
    }

    private sealed class Wanted(string name, string artist)
    {
        public string Name { get; } = MetadataNames.Normalize(name);
        public IReadOnlySet<string> Words { get; } = MetadataNames.Words(name);
        public IReadOnlyList<string> Artists { get; } = MetadataNames.Artists(artist);
        public bool IsCompilation { get; } = MetadataNames.IsVariousArtists(artist);
        public bool HasArtist => Artists.Count > 0;
    }

    private static int YearScore(int? found, int? wanted) =>
        found is null || wanted is null ? 1
        : found == wanted ? 2
        : 0;

    // Fewer words than this, and a name is too common to be this album when
    // someone else's: asked for "Blood" or "Varia" by name alone, a store
    // answers with a dozen strangers'.
    private const int DistinctiveWords = 3;

    // The name, as written or word for word in any order: the same is 2, one
    // inside the other ("Rumours" in "Rumours Live", "Mellon Collie and the
    // Infinite Sadness" in its first disc's "...: Dawn to Dusk") is 1. Any
    // one artist of either credit in the other's adds 2. When the tags name
    // an artist, someone else's album counts only by the very same name of
    // a few words: the tags' artist is not always the store's ("Alastor" is
    // John Zorn's in the tags and Eyvind Kang's at Apple), but someone
    // else's album merely containing the name is not this one.
    private static int Score(Candidate candidate, Wanted wanted)
    {
        var name = MetadataNames.Normalize(candidate.Album);
        if (name.Length == 0)
            return 0;

        var words = MetadataNames.Words(candidate.Album);
        var nameScore = name == wanted.Name || (words.Count > 0 && words.SetEquals(wanted.Words)) ? 2
            : name.Contains(wanted.Name, StringComparison.Ordinal) || wanted.Name.Contains(name, StringComparison.Ordinal)
              || (words.Count > 0 && wanted.Words.Count > 0 && (words.IsSubsetOf(wanted.Words) || words.IsSupersetOf(wanted.Words))) ? 1
            : 0;
        if (nameScore == 0)
            return 0;

        if (SameArtist(candidate, wanted))
            return nameScore + 2;
        return !wanted.HasArtist || (nameScore == 2 && wanted.Words.Count >= DistinctiveWords) ? nameScore : 0;
    }

    private static bool SameArtist(Candidate candidate, Wanted wanted)
    {
        if (wanted.IsCompilation)
            return MetadataNames.IsVariousArtists(candidate.Artist) || CompilationName().IsMatch(candidate.Album);

        var artists = MetadataNames.Artists(candidate.Artist);
        return wanted.Artists.Any(mine => artists.Any(theirs => Within(mine, theirs) || Within(theirs, mine)));

        // "Smashing Pumpkins" in "The Smashing Pumpkins"; but not a short
        // name inside any other, as "Can" is in "Pelican".
        static bool Within(string part, string whole) =>
            part == whole || (part.Length >= 4 && whole.Contains(part, StringComparison.Ordinal));
    }

    // Bandcamp credits a compilation to its label and names it "V/A - ...".
    [GeneratedRegex(@"^\s*(?:V/A|VA|Various Artists)\s*[-–:]", RegexOptions.IgnoreCase)]
    private static partial Regex CompilationName();
}
