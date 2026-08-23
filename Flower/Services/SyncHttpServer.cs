using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

// Raised when an unrecognized peer fingerprint explicitly asks to pair - see
// SyncHttpServer.HandlePairRequestAsync. Never raised by any other endpoint
// (see this file's top-of-file doc comment) - a peer merely calling some
// unrelated gated endpoint is just refused, not treated as a pairing attempt.
// The UI is expected to show an AirDrop-style "Allow this device?" prompt and
// report back via Resolution; the HTTP request stays pending (all concurrent
// pair-requests from the same not-yet-decided fingerprint share this one
// prompt/Resolution) until it does, or until the approval timeout denies by
// default.
public sealed class PeerApprovalRequestedEventArgs : EventArgs
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required TaskCompletionSource<bool> Resolution { get; init; }
}

// Peer-to-peer sync endpoints (see SYNC-PLAN.md):
//   GET  /api/localsend/v2/info        - device identity (phase 1)
//   POST /api/flower/v1/pair-request   - explicit pairing bootstrap, see below
//   POST /api/flower/v1/unpair-notify  - server-initiated revoke notification
//   GET  /api/flower/v1/playlists      - this device's current playlist manifest
//   POST /api/flower/v1/playlists/apply - adopt a peer-merged manifest wholesale
// (phase 2, playlist metadata sync), plus an embedded OpenSubsonic host (phase 3,
// see LibraryOpenSubsonicMapper/LibrarySyncService/LibraryDownloadService):
//   GET  /api/flower/v1/library - every real track in one response (bespoke,
//                                 not OpenSubsonic - see LibrarySyncContracts;
//                                 this is what LibrarySyncService actually uses)
//   POST /api/flower/v1/log/report - a Client pushes its own recent log
//                                 snapshot here as an extra step of the same
//                                 sync session (see LibrarySyncService.
//                                 PushLogSnapshotAsync, LogSyncContracts,
//                                 ClientLogStore) - never pulled by a Server
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
// Deliberately plain HTTP still, not HTTPS - see the plan doc for why (TLS
// remains explicitly deferred). What changed: identity is no longer a bare
// self-reported fingerprint string. Every gated request must additionally
// carry a signature (X-Flower-Signature/-Timestamp/-Nonce, or the same as
// query-string params for a URL handed directly to LibVLC/OpenSubsonicClient.
// BuildUrl - see GetIdentityValue) proving possession of the private key
// behind the fingerprint it claims - see SignedRequestCanonicalizer/
// DeviceSigningKey/SignatureVerifier. This closes the actual impersonation
// hole (anyone who merely observed a trusted peer's fingerprint used to be
// able to reuse it) without needing transport encryption.
//
// Route table (see Route/AuthMode/RateLimitCategory below) governs auth and
// rate-limit behavior declaratively per endpoint rather than ad hoc string-
// prefix checks. AuthMode.Open (info) needs no proof at all - a peer has to
// learn our fingerprint+public key here before either side can evaluate
// trust. AuthMode.SelfSigned (pair-request, unpair-notify) proves the caller
// holds the private key matching the public key it's offering, verified
// against that offered key itself rather than a trust-store lookup, since
// there's nothing to look up yet. AuthMode.TrustedPeer (everything else)
// verifies against the public key captured in TrustedPeerStore at the
// moment a fingerprint was actually approved - never a cached /info value.
//
// Every request is additionally required to originate from a private/
// loopback address (see LanGuard) - the wildcard "http://+:{port}/" bind
// means that check is the only thing standing between "LAN-only" and
// "reachable from the internet if the port is ever forwarded."
public class SyncHttpServer : IDisposable
{
    public const int DefaultPort = SyncProtocol.DefaultPort;
    private const int MaxPortAttempts = 10;
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    // Largest legitimate request body in this system today is the bulk
    // library manifest response (not a request body), roughly 6-8 MB of JSON
    // at a 16k-track library; the two actual request-body endpoints
    // (playlists/apply, log/report) stay well under that. 20 MB gives
    // comfortable headroom while still bounding worst-case memory use to
    // something trivial, including on mobile.
    private const long MaxBodyBytes = 20 * 1024 * 1024;

    private enum AuthMode { Open, SelfSigned, TrustedPeer }
    private enum RateLimitCategory { Info, Pair, Bulk, Browse, Stream }

