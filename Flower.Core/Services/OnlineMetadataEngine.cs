using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// What Flower looks up on the internet for a library that does not carry it
// (docs/ONLINE-METADATA-PLAN.md): so far, a picture of each artist, from
// Deezer. One piece of code that runs on whichever host owns the song - in
// the app for a device on its own (LocalOnlineMetadata), and on the server for
// everything paired to it, which asks over /api/flower/v1/artist-image
// (ServerOnlineMetadata). Nothing here knows which of the two it is in.
//
// Keyless, like OnlineCoverArt: nobody, owner or listener, signs up for
// anything. The owner's one switch (MusicLibrarySettings.LookUpOnline) is
// `enabled`, asked on every lookup so that turning it off takes effect at
// once; what is already cached is still served, since it was fetched while
// the switch was on and serving it asks nobody anything.
public sealed class OnlineMetadataEngine
{
    private const string DeezerApi = "https://api.deezer.com";

    // How long an artist Deezer has no picture of is believed to have none.
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(30);

    // Deezer allows fifty requests in five seconds from one address, and
    // answers the fifty-first with an error. A phone scrolling its Artists
    // tab asks for every row that passes, so the requests are spaced out
    // rather than sent as they come: one at a time, this far apart. Internal
    // so a test does not wait it out.
    internal static TimeSpan DeezerSpacing = TimeSpan.FromMilliseconds(125);

    private readonly HttpClient _http;
    private readonly ArtistPictureCache _pictures;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;

    // One lookup per artist at a time: an artist page and that artist's row
    // asking at once, or several devices asking the server for the same
    // artist, share one search rather than each making their own.
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _looking = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _deezerTurn = new(1, 1);
    private DateTimeOffset _nextDeezerRequest;

    public OnlineMetadataEngine(HttpClient http, string cacheDirectory, Func<bool> enabled,
        ILogger<OnlineMetadataEngine> logger, TimeProvider? clock = null)
    {
        _http = http;
        _pictures = new ArtistPictureCache(cacheDirectory, MissLifetime, clock);
        _enabled = enabled;
        _logger = logger;
    }

    public bool Enabled => _enabled();

    // The artist's picture: the bytes of an image, empty when there is none
    // to be had - not on Deezer, not told apart from a namesake, or lookups
    // switched off - and null when it could not be found out just now (no
    // connection, Deezer refusing), which is worth asking again later and
    // so is not remembered. The same three answers AlbumArtLoader gives for
    // a cover.
    //
    // albums are the names of the library's albums by this artist, which is
    // how the right one of several same-named artists is picked
    // (DeezerArtistSearch.Choose).
    //
    // ct lets a caller stop waiting, not the lookup stop: another caller may
    // be waiting on the same one, and a picture found is worth keeping for
    // the next time the row is drawn.
    public async Task<byte[]?> ArtistPictureAsync(string artist, IReadOnlyCollection<string> albums, CancellationToken ct = default)
    {
        var key = MetadataNames.Normalize(artist);
        if (key.Length == 0)
            return [];

        var (state, picture) = _pictures.Read(key);
        if (state == ArtistPictureCache.State.Found)
            return picture;
        if (state == ArtistPictureCache.State.None || !_enabled())
            return [];

        var lookup = _looking.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => LookUpArtistPictureAsync(artist, key, albums)));
        return await lookup.Value.WaitAsync(ct);
    }

    private async Task<byte[]?> LookUpArtistPictureAsync(string artist, string key, IReadOnlyCollection<string> albums)
    {
        try
        {
            return await TryLookUpArtistPictureAsync(artist, key, albums);
        }
        finally
        {
            // However it ended, so the next caller reads the cache - or,
            // after a failure, asks again.
            _looking.TryRemove(key, out _);
        }
    }

    private async Task<byte[]?> TryLookUpArtistPictureAsync(string artist, string key, IReadOnlyCollection<string> albums)
    {
        try
        {
            var url = await FindArtistPictureAsync(artist, albums);
            if (url == null)
            {
                _logger.LogInformation("Deezer has no picture of {Artist} that could be told apart from a namesake", artist);
                _pictures.WriteNone(key);
                return [];
            }

            var picture = await _http.GetByteArrayAsync(url);
            if (LocalAlbumArtReader.MimeTypeForBytes(picture) == null)
                throw new HttpRequestException($"The picture at {url} was {picture.Length} bytes of something that is not an image");

            _logger.LogInformation("Found a picture of {Artist} on Deezer", artist);
            _pictures.WriteFound(key, picture);
            return picture;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up a picture of {Artist} on Deezer", artist);
            return null;
        }
    }

    // The picture's address, or null for none. Searched by the whole name,
    // so a collaboration ("Hangedup & Tony Conrad") finds nobody of that
    // exact name and keeps its collage.
    private async Task<string?> FindArtistPictureAsync(string artist, IReadOnlyCollection<string> albums)
    {
        var found = DeezerArtistSearch.ParseArtists(
            await GetDeezerAsync($"/search/artist?q={Uri.EscapeDataString(artist)}&limit=10"));
        var candidates = DeezerArtistSearch.Candidates(found, artist);
        if (candidates.Count == 0)
            return null;

        // With no album to compare there is nothing the albums would show,
        // so they are not asked for (see DeezerArtistSearch.Choose).
        var candidateAlbums = new List<IReadOnlyList<string>>();
        if (albums.Count > 0)
        {
            foreach (var candidate in candidates)
                candidateAlbums.Add(DeezerArtistSearch.ParseAlbumTitles(
                    await GetDeezerAsync($"/artist/{candidate.Id}/albums?limit=100")));
        }

        return DeezerArtistSearch.Choose(candidates, candidateAlbums, albums)?.PictureUrl;
    }

    private async Task<string> GetDeezerAsync(string pathAndQuery)
    {
        await _deezerTurn.WaitAsync();
        try
        {
            var wait = _nextDeezerRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
            _nextDeezerRequest = DateTimeOffset.UtcNow + DeezerSpacing;

            using var response = await _http.GetAsync(DeezerApi + pathAndQuery);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        finally
        {
            _deezerTurn.Release();
        }
    }
}
