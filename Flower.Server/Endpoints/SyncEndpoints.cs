using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// Flower's own device-to-device sync protocol (/api/flower/v1/*), the half a
// paired client actually bulk-syncs through - as opposed to /rest/*, which is
// the published OpenSubsonic surface third-party clients speak.
//
// This server answered none of it until now, so a client that paired with it
// then failed its very first sync on a flat 404 from GET /library: pairing
// made the client a Client of this Server (SyncRolePolicy), and a Client pulls
// its whole catalog from its Server through this endpoint rather than through
// /rest/getAlbumList2 (see LibrarySyncContracts for why the bulk shape exists
// alongside the per-album one).
//
// Deliberately only the three routes a Client drives against its Server:
//
//   GET  /library         - the whole track catalog in one response
//   GET  /playlists       - this server's playlists, for the merge
//   POST /playlists/apply - the merged result the client resolved
//   POST /plays           - what a browser tab played, to count here
//   POST /track-state     - what a paired device has played, starred and
//                           configured, as its own current values
//   POST /log/report      - the caller's own recent log lines, for the owner
//                           to read back through the admin API
//
// Not here, and not accidentally omitted: pair-request (this server pairs by
// code instead - see PairingEndpoints) and unpair-notify (nothing server-side
// initiates a revoke that way; the admin API revokes directly, and the client
// finds out from the 403 its next request gets).
public static class SyncEndpoints
{
    // These are a handful of large requests per sync session, not a stream of
    // small ones, so the budget is small and the window is long.
    private static readonly RateLimiter BulkLimiter = new(max: 20, TimeSpan.FromSeconds(60));

    // Cover art is the exception in this group, and it must not be charged to
    // the budget above: it is one small request per album tile, so a browser
    // head painting an album grid spends twenty in the time it takes to scroll
    // a screen - and then the 429 lands on GET /library, which is the one route
    // in here that actually matters. The art throttled the sync. Same ceiling
    // /rest browsing gets (SubsonicEndpoints.RequestLimiter), because it is the
    // same kind of traffic.
    private static readonly RateLimiter ArtLimiter = new(max: 600, TimeSpan.FromSeconds(60));

    // Composed from the same two pieces the route is mapped from, so renaming
    // it can't silently drop cover art back onto BulkLimiter - the filter sees
    // a whole path, MapGet sees a suffix, and they cannot disagree.
    private const string GroupPrefix = "/api/flower/v1";
    private const string CoverArtRoute = "/cover-art";
    private const string CoverArtPath = GroupPrefix + CoverArtRoute;
    private const string CoverArtBatchRoute = "/cover-art/batch";
    private const string CoverArtBatchPath = GroupPrefix + CoverArtBatchRoute;

    // A playlist manifest for a large library, with a wide margin - the same
    // ceiling Kestrel is capped at process-wide (see Program.cs), applied here
    // as the route's own limit so a rejection is a 413 rather than a read that
    // runs to 20 MB before failing.
    private const long MaxBodyBytes = 20 * 1024 * 1024;