    private sealed record Route(
        string Method, string Path, AuthMode Auth, RateLimitCategory RateLimit,
        bool HasBody, bool IsBulkSync, Func<HttpListenerContext, byte[], Task> Handler);

    private HttpListener? _listener;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _deviceSigningKey;
    private readonly AppSettings _appSettings;
    private readonly Library _library;
    private readonly ILogger _logger;
    private readonly TrustedPeerStore _trustedPeerStore;
    private readonly ClientLogStore _clientLogStore;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly NonceReplayGuard _nonceReplayGuard = new();
    private CancellationTokenSource? _cts;
    private readonly List<Route> _routes;

    // Pre-trust endpoints (info/pair-request/unpair-notify) have no
    // fingerprint worth keying by yet - an attacker can mint a fresh keypair
    // per request, so source IP is the only thing that actually costs them
    // anything. Everything past the trust gate is keyed by the (self-
    // reported, but by then meaningless to spoof cheaply since it still has
    // to pass signature verification) fingerprint instead.
    private readonly RateLimiter _infoRateLimiter = new(max: 120, TimeSpan.FromSeconds(60));
    private readonly RateLimiter _pairRateLimiter = new(max: 5, TimeSpan.FromSeconds(60));
    private readonly RateLimiter _bulkRateLimiter = new(max: 20, TimeSpan.FromSeconds(60));
    private readonly RateLimiter _browseRateLimiter = new(max: 120, TimeSpan.FromSeconds(60));
    private readonly RateLimiter _streamRateLimiter = new(max: 30, TimeSpan.FromSeconds(60));

    public event EventHandler<PeerApprovalRequestedEventArgs>? PeerApprovalRequested;

    // Reuses PeerTrustRejectedEventArgs (PlaylistSyncService.cs) - same shape
    // (Fingerprint + Alias) fits a received unpair notification exactly, and
    // MainViewModel's existing HandlePeerTrustRejected handler already does
    // the right thing (clear the stale pairing if it matches) with no new
    // logic needed on the receiving side.
    public event EventHandler<PeerTrustRejectedEventArgs>? PeerUnpairNotified;

    // The port actually bound once Start() succeeds - may differ from DefaultPort,
    // see Start(). Null if binding failed on every port tried (sync unavailable).
    public int? BoundPort { get; private set; }

