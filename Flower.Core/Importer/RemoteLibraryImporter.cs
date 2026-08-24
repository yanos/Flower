using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
public readonly record struct RemoteLibraryFetch(bool NotModified, string? ETag, List<Track> Tracks);

// The other IMusicImporter: a catalog pulled from a Flower host over the bulk
// manifest endpoint, where Importer walks folders on disk. Every track it
// returns is a placeholder (Path == null) - this device does not have the file,
// only the knowledge that the origin does, and how to ask for it later.
//
// Deliberately not Subsonic-shaped. Browsing a Flower host over
// getAlbumList2/getAlbum means one request per album, which at a real library
// size is hundreds or thousands of connections in a burst - see
// LibrarySyncContracts for the full history. GET /api/flower/v1/library is the
// whole catalog in one request with an ETag, so that is what this reads. A
// genuine SubsonicLibraryImporter is still worth having for third-party servers
// (Navidrome, Jellyfin) that have no such endpoint, but never for talking to
// Flower.Server, which does.
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
    // closeConnection asks for a fresh connection per request. True when
    // talking to a peer's embedded SyncHttpServer, whose HttpListener (or the
    // OS) may have torn a pooled keep-alive connection down without telling us
    // - see PlaylistSyncService. False in the browser, where the fetch stack
    // owns connection reuse and the header is not ours to set.
    public RemoteLibraryImporter(
        HttpClient http,
        string baseUrl,
        IPeerCredentials credentials,
        string originFingerprint,
        string ownFingerprint,
        ILogger<RemoteLibraryImporter> logger,
        bool closeConnection = false)
    {
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

    // Throws rather than swallowing: a 403 off this route means the peer has
    // revoked us and a 401 means one request's signature was rejected, and only
    // the caller knows what to do about either (see LibrarySyncService's own
    // PeerTrustRejected handling, and "403 means revoked; a bad signature is
    // 401" in SYNC-PLAN.md).
    public async Task<RemoteLibraryFetch> FetchAsync(string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{LibraryPath}");
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        await request.AddPeerCredentialsAsync(_credentials);
        if (_closeConnection)
            request.Headers.ConnectionClose = true;

        using var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Remote library at {BaseUrl}: catalog unchanged since {Token}", _baseUrl, ifNoneMatch);
            return new RemoteLibraryFetch(NotModified: true, ifNoneMatch, []);
        }

        response.EnsureSuccessStatusCode();

        // Read off Headers.ETag when the value parses as one and off the raw
        // header when it does not - a weak or oddly-quoted tag we have to send
        // back verbatim is worth more than a strictly-parsed null.
        var servedToken = response.Headers.ETag?.Tag
            ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null);

        var json = await response.Content.ReadAsStringAsync();
        var manifest = JsonSerializer.Deserialize(json, LibrarySyncJsonContext.Default.LibrarySyncManifestDto);
        var songs = manifest?.Songs ?? [];

        var tracks = songs
            .Select(song => LibrarySyncMapper.ToPlaceholderTrack(song, _originFingerprint, _ownFingerprint))
            .ToList();

        _logger.LogInformation("Remote library at {BaseUrl}: fetched {SongCount} song(s)", _baseUrl, tracks.Count);
        return new RemoteLibraryFetch(NotModified: false, servedToken, tracks);
    }
}
