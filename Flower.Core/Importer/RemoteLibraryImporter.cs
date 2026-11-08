using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.Importer;

// What one conditional pull of a peer's catalog produced. NotModified means the
// peer answered 304 to our If-None-Match: its catalog is byte-for-byte the one
// we already hold, so Tracks is empty because nothing was fetched - not because
// the peer has nothing. A caller that cannot tell those apart will happily prune
// a library it never re-read (see Library.MergeSyncedTracks), so the two are
// kept distinct here rather than collapsed into an empty list.
//
// ListeningToken is where the catalog's listening stands, for an owner's
// device to follow GET /listening from (LibrarySyncManifestDto.ListeningToken).
public readonly record struct RemoteLibraryFetch(
    bool NotModified, string? ETag, List<Track> Tracks, IReadOnlySet<string> RemovedIds, string? ListeningToken = null);

// What one GET /listening answer produced: the songs whose listening moved, as
// LibrarySyncMapper.ToListeningTrack makes them, and the token to ask from
// next time.
public readonly record struct RemoteListeningFetch(string Token, List<Track> Tracks);

// The other IMusicImporter: a catalog pulled from a Flower host over the bulk
// manifest endpoint, where Importer walks folders on disk. Every track it
// returns is a placeholder (Path == null) - this device does not have the file,
// only the knowledge that the origin does, and how to ask for it later.
//
// Deliberately not Subsonic-shaped. Browsing a Flower host over
// getAlbumList2/getAlbum, while it still answered them, meant one request per
// album, which at a real library size is hundreds or thousands of connections
// in a burst - see LibrarySyncContracts for the full history. GET
// /api/flower/v1/library is the whole catalog in one request with an ETag, so
// that is what this reads.
//
// In Flower.Core so both heads share it: the desktop reaches it through
// LibrarySyncService, which pulls from a discovered peer and merges additively;
// the browser registers it as its IMusicImporter outright, because its "library
// on disk" is the server it was served from. That is also why the transport
// details below are constructor arguments rather than assumptions - the two
// heads disagree about all of them.
public sealed class RemoteLibraryImporter : IMusicImporter
{
    // The path is the whole API surface of the endpoint; kept here rather than
    // in SyncProtocol because it is Flower's own bulk-sync route, not part of
    // the LocalSend-derived handshake that class describes.
    public const string LibraryPath = "/api/flower/v1/library";
    public const string ListeningPath = "/api/flower/v1/listening";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IPeerCredentials _credentials;
    private readonly string _originFingerprint;
    private readonly string _ownFingerprint;
    private readonly bool _closeConnection;
    private readonly ILogger _logger;

    // originFingerprint is the *serving* device's, stamped onto every
    // placeholder so a later stream or download request knows who to ask. The
    // browser has no other way to learn it and must read it from /info
    // (SyncProtocol.InfoPath) first - a placeholder without it is unplayable,
    // so that is a prerequisite rather than a detail.
    //
    // ownFingerprint is this device's, and exists only so an incoming
    // RemotePlayCounts entry under our own name - a peer echoing back what it
    // once learned about us - is dropped instead of overwriting the local count
    // that is always authoritative here (see LibrarySyncMapper). The browser
    // passes an empty string, which matches nothing, which is exactly right: it
    // is not a device that has ever played anything for a peer to echo.
    //
    // closeConnection asks for a fresh connection per request. True when talking
    // to a server over the LAN, which (or whose OS) may have torn a pooled
    // keep-alive connection down without telling us - see PlaylistSyncService.
    // False in the browser, where the fetch stack
    // owns connection reuse and the header is not ours to set.
    // The origin's public key as /info served it, so a device-unknown refusal
    // from it can be checked against the fingerprint this device pinned (see
    // PeerResponses.EnsureSuccessAsync). Null for a caller that has none, whose
    // refusals then never read as a revocation.
    private readonly string? _originPublicKey;

    public RemoteLibraryImporter(
        HttpClient http,
        string baseUrl,
        IPeerCredentials credentials,
        string originFingerprint,
        string ownFingerprint,
        ILogger<RemoteLibraryImporter> logger,
        bool closeConnection = false,
        string? originPublicKey = null)
    {
        _originPublicKey = originPublicKey;
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _originFingerprint = originFingerprint;
        _ownFingerprint = ownFingerprint;
        _logger = logger;
        _closeConnection = closeConnection;
    }