    // The wire format is whatever the client's FlowerJsonContext writes:
    // PascalCase (no naming policy) with nulls omitted. Reflection-based here
    // for the same reason SubsonicResults is - this host is neither trimmed
    // nor AOT-compiled.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapSyncEndpoints(this WebApplication app)
    {
        // Once, at map time, and captured by the filter and handlers below -
        // not rebuilt from an ILoggerFactory on every request, which is what
        // this used to do in six separate places.
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SyncEndpoints));

        var sync = app.MapGroup(GroupPrefix).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var key = RateLimiter.KeyFor(http.Connection.RemoteIpAddress);
            var limiter = IsCoverArt(http.Request.Path) ? ArtLimiter : BulkLimiter;
            var now = DateTimeOffset.UtcNow;
            if (!limiter.TryAcquire(key, now))
            {
                // Debug: a peer that syncs enthusiastically trips this without
                // anything being wrong, and the caller is unauthenticated at
                // this point, so this cannot distinguish a busy phone from a
                // stranger. It is here so that "sync got slow" has a visible
                // cause rather than none - and throttled, because being rate
                // limited is precisely the state that repeats.
                if (RateLimitLogThrottle.ShouldLog(key, now, out var suppressed))
                {
                    RateLimitLogThrottle.Prune(now);
                    logger.LogDebug(
                        "Rate-limited {Method} {Path} from {RemoteAddress}.{AlsoSuppressed}",
                        http.Request.Method, http.Request.Path.Value, key,
                        suppressed == 0 ? "" : $" ({suppressed} more since the last one.)");
                }

                return SubsonicEndpoints.RateLimited(http);
            }

            if (http.Request.ContentLength > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            // The body has to be buffered before the signature can cover it,
            // and before model binding consumes it - the signed bytes are the
            // ones that arrived, not a re-serialization of what bound.
            byte[] body = [];
            if (http.Request.ContentLength is > 0)
            {
                http.Request.EnableBuffering();
                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
                body = buffer.ToArray();
                http.Request.Body.Position = 0;
            }

            var trustedPeers = services.GetRequiredService<TrustedPeerStore>();
            var replayGuard = services.GetRequiredService<NonceReplayGuard>();
            // 403 only for a caller this server genuinely has no key on file
            // for - a client treats that as "revoked" and unpairs itself. A
            // signature that just failed to verify (commonly a stale
            // timestamp, after the caller suspended mid-request) is a 401:
            // this attempt failed, the pairing is untouched.
            //
            // A signature, and only a signature. The browser head pulls its
            // whole library through GET /library below and used to be admitted
            // here on an admin-session bearer token instead, because
            // .NET-for-WebAssembly cannot sign - it signs with a WebCrypto key
            // now like everything else (see BrowserPeerCredentials).
            var auth = DeviceSignatureAuth.AuthenticateTrustedPeer(
                http.Request, body, trustedPeers, replayGuard, logger);
            if (auth.Failure == PeerAuthFailure.NotTrusted)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (auth.Failure != PeerAuthFailure.None)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            // Who the gate actually let through, for the handlers below to
            // attribute a write to. Not the same as the request's own
            // X-Flower-Fingerprint header, which is a claim rather than a
            // finding: this is the fingerprint whose signature actually
            // verified.
            context.HttpContext.Items[AuthenticatedFingerprintKey] = auth.Fingerprint;

            return await next(context);
        });

        sync.MapGet("/library", GetLibrary);
        sync.MapGet("/playlists", GetPlaylists);
        // Lambdas rather than method-group references purely so the logger
        // captured above reaches these three: a bare, non-generic ILogger is
        // not something the container can resolve as a handler parameter.
        sync.MapPost("/playlists/apply",
            (HttpContext context, Library library) => ApplyPlaylists(context, library, logger));
        sync.MapPost("/plays",
            (HttpContext context, PlayReportService plays) => ReportPlays(context, plays, logger));
        sync.MapPost("/track-state",
            (HttpContext context, Library library, TrustedPeerStore trustedPeers) =>
                ReportTrackState(context, library, trustedPeers, logger));
        sync.MapPost("/log/report",
            (HttpContext context, ClientLogStore logs) => ReportLog(context, logs, logger));
        // What this server already holds for the caller, so a client that has
        // just started up knows where to resume from instead of re-offering
        // its whole retained week. Only needed once per session - every
        // /log/report answers with the same shape.
        sync.MapGet("/log/watermark", GetLogWatermark);

        // The same album art /rest/getCoverArt serves, behind this group's gate
        // instead of the Subsonic one. A browser tab holds a session token and
        // no signing key, so /rest is a door it cannot open - and unlike
        // playback, art needs no stream ticket to get through this one, because
        // AlbumArtLoader fetches it with an HttpClient that can send the header
        // (an <audio> element is what cannot). Deliberately the existing
        // handler rather than a second implementation of "an album's art".
        sync.MapGet(CoverArtRoute, SubsonicEndpoints.GetCoverArt);

        // The same art, for up to CoverArtBatch.MaxIds albums at once.
        //
        // One request per tile is what a grid naturally does and what a server
        // cannot afford to be asked: a 1400-album library is 1400 requests
        // during one cold scroll, which is more than any per-source budget
        // worth having, and the traffic that got refused when that budget ran
        // out was playback. Batching is the fix that removes the burst rather
        // than raising the ceiling until it stops hurting - see
        // CoverArtBatch's own header, and AlbumArtLoader, which coalesces the
        // asking end.
        //
        // POST rather than GET because the id list is the request: thirty-odd
        // album ids do not belong in a query string, and this group signs
        // bodies already.
        sync.MapPost(CoverArtBatchRoute, (HttpContext context, Library library) => GetCoverArtBatch(context, library));
    }

    // Both cover-art routes share ArtLimiter. The batch one especially: it is
    // the route that exists so art stops competing with playback, and putting
    // it back in the general budget would undo exactly that.
    private static bool IsCoverArt(PathString path) =>
        path.Equals(CoverArtPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(CoverArtBatchPath, StringComparison.OrdinalIgnoreCase);

    // Deliberately built on SubsonicEndpoints.CoverArtCandidates, the same
    // "which files is this id's art in" rule the single-id route and the admin
    // replace route both use. A second answer to that question is how a batch
    // starts returning different pictures from the endpoint it is meant to
    // replace.
    private static IResult GetCoverArtBatch(HttpContext context, Library library)
    {
        var ids = ReadBatchRequest(context);
        if (ids == null)
            return Results.BadRequest();

        var entries = new List<(string Id, byte[] Bytes)>(ids.Count);
        var total = 0;

        foreach (var id in ids)
        {
            byte[] bytes = [];
            foreach (var candidate in SubsonicEndpoints.CoverArtCandidates(id, library))
            {
                if (LocalAlbumArtReader.ForFile(candidate.Path) is { } art)
                {
                    bytes = art.Bytes;
                    break;
                }
            }

            // Truncation, not failure: the covers already gathered are worth
            // sending, and the caller asks again for the ids that are missing
            // from the answer. Checked before appending so one very large
            // picture cannot carry the response past the cap.
            if (total + bytes.Length > CoverArtBatch.MaxBytes && entries.Count > 0)
                break;

            entries.Add((id, bytes));
            total += bytes.Length;
        }

        return Results.Bytes(CoverArtBatch.Write(entries), CoverArtBatch.ContentType);
    }

    // Null for anything malformed or over the cap. The list is a list of file
    // reads, so its length is not the caller's to choose without a bound.
    private static List<string>? ReadBatchRequest(HttpContext context)
    {
        try
        {
            context.Request.Body.Position = 0;
            var request = JsonSerializer.Deserialize<CoverArtBatchRequest>(context.Request.Body, JsonOptions);
            if (request?.Ids is not { Count: > 0 } ids || ids.Count > CoverArtBatch.MaxIds)
                return null;

            return ids.Where(id => !string.IsNullOrEmpty(id)).ToList()!;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed class CoverArtBatchRequest
    {
        public List<string>? Ids { get; set; }
    }

    private static readonly RefusalLogThrottle RateLimitLogThrottle = new();

    private const string AuthenticatedFingerprintKey = "flower.auth.fingerprint";

    // Conditional on Library.ChangeToken, served as the ETag: a client that
    // sends back the token it already holds gets a 304 and no body. Worth as much here as there - this is
    // megabytes of JSON at a real library size, rebuilt for every peer that
    // asks otherwise - so the serialized body is cached alongside the token it
    // was built from, and several peers missing the cache at once still only
    // build it once.
    private static IResult GetLibrary(
        HttpContext context, Library library, DeviceSigningKey signingKey, LibraryManifestCache cache,
        IOptionsMonitor<FlowerServerOptions> options)
    {
        var token = library.ChangeToken;
        context.Response.Headers.ETag = token;

        if (context.Request.Headers.IfNoneMatch.ToString() == token)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var json = cache.Get(token, () =>
        {
            // Only tracks this server actually has a file for, the same rule
            // the OpenSubsonic surface follows: a placeholder learned from
            // somewhere else is not this device's to advertise.
            var songs = library.Snapshot.Albums
                .SelectMany(album => album.Tracks)
                .Where(track => track.Path != null)
                // Under this server's own fingerprint, so a client merging
                // this manifest files the counts as *this device's* rather
                // than its own - see Track.RemotePlayCounts, and
                // SubsonicMapper.ToChild for why the /rest browse endpoints
                // deliberately do not pass one.
                .Select(track => SubsonicMapper.ToChild(track, signingKey.Fingerprint, options.CurrentValue.LibraryPaths))
                .ToList();
            return JsonSerializer.Serialize(new LibrarySyncManifestDto(signingKey.Fingerprint, songs), JsonOptions);
        });

        return Results.Text(json, "application/json");
    }

    private static IResult GetPlaylists(Library library, DeviceSigningKey signingKey) =>
        Results.Text(
            JsonSerializer.Serialize(
                PlaylistSyncMapper.ToManifest(signingKey.Fingerprint, library.Playlists), JsonOptions),
            "application/json");

    // The initiator resolved every conflict before POSTing here (see
    // PlaylistSyncService), so this side replaces its collection wholesale -
    // no second, independently divergent merge runs on this end.
    private static async Task<IResult> ApplyPlaylists(HttpContext context, Library library, ILogger logger)
    {
        using var reader = new StreamReader(context.Request.Body);
        var manifest = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (manifest == null)
            return Results.BadRequest();

        var playlists = manifest.Playlists
            .Select(dto => PlaylistSyncMapper.ToPlaylist(dto, library.Tracks))
            .ToList();
        // Persists itself, through the same PlaylistRepository the client's
        // own Library writes through.
        library.ReplacePlaylists(playlists);

        logger.LogInformation(
            "Applied {Count} playlist(s) pushed by {Fingerprint}",
            playlists.Count, context.Items[AuthenticatedFingerprintKey]);

        return Results.NoContent();
    }

    // A browser tab's plays, counted here because there is nowhere else for
    // them to be counted - see IPlayReporter. Not the peer-to-peer path: two
    // desktops exchange durable per-device totals through the library manifest
    // instead (Track.RemotePlayCounts), which is the better instrument for
    // both sides that can keep one.
    //
    // Gated like every other route in this group, on a trusted peer's
    // signature. Worth naming what a caller through it can do: inflate this
    // server's play counts. That is a nuisance, not a disclosure, and it is
    // bounded by being a paired device at all.
    // A paired device's own recent log lines, pushed at the end of each sync
    // session it runs (see LibrarySyncService.PushLogSnapshotAsync). Overlapping
    // snapshots merge into the server's durable seven-day history. The whole
    // point is the person who runs the server being able to see why somebody
    // else's phone is misbehaving without asking them to find a log file, so
    // it is stored against the device and read back through the admin API -
    // see AdminEndpoints' /devices/{fingerprint}/logs.
    //
    // Filed under the fingerprint the *signature* proved, never the one the
    // body claims: the body is attacker-controlled on a route any trusted
    // device can call, and believing it would let one paired device overwrite
    // another's log with whatever it liked.
    private static async Task<IResult> ReportLog(
        HttpContext context, ClientLogStore logs, ILogger logger)
    {
        using var reader = new StreamReader(context.Request.Body);
        var report = JsonSerializer.Deserialize<LogReportDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (report == null)
            return Results.BadRequest();

        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        var stored = logs.SetSnapshot(fingerprint, report.Alias, report.Entries, DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Stored {LineCount} log line(s) from {Alias} ({Fingerprint})",
            report.Entries.Count, report.Alias, fingerprint);

        // The answer to "what have you got?", so the caller's next push starts
        // exactly here. Reported from what is retained rather than from what
        // arrived: a line older than the retention window was accepted and
        // dropped, and telling the client otherwise would have it wait forever
        // for a gap that will never be filled.
        return Results.Json(WatermarkOf(stored.Entries), JsonOptions);
    }

    private static IResult GetLogWatermark(HttpContext context, ClientLogStore logs)
    {
        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        return Results.Json(WatermarkOf(logs.Get(fingerprint)?.Entries ?? []), JsonOptions);
    }

    // Entries arrive from ClientLogStore already in its own (Timestamp,
    // EventId) order, which is the order the client resumes from - so the
    // newest is simply the last.
    private static LogWatermarkDto WatermarkOf(IReadOnlyList<LogEntryDto> entries) =>
        entries.Count == 0
            ? new LogWatermarkDto(null, null)
            : DeviceLogArchive.Watermark(entries[^1]);

    private static async Task<IResult> ReportPlays(
        HttpContext context, PlayReportService plays, ILogger logger)
    {
        using var reader = new StreamReader(context.Request.Body);
        var report = JsonSerializer.Deserialize<PlayReportDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (report == null)
            return Results.BadRequest();

        var applied = plays.Apply(report, DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Applied {AppliedCount} of {ReportedCount} play event(s) reported by {Fingerprint}",
            applied, report.Plays.Count, context.Items[AuthenticatedFingerprintKey]);

        return Results.NoContent();
    }

    // The durable-device half of the route above - see PlayCountReportDto for
    // why one reports events and the other totals, and Library's
    // MergeReportedPlayCounts for what happens to them here.
    //
    // Filed under the fingerprint the signature proved, exactly as ReportLog
    // does and for the same reason: on a route every paired device can call,
    // the body cannot be allowed to name whose count this is.
    // What a paired device has played, starred and configured of this server's
    // tracks. See TrackStateDto for why it is stated as values rather than as
    // events, and Library.MergeReportedTrackState for what each field is
    // allowed to do when it lands.
    //
    // The fingerprint comes from context.Items, never from the body: this is a
    // route every paired device may call, so the body is attacker-controlled,
    // and a device that could name its own reporter could write another
    // device's tally. Same rule ReportLog next door follows.
    //
    // Adminness is read here rather than enforced by the group filter, because
    // unlike /api/admin this route is not an admin route - it is a route with
    // an admin *part*. A housemate's phone reporting its own plays is the
    // system working; the same phone restarring the owner's library is not. So
    // the answer is 204 either way and the flag rides into the merge, which
    // drops what the caller may not write rather than refusing the request.
    private static async Task<IResult> ReportTrackState(
        HttpContext context, Library library, TrustedPeerStore trustedPeers, ILogger logger)
    {
        using var reader = new StreamReader(context.Request.Body);
        var report = JsonSerializer.Deserialize<TrackStateReportDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (report == null)
            return Results.BadRequest();

        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        var callerIsAdmin = trustedPeers.IsAdmin(fingerprint);
        var applied = library.MergeReportedTrackState(fingerprint, report.Tracks, callerIsAdmin);

        logger.LogInformation(
            "Applied {AppliedCount} of {ReportedCount} track state report(s) from {Fingerprint} (admin: {IsAdmin})",
            applied, report.Tracks.Count, fingerprint, callerIsAdmin);

        // Read after the merge, so it is the token the caller's own report
        // produced - see TrackStateReportHeaders for the loop this closes.
        context.Response.Headers[TrackStateReportHeaders.LibraryToken] = library.ChangeToken;

        return Results.NoContent();
    }
}

// The manifest body cache behind GET /library, a DI singleton rather than
// statics on the endpoint class: the cached JSON belongs to one Library's
// current state, and a static would outlive (and be shared between) the
// several hosts a test run boots in one process.
public sealed class LibraryManifestCache
{
    private readonly object _lock = new();
    private string? _token;
    private string? _json;

    public string Get(string token, Func<string> build)
    {
        lock (_lock)
        {
            if (_token != token || _json == null)
            {
                _json = build();
                _token = token;
            }
            return _json;
        }
    }
}