    public SyncHttpServer(
        DeviceIdentity deviceIdentity,
        DeviceSigningKey deviceSigningKey,
        AppSettings appSettings,
        Library library,
        TrustedPeerStore trustedPeerStore,
        ClientLogStore clientLogStore,
        ILogger<SyncHttpServer> logger)
    {
        _deviceIdentity = deviceIdentity;
        _deviceSigningKey = deviceSigningKey;
        _appSettings = appSettings;
        _library = library;
        _trustedPeerStore = trustedPeerStore;
        _clientLogStore = clientLogStore;
        _logger = logger;

        _routes =
        [
            new Route("GET", SyncProtocol.InfoPath, AuthMode.Open, RateLimitCategory.Info, false, false, HandleInfoAsync),
            new Route("POST", "/api/flower/v1/pair-request", AuthMode.SelfSigned, RateLimitCategory.Pair, false, false, HandlePairRequestAsync),
            new Route("POST", "/api/flower/v1/unpair-notify", AuthMode.SelfSigned, RateLimitCategory.Pair, false, false, HandleUnpairNotifyAsync),
            new Route("GET", "/api/flower/v1/playlists", AuthMode.TrustedPeer, RateLimitCategory.Bulk, false, true, HandleGetPlaylistsAsync),
            new Route("POST", "/api/flower/v1/playlists/apply", AuthMode.TrustedPeer, RateLimitCategory.Bulk, true, true, HandleApplyPlaylistsAsync),
            new Route("GET", "/api/flower/v1/library", AuthMode.TrustedPeer, RateLimitCategory.Bulk, false, true, HandleGetLibraryAsync),
            new Route("POST", "/api/flower/v1/log/report", AuthMode.TrustedPeer, RateLimitCategory.Bulk, true, false, HandleReportLogAsync),
            new Route("GET", "/rest/getAlbumList2", AuthMode.TrustedPeer, RateLimitCategory.Browse, false, false, HandleGetAlbumList2Async),
            new Route("GET", "/rest/getAlbum", AuthMode.TrustedPeer, RateLimitCategory.Browse, false, false, HandleGetAlbumAsync),
            new Route("GET", "/rest/getCoverArt", AuthMode.TrustedPeer, RateLimitCategory.Browse, false, false, HandleGetCoverArtAsync),
            new Route("GET", "/rest/stream", AuthMode.TrustedPeer, RateLimitCategory.Stream, false, false, HandleStreamAsync),
        ];
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
            catch (HttpListenerException ex)
            {
                _logger.LogDebug(ex, "Port {Port} unavailable, trying the next one", port);
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

            var remoteAddress = context.Request.RemoteEndPoint?.Address;
            if (remoteAddress == null || !LanGuard.IsPrivateOrLoopback(remoteAddress))
            {
                context.Response.StatusCode = 403;
                _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: not a LAN-local address",
                    context.Request.HttpMethod, context.Request.Url?.AbsolutePath, context.Request.RemoteEndPoint);
                return;
            }

            var path = context.Request.Url?.AbsolutePath;
            var method = context.Request.HttpMethod;
            var route = _routes.FirstOrDefault(r => r.Method == method && r.Path == path);
            if (route == null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (!CheckRateLimit(route.RateLimit, context))
            {
                context.Response.StatusCode = 429;
                _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: rate limit exceeded", method, path, context.Request.RemoteEndPoint);
                return;
            }

            var body = Array.Empty<byte>();
            if (route.HasBody)
            {
                var read = await RequestBodyReader.ReadWithCapAsync(context.Request.InputStream, context.Request.ContentLength64, MaxBodyBytes);
                if (read == null)
                {
                    context.Response.StatusCode = 413;
                    _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: body exceeded {MaxBytes} bytes", method, path, context.Request.RemoteEndPoint, MaxBodyBytes);
                    return;
                }
                body = read;
            }

            string? fingerprint = null;
            if (route.Auth == AuthMode.SelfSigned)
            {
                fingerprint = PeerSignatureAuth.VerifySelfSigned(ToSignedRequest(context, body), _nonceReplayGuard, DateTimeOffset.UtcNow);
                if (fingerprint == null)
                {
                    context.Response.StatusCode = 401;
                    _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: self-signed verification failed", method, path, context.Request.RemoteEndPoint);
                    return;
                }
            }
            else if (route.Auth == AuthMode.TrustedPeer)
            {
                // 403 and 401 mean very different things to the caller here:
                // a client that gets a 403 off a sync route concludes it has
                // been revoked and unpairs itself, so that answer is reserved
                // for a peer there is genuinely no key on file for. A
                // signature that simply did not verify - most often a stale
                // timestamp from a caller that suspended with the request in
                // flight - is a 401 and costs nothing but this attempt. See
                // PeerSignatureAuth.AuthenticateTrustedPeer.
                var auth = PeerSignatureAuth.AuthenticateTrustedPeer(ToSignedRequest(context, body), _trustedPeerStore.GetPublicKey, _nonceReplayGuard, DateTimeOffset.UtcNow);
                fingerprint = auth.Fingerprint;
                if (auth.Failure != PeerAuthFailure.None)
                {
                    context.Response.StatusCode = auth.Failure == PeerAuthFailure.NotTrusted ? 403 : 401;
                    _logger.LogWarning("Rejected {Method} {Path} from {RemoteEndPoint}: {Reason}", method, path, context.Request.RemoteEndPoint,
                        auth.Failure == PeerAuthFailure.NotTrusted ? "not a trusted peer" : "signature did not verify");
                    return;
                }

                // Bulk-sync endpoints only (not browse/stream - see
                // SyncRolePolicy.ShouldRejectPeerAsServer's own doc comment) -
                // a correctly-behaving Server never initiates bulk sync at
                // all, so this is defense-in-depth against a caller that also
                // claims to be a Server somehow reaching this endpoint.
                if (route.IsBulkSync && SyncRolePolicy.ShouldRejectPeerAsServer(_appSettings.IsServer, IsCallerServer(context)))
                {
                    context.Response.StatusCode = 403;
                    _logger.LogWarning("Rejected bulk sync {Method} {Path} from {RemoteEndPoint}: caller also advertises Server role", method, path, context.Request.RemoteEndPoint);
                    return;
                }
            }

            await route.Handler(context, body);
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

    private bool CheckRateLimit(RateLimitCategory category, HttpListenerContext context)
    {
        var now = DateTimeOffset.UtcNow;
        return category switch
        {
            RateLimitCategory.Info => _infoRateLimiter.TryAcquire(RemoteIpKey(context), now),
            RateLimitCategory.Pair => _pairRateLimiter.TryAcquire(RemoteIpKey(context), now),
            RateLimitCategory.Bulk => _bulkRateLimiter.TryAcquire(FingerprintKey(context), now),
            RateLimitCategory.Browse => _browseRateLimiter.TryAcquire(FingerprintKey(context), now),
            RateLimitCategory.Stream => _streamRateLimiter.TryAcquire(FingerprintKey(context), now),
            _ => true,
        };
    }

    private static string RemoteIpKey(HttpListenerContext context) => context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";

    // Self-reported at this point (auth hasn't run yet when rate limiting is
    // checked) - fine for a rate-limit key: a TrustedPeer route still 403s an
    // untrusted/unverifiable fingerprint regardless of how many requests it
    // sent, so spreading requests across fake fingerprints buys an attacker
    // nothing but individual rejections.
    private static string FingerprintKey(HttpListenerContext context) => GetIdentityValue(context, "X-Flower-Fingerprint") ?? "unknown";

    // Header if present, else the same name as a query param. The rule itself
    // lives on SignedRequest.Identity (shared with Flower.Server); this is the
    // convenience wrapper for the handlers below, which only ever want one
    // value and have no body to canonicalize.
    private static string? GetIdentityValue(HttpListenerContext context, string name) =>
        ToSignedRequest(context, []).Identity(name);

    // The one place an HttpListenerContext is turned into the transport-
    // agnostic shape both servers verify against - see SignedRequest.
    // Flower.Server has the one matching method for Kestrel and nothing else.
    private static SignedRequest ToSignedRequest(HttpListenerContext context, byte[] body)
    {
        var qs = context.Request.QueryString;
        var query = new List<(string Key, string Value)>();
        foreach (var key in qs.AllKeys)
        {
            if (key == null)
                continue;
            foreach (var value in qs.GetValues(key) ?? [])
                query.Add((key, value));
        }

        return new SignedRequest(
            context.Request.HttpMethod, context.Request.Url!.AbsolutePath, query, body,
            name => context.Request.Headers[name]);
    }

    private static bool IsCallerServer(HttpListenerContext context) =>
        string.Equals(GetIdentityValue(context, "X-Flower-Role"), "server", StringComparison.OrdinalIgnoreCase);

    // The one and only call that can raise PeerApprovalRequested - see this
    // file's top-of-file doc comment. Reached only from PeerPairingService,
    // itself only ever called from MainViewModel.PairWithServer's explicit
    // "Ask to pair" action (ServerPickerView's button on desktop, the
    // Ask-to-pair confirm sheet on mobile) - never automatically off some
    // other request's back. The dispatcher's AuthMode.SelfSigned check has
    // already proven the caller holds the private key behind the fingerprint/
    // public key it's presenting by the time this runs.
    private async Task HandlePairRequestAsync(HttpListenerContext context, byte[] body)
    {
        var fingerprint = GetIdentityValue(context, "X-Flower-Fingerprint")!;
        var publicKey = GetIdentityValue(context, "X-Flower-PublicKey")!;
        var alias = GetIdentityValue(context, "X-Flower-Alias");
        if (string.IsNullOrEmpty(alias))
            alias = fingerprint;

        if (_trustedPeerStore.IsTrusted(fingerprint))
        {
            context.Response.StatusCode = 204; // Already paired - idempotent, no prompt needed.
            return;
        }

        _logger.LogInformation("Pair request from {Alias} ({Fingerprint})", alias, fingerprint);
        var approved = await RequestApprovalAsync(fingerprint, alias);
        _logger.LogInformation("Pair request from {Alias} ({Fingerprint}) result: {Approved}", alias, fingerprint, approved);
        if (approved)
            await _trustedPeerStore.ApproveAsync(fingerprint, alias, publicKey);
        else
            await _trustedPeerStore.DenyAsync(fingerprint, alias);

        context.Response.StatusCode = approved ? 204 : 403;
    }

    // Concurrent pair-requests from the same not-yet-decided fingerprint (a
    // retried "Ask to pair" tap, say) share a single prompt/TaskCompletionSource
    // rather than surfacing the approve/deny dialog twice. Only the caller that
    // actually creates the pending entry raises the event and starts the
    // timeout; every other concurrent caller just awaits the same task.
    // internal rather than private so a test can drive the *subscriber* side -
    // MainViewModel's handler, which fails closed when no UI is listening to
    // its own PeerApprovalRequested and otherwise marshals the prompt to the UI
    // thread. Reaching it through a real pair request means standing up a
    // socket and a keypair to test a branch that has nothing to do with either
    // (SyncHttpServerRoundTripTests already covers that path). See
    // docs/ARCHITECTURE-REVIEW.md Tier 5.6.
    internal async Task<bool> RequestApprovalAsync(string fingerprint, string alias)
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

    // Server-initiated counterpart to a local "Forget" (see
    // TrustedDevicesView/PeerUnpairNotifier) - lets a revoked peer clear its
    // own stale pairing proactively rather than only discovering it passively
    // via a later 403 or /info poll. Idempotent and side-effect-free beyond
    // raising the event: 204 regardless of whether this device even has a
    // pairing matching that fingerprint, so it never leaks local state to the
    // caller.
    private Task HandleUnpairNotifyAsync(HttpListenerContext context, byte[] body)
    {
        var fingerprint = GetIdentityValue(context, "X-Flower-Fingerprint")!;
        var alias = GetIdentityValue(context, "X-Flower-Alias") ?? fingerprint;

        PeerUnpairNotified?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = fingerprint, Alias = alias });

        context.Response.StatusCode = 204;
        return Task.CompletedTask;
    }


