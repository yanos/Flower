using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly IPeerCredentials? _credentials;

    // credentials is for talking to a Flower.Server rather than a real Subsonic
    // server: auth there is the signed trust gate
    // (X-Flower-Fingerprint/-Alias/-PublicKey/-Signature/-Timestamp/-Nonce - see
    // SignedDeviceCredentials), not real Subsonic credentials, but this is still
    // the same client either way - see SYNC-PLAN.md's "one OpenSubsonic client,
    // one kind of server". It is an
    // object consulted per request rather than a fixed header list because a
    // signature/nonce must be unique per call (see DeviceSigningKey.Sign) -
    // this client instance is long-lived and calls it repeatedly (once per
    // browse call, once per stream/download), so the identity params can
    // never be computed just once at construction time.
    public OpenSubsonicClient(
        string baseUrl, string username, string password,
        HttpClient? httpClient = null, string clientName = "Flower",
        IPeerCredentials? credentials = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _clientName = clientName;
        _http = httpClient ?? PeerHttpClient.Create();
        _credentials = credentials;
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

    // Builds a URL with every peer-identity/signature credential embedded in
    // the query string - necessary for a URL handed directly to something
    // else to fetch (LibVLC playing GetStreamUrl directly, see
    // TrackDecoder.EnsureMedia's "://" check, or GetDownloadUrl/GetCoverArtUrl
    // returned for the caller's own use), which can't carry the custom
    // headers an authenticated HttpClient call can - see
    // SignedRequest.Identity, which accepts either. Not used by
    // SendAsync/DownloadTrackAsync below, which send the identical
    // information as headers instead (see BuildPlainUrl) - harmless against a
    // real third-party OpenSubsonic server either way, which just ignores
    // the extra unknown params.
    public async Task<string> BuildUrlAsync(string endpoint, IEnumerable<(string Key, string Value)>? extraParams = null)
    {
        var path = $"/rest/{endpoint}";
        var parameters = AuthParams();
        if (extraParams != null)
            parameters.AddRange(extraParams);
        if (_credentials != null)
            parameters.AddRange(await _credentials.AuthorizeAsync("GET", path, parameters, []));

        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{_baseUrl}{path}?{query}";
    }

    // Counterpart to BuildUrl for SendAsync/DownloadTrackAsync: builds the URL
    // without baking peer-identity/signature into the query (those travel as
    // headers instead, computed fresh - see the callers below) - avoids
    // generating and discarding an unused, individually-still-valid signed
    // query string alongside every header-authenticated call.
    private string BuildPlainUrl(string endpoint, IEnumerable<(string Key, string Value)>? extraParams, out string path, out List<(string Key, string Value)> parameters)
    {
        path = $"/rest/{endpoint}";
        parameters = AuthParams();
        if (extraParams != null)
            parameters.AddRange(extraParams);
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{_baseUrl}{path}?{query}";
    }

    // Only forces a fresh connection per request (rather than pooling/reusing
    // one) when talking to a peer Flower device - a real third-party Subsonic
    // server browsing session can have many requests where connection reuse
    // is actually worth keeping. Peer-to-peer sync sessions are only ever a
    // couple of requests each, so the extra handshake is negligible, and it
    // avoids reusing a keep-alive connection the peer's HttpListener (or the
    // OS, e.g. after iOS backgrounds the app) already tore down - observed in
    // practice as "Connection reset by peer" on iOS.
    private async Task AddPeerIdentityHeadersAsync(HttpRequestMessage request, string method, string path, IEnumerable<(string Key, string Value)> parameters)
    {
        if (_credentials == null)
            return;

        foreach (var header in await _credentials.AuthorizeAsync(method, path, parameters, []))
            request.Headers.Add(header.Key, header.Value);
        request.Headers.ConnectionClose = true;
    }

    private async Task<SubsonicResponse> SendAsync(string endpoint, IEnumerable<(string Key, string Value)>? extraParams = null)
    {
        var url = BuildPlainUrl(endpoint, extraParams, out var path, out var parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddPeerIdentityHeadersAsync(request, "GET", path, parameters);

        using var httpResponse = await _http.SendAsync(request);
        httpResponse.EnsureSuccessStatusCode(); // e.g. a 403 from a peer's trust gate - surfaces as a plain HttpRequestException.
        var json = await httpResponse.Content.ReadAsStringAsync();

        var response = JsonSerializer.Deserialize(json, OpenSubsonicJsonContext.Default.SubsonicEnvelope)?.Response
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
    //
    // A task, like everything else that signs: the browser's key answers through
    // crypto.subtle and cannot be asked on the calling stack (see
    // IPeerCredentials). Every other head completes these synchronously.
    public Task<string> GetStreamUrlAsync(string id) => BuildUrlAsync("stream", [("id", id)]);

    public Task<string> GetDownloadUrlAsync(string id) => BuildUrlAsync("download", [("id", id)]);

    public Task<string> GetCoverArtUrlAsync(string id, int? size = null)
    {
        var parameters = new List<(string, string)> { ("id", id) };
        if (size.HasValue)
            parameters.Add(("size", size.Value.ToString()));

        return BuildUrlAsync("getCoverArt", parameters);
    }

    // Streams stream?id=... straight to a file rather than buffering the whole
    // track in memory - see LibraryDownloadService (SYNC-PLAN.md Phase 3's
    // download button). Uses the same identity headers as every other request
    // (see the constructor's peerIdentityParams), so this also goes through a
    // peer's trust gate like any other /rest/* call.
    public async Task DownloadTrackAsync(string id, string destinationPath)
    {
        var partPath = destinationPath + PartialSuffix;

        // A previous attempt that died mid-transfer left its bytes here, so
        // ask for the rest instead of starting over - on a phone on flaky
        // wifi, a large FLAC otherwise restarts at byte 0 every time and may
        // never finish. Resuming only works because the caller's destination
        // path is deterministic per track (see LibraryDownloadService); a
        // random name per attempt would strand each partial instead.
        var alreadyHave = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (!await TryFetchAsync(id, partPath, alreadyHave))
        {
            // The server refused the range as unsatisfiable, which means our
            // partial is longer than the track now is - it is worthless.
            File.Delete(partPath);
            await TryFetchAsync(id, partPath, 0);
        }

        // Only now does the file take the name the library will record, so a
        // half-downloaded track is never mistaken for a playable one.
        File.Move(partPath, destinationPath, overwrite: true);
    }

    // Writes the track to partPath, appending to what is already there when
    // asked to resume from a non-zero offset. Returns false only when the
    // server rejected the range outright and the caller must retry from zero;
    // a failed *transfer* throws instead, deliberately leaving the partial in
    // place for the next attempt to resume from.
    private async Task<bool> TryFetchAsync(string id, string partPath, long from)
    {
        var url = BuildPlainUrl("stream", [("id", id)], out var path, out var parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddPeerIdentityHeadersAsync(request, "GET", path, parameters);
        if (from > 0)
            request.Headers.Range = new RangeHeaderValue(from, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (from > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return false;

        response.EnsureSuccessStatusCode(); // e.g. a 403 from a peer's trust gate - surfaces as a plain HttpRequestException.

        // A server is free to ignore Range and answer 200 with the whole body
        // (a peer on an older build does exactly that), and appending that to
        // a partial would corrupt it - so a 200 always overwrites, and only a
        // 206 appends. The full body is already on its way either way, so this
        // is handled by writing it rather than by asking again.
        var append = from > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        await using var fileStream = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write);
        await response.Content.CopyToAsync(fileStream);
        return true;
    }

    // Kept next to the only two places that care (here and the resume check
    // above) rather than spelled inline, since the two must agree exactly.
    public const string PartialSuffix = ".part";
}
