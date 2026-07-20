using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Services;

public sealed class SubsonicException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

// Hand-rolled OpenSubsonic/Subsonic REST client (see SYNC-PLAN.md, "The unifying
// decision": one client, three interchangeable servers - a third-party Navidrome/
// Jellyfin-compat instance, a first-party Flower.Server, or another Flower app
// hosting the protocol embedded in-process). Uses the ID3-tag-based browsing
// endpoints (getArtists/getArtist/getAlbumList2/getAlbum) rather than the older
// folder-based getIndexes - both exist in the spec, but ID3 browsing is what
// modern servers (Navidrome) are actually organized around and is all Flower's own
// Track/Playlist model needs.
public class OpenSubsonicClient
{
    private const string ApiVersion = "1.16.1";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _clientName;
    private readonly List<(string Key, string Value)> _extraHeaders;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // extraHeaders is for talking to another Flower device's embedded host rather
    // than a real Subsonic server: peer-to-peer auth is the fingerprint trust gate
    // (X-Flower-Fingerprint/X-Flower-Alias - see SyncHttpServer.AuthorizeAsync,
    // LibrarySyncService), not real Subsonic credentials, but this is still the
    // same client either way - see SYNC-PLAN.md's "one client, three
    // interchangeable servers".
    public OpenSubsonicClient(
        string baseUrl, string username, string password,
        HttpClient? httpClient = null, string clientName = "Flower",
        IEnumerable<(string Key, string Value)>? extraHeaders = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _clientName = clientName;
        _http = httpClient ?? new HttpClient();
        _extraHeaders = extraHeaders?.ToList() ?? [];
    }