    // Deliberately ungated (see the route table's AuthMode.Open: a peer has to
    // learn our fingerprint+public key here before either side can evaluate
    // trust at all) - but if the caller already identifies itself via the
    // same X-Flower-Fingerprint header every gated endpoint expects, this
    // answers whether we currently trust it, via trustsCaller. That's what
    // NetworkDiscoveryService.ResolveAliasAsync polls every ~5s for every
    // known peer (not just during an active sync attempt), so a Client whose
    // paired Server just revoked (or never granted) it finds out on this
    // device's own timetable rather than needing to be mid-sync - see
    // DiscoveredDevice.TrustsUs. trustsCaller is omitted (not false) when
    // the caller didn't identify itself, so a plain unauthenticated /info
    // probe can't be misread as a rejection. This existence check
    // (IsTrusted) is intentionally unauthenticated/display-only - it grants
    // no access, unlike the gated endpoints' full signature verification.
    private async Task HandleInfoAsync(HttpListenerContext context, byte[] body)
    {
        var deviceType = OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() ? "mobile" : "desktop";
        var callerFingerprint = GetIdentityValue(context, "X-Flower-Fingerprint");
        var responseDto = new SyncInfoResponseDto(
            _deviceIdentity.Alias,
            "2.0",
            null,
            deviceType,
            _deviceIdentity.Fingerprint,
            _deviceSigningKey.PublicKeyBase64,
            _appSettings.IsServer,
            false,
            string.IsNullOrEmpty(callerFingerprint) ? null : _trustedPeerStore.IsTrusted(callerFingerprint),
            // The same token GET /api/flower/v1/library serves as its ETag.
            // This is what closes Tier 1.4's actual correctness gap: sync
            // previously fired only on first mDNS contact or a debounced
            // *local* change, so a change made on the server was never
            // noticed at all while both apps stayed running. Carrying it on
            // the poll every client already runs (~5s, see
            // NetworkDiscoveryService.ResolveAliasAsync) means a client sees
            // a changed server catalog promptly without a new connection, a
            // new endpoint, or anything long-lived to keep alive on mobile.
            _library.ChangeToken,
            // Only worth reporting when this app is acting as a Server: a
            // Client is dialled by nobody, so a list of addresses to reach it
            // on is noise a peer would remember and never use. See
            // LocalAddresses and REMOTE-ACCESS-PLAN.md.
            _appSettings.IsServer && BoundPort is { } boundPort ? LocalAddresses.Reachable(boundPort) : null);
        var responseBody = JsonSerializer.Serialize(responseDto, SyncProtocolJsonContext.Default.SyncInfoResponseDto);
        await WriteJsonAsync(context, responseBody);
    }

