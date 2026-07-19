using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Raised the first time an unrecognized peer fingerprint calls a gated
// endpoint - see SyncHttpServer.AuthorizeAsync. The UI is expected to show an
// AirDrop-style "Allow this device?" prompt and report back via Resolution;
// the HTTP request stays pending (all concurrent requests from the same
// not-yet-decided fingerprint share this one prompt/Resolution) until it does,
// or until the approval timeout denies by default.
public sealed class PeerApprovalRequestedEventArgs : EventArgs
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required TaskCompletionSource<bool> Resolution { get; init; }
}

// Plain-HTTP sync endpoints (see SYNC-PLAN.md):
//   GET  /api/localsend/v2/info        - device identity (phase 1)
//   GET  /api/flower/v1/playlists      - this device's current playlist manifest
//   POST /api/flower/v1/playlists/apply - adopt a peer-merged manifest wholesale
// (phase 2, playlist metadata sync), plus an embedded OpenSubsonic host (phase 3,
// see LibraryOpenSubsonicMapper/LibrarySyncService/LibraryDownloadService):
//   GET  /api/flower/v1/library - every real track in one response (bespoke,
//                                 not OpenSubsonic - see LibrarySyncContracts;
//                                 this is what LibrarySyncService actually uses)
//   GET  /rest/getAlbumList2    - this device's own real tracks, grouped by album
//   GET  /rest/getAlbum         - one album's song list (OpenSubsonic-shaped
//                                 browsing, kept for real third-party interop -
//                                 not used by Flower's own sync, which would mean
//                                 one request per album)
//   GET  /rest/stream           - one song's actual file bytes, by SyncKey
//   GET  /rest/getCoverArt      - one album's art bytes, by album id (see
//                                 LibraryOpenSubsonicMapper.AlbumId) - used by
//                                 AlbumArtLoader's remote-fetch path for a
//                                 placeholder track's art (SYNC-PLAN.md Phase 3)
// Deliberately plain HTTP for now, not HTTPS - see the plan doc for why.
//
// Trust gate (phase 3): every /api/flower/v1/* endpoint requires the caller to
// identify itself via X-Flower-Fingerprint/X-Flower-Alias headers (see
// PlaylistSyncService, which sends both). An unrecognized fingerprint raises
// PeerApprovalRequested and blocks that request until the user approves/denies
// (or the request times out and is denied by default) - see AuthorizeAsync.
// /api/localsend/v2/info stays ungated: a peer has to learn our fingerprint (and
// we, its) via that endpoint before either side can evaluate trust at all.
public class SyncHttpServer : IDisposable
{
    public const int DefaultPort = 53317;
    private const int MaxPortAttempts = 10;
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    private HttpListener? _listener;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AppSettings _appSettings;
    private readonly Library _library;
    private readonly ILogger _logger;
    private readonly PlaylistStore _playlistStore;
    private readonly TrustedPeerStore _trustedPeerStore;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private CancellationTokenSource? _cts;

    public event EventHandler<PeerApprovalRequestedEventArgs>? PeerApprovalRequested;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The port actually bound once Start() succeeds - may differ from DefaultPort,
    // see Start(). Null if binding failed on every port tried (sync unavailable).
    public int? BoundPort { get; private set; }

    public SyncHttpServer(
        DeviceIdentity deviceIdentity,
        AppSettings appSettings,
        Library library,
        PlaylistStore playlistStore,
        TrustedPeerStore trustedPeerStore,
        ILogger<SyncHttpServer> logger)
    {
        _deviceIdentity = deviceIdentity;
        _appSettings = appSettings;
        _library = library;
        _playlistStore = playlistStore;
        _trustedPeerStore = trustedPeerStore;
        _logger = logger;
    }