    // MD5 here is mandated by the Subsonic auth scheme itself (token = md5(password
    // + salt)), not a security choice of ours - see SYNC-PLAN.md's auth note. Fine
    // over HTTPS, which any real deployment should terminate via a reverse proxy.
    public static string ComputeToken(string password, string salt) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));

    private static string GenerateSalt() => RandomNumberGenerator.GetHexString(16, lowercase: true);

    private List<(string Key, string Value)> AuthParams()
    {
        var salt = GenerateSalt();
        return
        [
            ("u", _username),
            ("t", ComputeToken(_password, salt)),
            ("s", salt),
            ("v", ApiVersion),
            ("c", _clientName),
            ("f", "json"),
        ];
    }

    // Only forces a fresh connection per request (rather than pooling/reusing
    // one) when talking to a peer Flower device (extraHeaders non-empty, i.e.
    // the trust-gate identity headers are set) - a real third-party Subsonic
    // server browsing session can have many requests where connection reuse
    // is actually worth keeping. Peer-to-peer sync sessions are only ever a
    // couple of requests each, so the extra handshake is negligible, and it
    // avoids reusing a keep-alive connection the peer's HttpListener (or the
    // OS, e.g. after iOS backgrounds the app) already tore down - observed in
    // practice as "Connection reset by peer" on iOS.
    private void SetConnectionCloseForPeerRequests(HttpRequestMessage request)
    {
        if (_extraHeaders.Count > 0)
            request.Headers.ConnectionClose = true;
    }

    public string BuildUrl(string endpoint, IEnumerable<(string Key, string Value)>? extraParams = null)
    {
        var parameters = AuthParams();
        // Also embedded as query params, not just sent as headers (see
        // SendAsync/DownloadTrackAsync, which add _extraHeaders to the
        // request directly) - a URL handed to something else to fetch (LibVLC
        // playing GetStreamUrl directly, see VlcAudioManager.Play's "://"
        // check) can't carry custom headers the way an authenticated
        // HttpClient call can, and SyncHttpServer.AuthorizeAsync/IsCallerServer
        // accept either. Harmless against a real third-party OpenSubsonic
        // server, which just ignores the extra unknown params.
        parameters.AddRange(_extraHeaders);
        if (extraParams != null)
            parameters.AddRange(extraParams);

        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{_baseUrl}/rest/{endpoint}?{query}";
    }

    private async Task<SubsonicResponse> SendAsync(string endpoint, IEnumerable<(string Key, string Value)>? extraParams = null)
    {
        var url = BuildUrl(endpoint, extraParams);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in _extraHeaders)
            request.Headers.Add(header.Key, header.Value);
        SetConnectionCloseForPeerRequests(request);

        using var httpResponse = await _http.SendAsync(request);
        httpResponse.EnsureSuccessStatusCode(); // e.g. a 403 from a peer's trust gate - surfaces as a plain HttpRequestException.
        var json = await httpResponse.Content.ReadAsStringAsync();

        var response = JsonSerializer.Deserialize<SubsonicEnvelope>(json, JsonOptions)?.Response
            ?? throw new SubsonicException(0, "Empty or malformed subsonic-response envelope.");

        if (response.Status == "failed")
            throw new SubsonicException(response.Error?.Code ?? 0, response.Error?.Message ?? "Unknown Subsonic error.");

        return response;
    }

    public async Task PingAsync() => await SendAsync("ping");

    public async Task<List<IndexID3>> GetArtistsAsync()
    {
        var response = await SendAsync("getArtists");
        return response.Artists?.Index ?? [];
    }

    public async Task<ArtistWithAlbumsID3> GetArtistAsync(string id)
    {
        var response = await SendAsync("getArtist", [("id", id)]);
        return response.Artist ?? throw new SubsonicException(0, "getArtist returned no artist.");
    }

    public async Task<AlbumWithSongsID3> GetAlbumAsync(string id)
    {
        var response = await SendAsync("getAlbum", [("id", id)]);
        return response.Album ?? throw new SubsonicException(0, "getAlbum returned no album.");
    }

    public async Task<List<AlbumID3>> GetAlbumList2Async(string type = "alphabeticalByName", int size = 500, int offset = 0)
    {
        var response = await SendAsync("getAlbumList2",
        [
            ("type", type),
            ("size", size.ToString()),
            ("offset", offset.ToString()),
        ]);
        return response.AlbumList2?.Album ?? [];
    }

    public async Task<Child> GetSongAsync(string id)
    {
        var response = await SendAsync("getSong", [("id", id)]);
        return response.Song ?? throw new SubsonicException(0, "getSong returned no song.");
    }

    public async Task<SearchResult3> Search3Async(string query, int artistCount = 20, int albumCount = 20, int songCount = 20)
    {
        var response = await SendAsync("search3",
        [
            ("query", query),
            ("artistCount", artistCount.ToString()),
            ("albumCount", albumCount.ToString()),
            ("songCount", songCount.ToString()),
        ]);
        return response.SearchResult3 ?? new SearchResult3(null, null, null);
    }

    public async Task<List<PlaylistDto>> GetPlaylistsAsync()
    {
        var response = await SendAsync("getPlaylists");
        return response.Playlists?.Playlist ?? [];
    }

    public async Task<PlaylistWithSongsDto> GetPlaylistAsync(string id)
    {
        var response = await SendAsync("getPlaylist", [("id", id)]);
        return response.Playlist ?? throw new SubsonicException(0, "getPlaylist returned no playlist.");
    }

    public async Task<PlaylistWithSongsDto?> CreatePlaylistAsync(string name, IEnumerable<string>? songIds = null)
    {
        var parameters = new List<(string, string)> { ("name", name) };
        if (songIds != null)
            parameters.AddRange(songIds.Select(id => ("songId", id)));

        var response = await SendAsync("createPlaylist", parameters);
        return response.Playlist;
    }

    public async Task UpdatePlaylistAsync(
        string playlistId,
        string? name = null,
        string? comment = null,
        bool? isPublic = null,
        IEnumerable<string>? songIdsToAdd = null,
        IEnumerable<int>? songIndexesToRemove = null)
    {
        var parameters = new List<(string, string)> { ("playlistId", playlistId) };
        if (name != null)
            parameters.Add(("name", name));
        if (comment != null)
            parameters.Add(("comment", comment));
        if (isPublic.HasValue)
            parameters.Add(("public", isPublic.Value ? "true" : "false"));
        if (songIdsToAdd != null)
            parameters.AddRange(songIdsToAdd.Select(id => ("songIdToAdd", id)));
        if (songIndexesToRemove != null)
            parameters.AddRange(songIndexesToRemove.Select(i => ("songIndexToRemove", i.ToString())));

        await SendAsync("updatePlaylist", parameters);
    }

    public async Task DeletePlaylistAsync(string id) => await SendAsync("deletePlaylist", [("id", id)]);

    public async Task StarAsync(string? id = null, string? albumId = null, string? artistId = null) =>
        await SendAsync("star", StarParams(id, albumId, artistId));

    public async Task UnstarAsync(string? id = null, string? albumId = null, string? artistId = null) =>
        await SendAsync("unstar", StarParams(id, albumId, artistId));

    private static List<(string, string)> StarParams(string? id, string? albumId, string? artistId)
    {
        var parameters = new List<(string, string)>();
        if (id != null)
            parameters.Add(("id", id));
        if (albumId != null)
            parameters.Add(("albumId", albumId));
        if (artistId != null)
            parameters.Add(("artistId", artistId));

        return parameters;
    }

    public async Task ScrobbleAsync(string id, DateTimeOffset? time = null, bool submission = true)
    {
        var parameters = new List<(string, string)>
        {
            ("id", id),
            ("submission", submission ? "true" : "false"),
        };
        if (time.HasValue)
            parameters.Add(("time", time.Value.ToUnixTimeMilliseconds().ToString()));

        await SendAsync("scrobble", parameters);
    }

    // Binary endpoints - callers stream/fetch bytes themselves (LibVLC can also
    // play a URL directly), so these just build fully-authed URLs rather than
    // buffering audio into memory here. See SYNC-PLAN.md Phase 3's download flow.
    public string GetStreamUrl(string id) => BuildUrl("stream", [("id", id)]);

    public string GetDownloadUrl(string id) => BuildUrl("download", [("id", id)]);

    public string GetCoverArtUrl(string id, int? size = null)
    {
        var parameters = new List<(string, string)> { ("id", id) };
        if (size.HasValue)
            parameters.Add(("size", size.Value.ToString()));

        return BuildUrl("getCoverArt", parameters);
    }

    // Streams stream?id=... straight to a file rather than buffering the whole
    // track in memory - see LibraryDownloadService (SYNC-PLAN.md Phase 3's
    // download button). Uses the same identity headers as every other request
    // (see the constructor's extraHeaders), so this also goes through a peer's
    // trust gate like any other /rest/* call.
    public async Task DownloadTrackAsync(string id, string destinationPath)
    {
        var url = BuildUrl("stream", [("id", id)]);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in _extraHeaders)
            request.Headers.Add(header.Key, header.Value);
        SetConnectionCloseForPeerRequests(request);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(destinationPath);
        await response.Content.CopyToAsync(fileStream);
    }
}