    private async Task HandleGetPlaylistsAsync(HttpListenerContext context, byte[] body)
    {
        var manifest = PlaylistSyncMapper.ToManifest(_deviceIdentity.Fingerprint, _library.Playlists);
        await WriteJsonAsync(context, JsonSerializer.Serialize(manifest, FlowerJsonContext.Default.PlaylistSyncManifestDto));
    }

    // Bulk, non-OpenSubsonic endpoint for LibrarySyncService - see
    // LibrarySyncContracts for why this exists alongside (not instead of)
    // /rest/getAlbumList2+getAlbum.
    //
    // Conditional on Library.ChangeToken, which is served as the ETag: a
    // caller that sends back the token it already holds gets 304 and no body
    // at all. Both halves matter (ARCHITECTURE-REVIEW Tier 1.4) - this
    // response is 6-8 MB of JSON at a 16k-track library, and *building* it
    // costs ~1,400 TagLib opens for the per-album art hashes, so the previous
    // behaviour meant editing one playlist track re-serialized and re-hashed
    // a peer's entire library. The serialized body is cached alongside the
    // token it was built from, so even a genuine cache miss from several
    // peers at once only builds it once.
    private readonly object _manifestCacheLock = new();
    private string? _cachedManifestToken;
    private string? _cachedManifestJson;