    // The IMusicImporter face, for the head that treats the remote catalog as
    // its whole library. libraryPaths is ignored - there are no folders to
    // scan - and so is the ETag, since a caller at this level is asking for the
    // catalog rather than for what changed. Anything conditional goes through
    // FetchAsync.
    public async Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null) =>
        (await FetchAsync()).Tracks;

    // Placeholders for someone else's files, every one of them.
    public bool ScansLocalFiles => false;

    // The catalog is parsed as it streams in, a megabyte at a time, rather than
    // read into one string and parsed in one go. The difference is only felt in
    // a browser tab, where there is one thread and every await on the network
    // hands it back to the page: a 16k-track catalog is 12 MB, and parsing it
    // whole under the WASM interpreter held that thread for 0.9s, with the UI
    // already on screen and ignoring input. A megabyte keeps each slice near
    // 150ms without costing total time - the default 16KB buffer made the same
    // parse take 3.8s, in 750 round trips through the event loop.
    private static readonly JsonTypeInfo<LibrarySyncManifestDto> ManifestInChunks =
        (JsonTypeInfo<LibrarySyncManifestDto>)new JsonSerializerOptions(LibrarySyncJsonContext.Default.Options) { DefaultBufferSize = 1 << 20 }
            .GetTypeInfo(typeof(LibrarySyncManifestDto));

    // Throws rather than swallowing, as a PeerRefusedException carrying the
    // server's reason: device-unknown means the peer may have revoked us,
    // anything else means this one request failed, and only the caller knows
    // what to do about either (see LibrarySyncService's own PeerTrustRejected
    // handling, and FlowerProblem).
    public async Task<RemoteLibraryFetch> FetchAsync(string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{LibraryPath}");
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        await request.AddPeerCredentialsAsync(_credentials);
        if (_closeConnection)
            request.Headers.ConnectionClose = true;

        // Without this a browser's HttpClient buffers the whole body before
        // SendAsync returns, and the parse below never yields to the page -
        // see ManifestInChunks. The option is what .NET's own
        // SetBrowserResponseStreamingEnabled sets, spelled out because that
        // extension lives in an assembly Flower.Core does not reference.
        if (OperatingSystem.IsBrowser())
            request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogTrace("Remote library at {BaseUrl}: catalog unchanged since {Token}", _baseUrl, ifNoneMatch);
            return new RemoteLibraryFetch(NotModified: true, ifNoneMatch, [], new HashSet<string>());
        }

        await response.EnsureSuccessAsync(_originPublicKey, _originFingerprint);

        // Read off Headers.ETag when the value parses as one and off the raw
        // header when it does not - a weak or oddly-quoted tag we have to send
        // back verbatim is worth more than a strictly-parsed null.
        var servedToken = response.Headers.ETag?.Tag
            ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null);

        await using var body = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync(body, ManifestInChunks);
        var songs = manifest?.Songs ?? [];

        var tracks = songs
            .Select(song => LibrarySyncMapper.ToPlaceholderTrack(song, _originFingerprint, _ownFingerprint))
            .ToList();

        _logger.LogInformation("Remote library at {BaseUrl}: fetched {SongCount} song(s)", _baseUrl, tracks.Count);
        return new RemoteLibraryFetch(
            NotModified: false, servedToken, tracks, (manifest?.Removed ?? []).ToHashSet(), manifest?.ListeningToken);
    }

    // The listening that moved on the origin since `since` - see
    // ListeningChangesDto. Throws as FetchAsync does; a 409 among those says
    // `since` is not a token the origin can count from, and the caller reads
    // the whole catalog instead.
    public async Task<RemoteListeningFetch> FetchListeningAsync(string since)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_baseUrl}{ListeningPath}?since={Uri.EscapeDataString(since)}");
        await request.AddPeerCredentialsAsync(_credentials);
        if (_closeConnection)
            request.Headers.ConnectionClose = true;

        using var response = await _http.SendAsync(request);
        await response.EnsureSuccessAsync(_originPublicKey, _originFingerprint);

        await using var body = await response.Content.ReadAsStreamAsync();
        var changes = await JsonSerializer.DeserializeAsync(body, LibrarySyncJsonContext.Default.ListeningChangesDto)
                      ?? throw new JsonException("GET /listening answered with no body");

        var tracks = changes.Songs
            .Select(song => LibrarySyncMapper.ToListeningTrack(song, _originFingerprint, _ownFingerprint))
            .ToList();

        _logger.LogDebug("Remote library at {BaseUrl}: listening moved on {SongCount} song(s) since {Since}",
            _baseUrl, tracks.Count, since);
        return new RemoteListeningFetch(changes.Token, tracks);
    }
}