    // Tries DefaultPort first, then a handful of ports after it. Covers testing
    // desktop + the iOS Simulator side by side on the same Mac, where the Simulator
    // shares the host's actual TCP port namespace (same root cause as it sharing the
    // hostname - see NetworkDiscoveryService) and a fixed port would always collide.
    // On real separate devices the first attempt always succeeds. The actual bound
    // port is advertised via mDNS (see NetworkDiscoveryService.Start), so peers never
    // need to assume DefaultPort - they read it from the discovery answer instead.
    //
    // Wildcard binding ("+") needs no admin rights on macOS/Linux/mobile, but on
    // Windows it requires a one-time "netsh http add urlacl" reservation (or running
    // elevated) - a known gap, not yet handled here.
    public void Start()
    {
        _cts = new CancellationTokenSource();

        for (var port = DefaultPort; port < DefaultPort + MaxPortAttempts; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _listener = listener;
            BoundPort = port;
            _logger.LogInformation("Sync HTTP server listening on port {Port}", port);
            _ = ListenLoopAsync(_cts.Token);
            return;
        }

        _logger.LogError("Could not bind any port in {StartPort}..{EndPort}, sync endpoint disabled",
            DefaultPort, DefaultPort + MaxPortAttempts - 1);
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Listener stopped/disposed.
            }

            _ = HandleRequestAsync(context);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            // Matches the client's own ConnectionClose (see PlaylistSyncService/
            // LibrarySyncService/OpenSubsonicClient): both ends agree every sync
            // connection is one-shot, rather than a client later trying to reuse
            // a pooled connection this listener (or the OS) has already torn
            // down - observed in practice as "Connection reset by peer" on iOS.
            context.Response.KeepAlive = false;

            var path = context.Request.Url?.AbsolutePath;
            var method = context.Request.HttpMethod;

            if (path != null && RequiresTrust(path))
            {
                if (!await AuthorizeAsync(context))
                {
                    context.Response.StatusCode = 403;
                    _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: not authorized",
                        method, path, context.Request.RemoteEndPoint);
                    return;
                }