    private async Task HandleGetLibraryAsync(HttpListenerContext context, byte[] body)
    {
        var token = _library.ChangeToken;
        context.Response.Headers["ETag"] = token;

        if (context.Request.Headers["If-None-Match"] == token)
        {
            context.Response.StatusCode = 304;
            _logger.LogDebug("Library manifest unchanged ({Token}) for {RemoteEndPoint}, answered 304", token, context.Request.RemoteEndPoint);
            return;
        }

        string json;
        lock (_manifestCacheLock)
        {
            if (_cachedManifestToken != token || _cachedManifestJson == null)
            {
                var manifest = new LibrarySyncManifestDto(
                    _deviceIdentity.Fingerprint,
                    LibraryOpenSubsonicMapper.BuildAllSongs(_library.Snapshot, _deviceIdentity.Fingerprint));
                _cachedManifestJson = JsonSerializer.Serialize(manifest, FlowerJsonContext.Default.LibrarySyncManifestDto);
                _cachedManifestToken = token;
                _logger.LogDebug("Rebuilt library manifest at token {Token} ({Bytes} bytes)", token, _cachedManifestJson.Length);
            }
            json = _cachedManifestJson;
        }

        await WriteJsonAsync(context, json);
    }

    // A Client's pushed log snapshot (see LibrarySyncService.PushLogSnapshotAsync).
    // Stored under the header identity the dispatcher already validated this
    // request against, not whatever fingerprint the JSON body itself claims -
    // the body hasn't been independently authenticated, only the headers have.
    private async Task HandleReportLogAsync(HttpListenerContext context, byte[] body)
    {
        var json = Encoding.UTF8.GetString(body);
        var report = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.LogReportDto);
        if (report == null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var fingerprint = GetIdentityValue(context, "X-Flower-Fingerprint")!;
        var alias = GetIdentityValue(context, "X-Flower-Alias") ?? report.Alias;
        _clientLogStore.SetSnapshot(fingerprint, alias, report.Entries, DateTimeOffset.UtcNow);
        _logger.LogDebug("Received log snapshot from {Alias} ({Fingerprint}): {Count} line(s)", alias, fingerprint, report.Entries.Count);

        context.Response.StatusCode = 204;
    }

    // The initiator of a sync session (see PlaylistSyncService) has already resolved
    // every conflict by the time it POSTs here, so this side just replaces its whole
    // playlist collection to match - no merge logic runs on this end.
    private async Task HandleApplyPlaylistsAsync(HttpListenerContext context, byte[] body)
    {
        var json = Encoding.UTF8.GetString(body);
        var manifest = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.PlaylistSyncManifestDto);
        if (manifest == null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var playlists = manifest.Playlists
            .Select(dto => PlaylistSyncMapper.ToPlaylist(dto, _library.Tracks))
            .ToList();
        // ReplacePlaylists persists, via Library.PlaylistsChanged - and only
        // when the incoming set is actually different, where this used to write
        // playlists.json on every push whether anything changed or not.
        _library.ReplacePlaylists(playlists);

        _logger.LogInformation("Received and applied {Count} playlist(s) pushed from a peer", playlists.Count);

        context.Response.StatusCode = 204;
    }

    // Embedded OpenSubsonic host (SYNC-PLAN.md Phase 3's "one client, three
    // interchangeable servers"): serves this device's own real (Path != null)
    // tracks for LibrarySyncService to pull from a peer. Only getAlbumList2/
    // getAlbum are implemented - nothing else calls getArtists/getSong/stream yet
    // (the mobile download-button UI, which needs stream, is a later piece).
    private async Task HandleGetAlbumList2Async(HttpListenerContext context, byte[] body)
    {
        var query = context.Request.QueryString;
        var size = int.TryParse(query["size"], out var s) ? s : 500;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(_library.Snapshot)
            .Skip(offset)
            .Take(size)
            .ToList();

        var responseBody = JsonSerializer.Serialize(new SubsonicEnvelope
        {
            Response = new SubsonicResponse { Status = "ok", Version = "1.16.1", AlbumList2 = new AlbumList2(albums) },
        }, ExternalProtocolJsonContext.Default.SubsonicEnvelope);
        await WriteJsonAsync(context, responseBody);
    }

    private async Task HandleGetAlbumAsync(HttpListenerContext context, byte[] body)
    {
        var id = context.Request.QueryString["id"];
        var album = id != null ? LibraryOpenSubsonicMapper.FindAlbum(_library.Snapshot, id, _deviceIdentity.Fingerprint) : null;
        if (album == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var responseBody = JsonSerializer.Serialize(new SubsonicEnvelope
        {
            Response = new SubsonicResponse { Status = "ok", Version = "1.16.1", Album = album },
        }, ExternalProtocolJsonContext.Default.SubsonicEnvelope);
        await WriteJsonAsync(context, responseBody);
    }

    // Serves this device's own real file bytes for one song, looked up by
    // Track.Id (the same id LibraryOpenSubsonicMapper.ToChild hands out and the
    // requesting peer stored as Track.OriginTrackId - see LibraryDownloadService,
    // SYNC-PLAN.md Phase 3's download button). Never serves a placeholder - only
    // a track this device actually has a file for.
    private async Task HandleStreamAsync(HttpListenerContext context, byte[] body)
    {
        // Library.Find, not a scan: this used to format every track's Guid to
        // compare it against the query string, which both duplicated the id
        // conversion Library already owns and made a stream request O(n) with
        // an allocation per track. The Path check stays here - "has a real
        // file" is this endpoint's rule, not the library's.
        var id = context.Request.QueryString["id"];
        var track = _library.Find(id);

        if (track?.Path == null || !File.Exists(track.Path))
        {
            // No logging previously existed anywhere on this path - a failed
            // stream request (bad/stale id, or the file moved/got deleted
            // since this device last reported having it) was completely
            // invisible in the server's own log, indistinguishable from the
            // request never having arrived at all.
            _logger.LogWarning("Stream request for id {Id} from {RemoteEndPoint} could not be served: {Reason}",
                id, context.Request.RemoteEndPoint,
                id == null ? "no id supplied" : track == null ? "no matching track for that id" : "file missing on disk");
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        // Advertised unconditionally, including on the full-body response: a
        // client only knows it may resume an interrupted download if it saw
        // this on the *first* (unranged) response. Flower.Server gets this for
        // free from Results.File(..., enableRangeProcessing: true).
        context.Response.Headers["Accept-Ranges"] = "bytes";

        using var fileStream = File.OpenRead(track.Path);
        var total = fileStream.Length;
        var rangeHeader = context.Request.Headers["Range"];
        var range = ParseSingleByteRange(rangeHeader, total);

        if (range == UnsatisfiableRange)
        {
            // RFC 9110 14.4: a syntactically valid range that starts past the
            // end gets 416 plus the real length, so the client can recover
            // rather than guess. This is the case a resuming client hits when
            // its partial file is somehow longer than the track now is.
            _logger.LogWarning("Stream request for id {Id} from {RemoteEndPoint} asked for unsatisfiable range {Range} of {Total} bytes",
                id, context.Request.RemoteEndPoint, rangeHeader, total);
            context.Response.StatusCode = 416;
            context.Response.Headers["Content-Range"] = $"bytes */{total}";
            context.Response.ContentLength64 = 0;
            return;
        }

        var (start, end) = range ?? (0, total - 1);
        var length = end - start + 1;

        if (range != null)
        {
            context.Response.StatusCode = 206;
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
            fileStream.Seek(start, SeekOrigin.Begin);
        }

        _logger.LogInformation("Streaming {Title} ({Id}, bytes {Start}-{End} of {Total}) to {RemoteEndPoint}",
            track.Title, id, start, end, total, context.Request.RemoteEndPoint);
        context.Response.ContentLength64 = length;
        await CopyRangeAsync(fileStream, context.Response.OutputStream, length);
    }

    // Distinct from null (no range asked for) and from a real (start, end):
    // "asked for a range that cannot be served", which is a 416 rather than a
    // silent full body. A negative start is rejected by the parser, so this
    // sentinel value can never collide with a real range.
    public static readonly (long Start, long End)? UnsatisfiableRange = (-1, -1);

    // Single byte range only ("bytes=100-", "bytes=100-199", "bytes=-500"),
    // which is all any streaming or resuming client sends. A multipart range
    // request, or anything malformed, returns null - RFC 9110 14.2 says a
    // recipient that cannot interpret a Range header must ignore it and serve
    // the whole representation, which is strictly better than failing.
    public static (long Start, long End)? ParseSingleByteRange(string? header, long total)
    {
        if (header == null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var spec = header["bytes=".Length..].Trim();
        if (spec.Contains(','))
            return null;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return null;

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        // Suffix form: the *last* N bytes. N == 0 asks for nothing, which is
        // unsatisfiable rather than an empty 206.
        if (fromText.Length == 0)
        {
            if (!long.TryParse(toText, out var suffix) || suffix < 0)
                return null;
            if (suffix == 0)
                return UnsatisfiableRange;

            var suffixStart = Math.Max(0, total - suffix);
            return total == 0 ? UnsatisfiableRange : (suffixStart, total - 1);
        }

        if (!long.TryParse(fromText, out var start) || start < 0)
            return null;
        if (start >= total)
            return UnsatisfiableRange;

        if (toText.Length == 0)
            return (start, total - 1);
        if (!long.TryParse(toText, out var end) || end < start)
            return null;

        // An end past the end is clamped, not refused (RFC 9110 14.1.1).
        return (start, Math.Min(end, total - 1));
    }

    // CopyToAsync would send everything from the seek position to EOF, which is
    // wrong for a bounded "bytes=0-1023" request and would contradict the
    // Content-Length already written.
    private static async Task CopyRangeAsync(Stream source, Stream destination, long length)
    {
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    // Serves this device's own real album art bytes, looked up by album id (the
    // same id LibraryOpenSubsonicMapper.AlbumId/ToChild's CoverArt is derived
    // from) - see AlbumArtLoader's remote-fetch path, SYNC-PLAN.md Phase 3's
    // synced art. Never serves art for an album with no local (Path != null)
    // track at all, same "only what this device actually has" rule as /rest/stream.
    private async Task HandleGetCoverArtAsync(HttpListenerContext context, byte[] body)
    {
        var id = context.Request.QueryString["id"];
        var track = id != null
            ? _library.Snapshot.AlbumTracks(id).FirstOrDefault(t => t.Path != null)
            : null;

        // The art's own MIME type, carried out of the lookup rather than
        // sniffed back out of the bytes here - which used to mean every
        // format that isn't PNG went out labelled as JPEG.
        var art = track != null ? LocalAlbumArtReader.ForFile(track.Path, _logger) : null;
        if (art == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = art.MimeType;
        context.Response.ContentLength64 = art.Bytes.Length;
        await context.Response.OutputStream.WriteAsync(art.Bytes);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, string responseBody)
    {
        var bytes = Encoding.UTF8.GetBytes(responseBody);
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

    // Rebinds a fresh HttpListener on the same port range - meant to be
    // called alongside NetworkDiscoveryService.Restart when an iOS app
    // returns to the foreground (see AppDelegate.CustomizeAppBuilder). Real
    // iOS hardware has the same failure mode here as that class's
    // NSNetService publication: suspending the app while backgrounded can
    // silently invalidate the listener's underlying socket. .NET's
    // HttpListener object survives that (IsListening can still report true),
    // but ListenLoopAsync's GetContextAsync() throws once the socket is
    // actually dead, its catch assumes that means a deliberate Stop() and
    // exits for good - so nothing is left accepting connections at all,
    // observed from a peer's side as a bare "Connection refused" against a
    // port that used to work. Swallows a failure while tearing down the old
    // listener (it may already be in whatever abnormal state caused this)
    // since the fresh Start() below is what actually matters here.
    public void Restart()
    {
        _logger.LogInformation("Restarting sync HTTP listener");
        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure while stopping the old listener before restart");
        }
        finally
        {
            _listener?.Close();
        }

        Start();
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }
}