                // Bulk-sync endpoints only (not browse/stream - see
                // SyncRolePolicy.ShouldRejectPeerAsServer's own doc comment) -
                // a correctly-behaving Server never initiates bulk sync at
                // all, so this is defense-in-depth against a caller that also
                // claims to be a Server somehow reaching this endpoint.
                if (IsBulkSyncPath(path) && SyncRolePolicy.ShouldRejectPeerAsServer(_appSettings.IsServer, IsCallerServer(context)))
                {
                    context.Response.StatusCode = 403;
                    _logger.LogWarning("Rejected bulk sync {Method} {Path} from {RemoteEndPoint}: caller also advertises Server role",
                        method, path, context.Request.RemoteEndPoint);
                    return;
                }
            }

            if (path == "/api/localsend/v2/info" && method == "GET")
                await HandleInfoAsync(context);
            else if (path == "/api/flower/v1/playlists" && method == "GET")
                await HandleGetPlaylistsAsync(context);
            else if (path == "/api/flower/v1/playlists/apply" && method == "POST")
                await HandleApplyPlaylistsAsync(context);
            else if (path == "/api/flower/v1/library" && method == "GET")
                await HandleGetLibraryAsync(context);
            else if (path == "/rest/getAlbumList2" && method == "GET")
                await HandleGetAlbumList2Async(context);
            else if (path == "/rest/getAlbum" && method == "GET")
                await HandleGetAlbumAsync(context);
            else if (path == "/rest/stream" && method == "GET")
                await HandleStreamAsync(context);
            else if (path == "/rest/getCoverArt" && method == "GET")
                await HandleGetCoverArtAsync(context);
            else
                context.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            // Client disconnected mid-response or similar - nothing to recover.
            _logger.LogDebug(ex, "Request handling failed (client likely disconnected)");
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static bool RequiresTrust(string path) =>
        path.StartsWith("/api/flower/v1/", StringComparison.Ordinal) ||
        path.StartsWith("/rest/", StringComparison.Ordinal);

    // The bulk-merge endpoints (playlists/library manifest) - distinct from
    // /rest/* browse/stream, which stays open to any trusted peer regardless
    // of role. See SyncRolePolicy.ShouldRejectPeerAsServer.
    private static bool IsBulkSyncPath(string path) =>
        path.StartsWith("/api/flower/v1/", StringComparison.Ordinal);

    private static bool IsCallerServer(HttpListenerContext context) =>
        string.Equals(context.Request.Headers["X-Flower-Role"], "server", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> AuthorizeAsync(HttpListenerContext context)
    {
        var fingerprint = context.Request.Headers["X-Flower-Fingerprint"];
        if (string.IsNullOrEmpty(fingerprint))
        {
            _logger.LogWarning("Denying request with no X-Flower-Fingerprint header");
            return false; // Can't evaluate trust without a claimed identity - deny outright.
        }

        if (_trustedPeerStore.IsTrusted(fingerprint))
            return true;

        var alias = context.Request.Headers["X-Flower-Alias"];
        if (string.IsNullOrEmpty(alias))
            alias = fingerprint;

        _logger.LogInformation("Untrusted peer {Alias} ({Fingerprint}) requesting approval", alias, fingerprint);
        var approved = await RequestApprovalAsync(fingerprint, alias);
        _logger.LogInformation("Peer {Alias} ({Fingerprint}) approval result: {Approved}", alias, fingerprint, approved);
        if (approved)
            await _trustedPeerStore.ApproveAsync(fingerprint, alias);

        return approved;
    }

    // Concurrent requests from the same not-yet-decided fingerprint (e.g. a
    // playlist GET immediately followed by its POST /apply in one sync session)
    // share a single prompt/TaskCompletionSource rather than surfacing the
    // approve/deny dialog twice. Only the caller that actually creates the
    // pending entry raises the event and starts the timeout; every other
    // concurrent caller just awaits the same task.
    private async Task<bool> RequestApprovalAsync(string fingerprint, string alias)
    {
        var newTcs = new TaskCompletionSource<bool>();
        var tcs = _pendingApprovals.GetOrAdd(fingerprint, newTcs);

        if (ReferenceEquals(tcs, newTcs))
        {
            var handler = PeerApprovalRequested;
            if (handler == null)
                tcs.TrySetResult(false); // No UI listening - fail closed rather than trusting a stranger.
            else
                handler.Invoke(this, new PeerApprovalRequestedEventArgs { Fingerprint = fingerprint, Alias = alias, Resolution = tcs });

            _ = Task.Delay(ApprovalTimeout).ContinueWith(_ =>
            {
                tcs.TrySetResult(false); // Ignored/unanswered prompt - deny by default.
                _pendingApprovals.TryRemove(fingerprint, out TaskCompletionSource<bool>? _);
            });
        }

        return await tcs.Task;
    }

    private async Task HandleInfoAsync(HttpListenerContext context)
    {
        var deviceType = OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() ? "mobile" : "desktop";
        var body = JsonSerializer.Serialize(new
        {
            alias = _deviceIdentity.Alias,
            version = "2.0",
            deviceModel = (string?)null,
            deviceType,
            fingerprint = _deviceIdentity.Fingerprint,
            isServer = _appSettings.IsServer,
            download = false
        });
        await WriteJsonAsync(context, body);
    }

    private async Task HandleGetPlaylistsAsync(HttpListenerContext context)
    {
        var manifest = PlaylistSyncMapper.ToManifest(_deviceIdentity.Fingerprint, _library.Playlists);
        await WriteJsonAsync(context, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    // Bulk, non-OpenSubsonic endpoint for LibrarySyncService - see
    // LibrarySyncContracts for why this exists alongside (not instead of)
    // /rest/getAlbumList2+getAlbum.
    private async Task HandleGetLibraryAsync(HttpListenerContext context)
    {
        var manifest = new LibrarySyncManifestDto(_deviceIdentity.Fingerprint, LibraryOpenSubsonicMapper.BuildAllSongs(_library.Tracks, _deviceIdentity.Fingerprint));
        await WriteJsonAsync(context, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    // The initiator of a sync session (see PlaylistSyncService) has already resolved
    // every conflict by the time it POSTs here, so this side just replaces its whole
    // playlist collection to match - no merge logic runs on this end.
    private async Task HandleApplyPlaylistsAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        var manifest = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(json, JsonOptions);
        if (manifest == null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var playlists = manifest.Playlists
            .Select(dto => PlaylistSyncMapper.ToPlaylist(dto, _library.Tracks))
            .ToList();
        _library.ReplacePlaylists(playlists);
        await _playlistStore.SaveAsync(playlists);

        _logger.LogInformation("Received and applied {Count} playlist(s) pushed from a peer", playlists.Count);

        context.Response.StatusCode = 204;
    }

    // Embedded OpenSubsonic host (SYNC-PLAN.md Phase 3's "one client, three
    // interchangeable servers"): serves this device's own real (Path != null)
    // tracks for LibrarySyncService to pull from a peer. Only getAlbumList2/
    // getAlbum are implemented - nothing else calls getArtists/getSong/stream yet
    // (the mobile download-button UI, which needs stream, is a later piece).
    private async Task HandleGetAlbumList2Async(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var size = int.TryParse(query["size"], out var s) ? s : 500;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(_library.Tracks)
            .Skip(offset)
            .Take(size)
            .ToList();

        var body = JsonSerializer.Serialize(new SubsonicEnvelope
        {
            Response = new SubsonicResponse { Status = "ok", Version = "1.16.1", AlbumList2 = new AlbumList2(albums) },
        }, JsonOptions);
        await WriteJsonAsync(context, body);
    }

    private async Task HandleGetAlbumAsync(HttpListenerContext context)
    {
        var id = context.Request.QueryString["id"];
        var album = id != null ? LibraryOpenSubsonicMapper.FindAlbum(_library.Tracks, id, _deviceIdentity.Fingerprint) : null;
        if (album == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var body = JsonSerializer.Serialize(new SubsonicEnvelope
        {
            Response = new SubsonicResponse { Status = "ok", Version = "1.16.1", Album = album },
        }, JsonOptions);
        await WriteJsonAsync(context, body);
    }

    // Serves this device's own real file bytes for one song, looked up by SyncKey
    // (the same id LibraryOpenSubsonicMapper.ToChild hands out - see
    // LibraryDownloadService, SYNC-PLAN.md Phase 3's download button). Never
    // serves a placeholder - only a track this device actually has a file for.
    private async Task HandleStreamAsync(HttpListenerContext context)
    {
        var id = context.Request.QueryString["id"];
        var track = id != null
            ? _library.Tracks.FirstOrDefault(t => t.Path != null && t.SyncKey == id)
            : null;

        if (track?.Path == null || !File.Exists(track.Path))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        using var fileStream = File.OpenRead(track.Path);
        context.Response.ContentLength64 = fileStream.Length;
        await fileStream.CopyToAsync(context.Response.OutputStream);
    }

    // Serves this device's own real album art bytes, looked up by album id (the
    // same id LibraryOpenSubsonicMapper.AlbumId/ToChild's CoverArt is derived
    // from) - see AlbumArtLoader's remote-fetch path, SYNC-PLAN.md Phase 3's
    // synced art. Never serves art for an album with no local (Path != null)
    // track at all, same "only what this device actually has" rule as /rest/stream.
    private async Task HandleGetCoverArtAsync(HttpListenerContext context)
    {
        var id = context.Request.QueryString["id"];
        var track = id != null
            ? _library.Tracks.FirstOrDefault(t => t.Path != null && LibraryOpenSubsonicMapper.AlbumId(t.Album, t.Artists) == id)
            : null;

        var bytes = track != null ? AlbumArtLoader.TryGetLocalArtBytes(track) : null;
        if (bytes == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = SniffImageContentType(bytes);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static string SniffImageContentType(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            ? "image/png"
            : "image/jpeg"; // Overwhelmingly the common case for embedded/cover-file art either way.

    private static async Task WriteJsonAsync(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener?.IsListening == true)
            _listener.Stop();
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }
}
