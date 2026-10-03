using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// Flower's own device-to-device sync protocol (/api/flower/v1/*), which a
// paired client bulk-syncs through. Since the OpenSubsonic adapter was deleted
// (docs/SYNC-PLAN.md) it is the only client protocol this server speaks.
//
// This server answered none of it until now, so a client that paired with it
// then failed its very first sync on a flat 404 from GET /library: a paired
// client pulls its whole catalog from its server through this endpoint (see
// LibraryDtoMapper and LibraryContracts for why the bulk shape exists alongside
// the per-album one).
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
//   GET  /stream          - a track's bytes, ranged
//   GET  /download        - the same bytes, as a named file
//
// Not here, and not accidentally omitted: pair-request (this server pairs by
// code instead - see PairingEndpoints) and unpair-notify (nothing server-side
// initiates a revoke that way; the admin API revokes directly, and the client
// finds out from the 403 its next request gets).
public static class SyncEndpoints
{
    // The budgets for this group are RequestGate's, charged per device once a
    // request has verified (docs/TRUST-BOUNDARY-PLAN.md step 2). Three planes,
    // and the split is the point rather than the ceilings:
    //
    //   - Bulk, sixty a minute: a handful of large requests per sync session.
    //     Sixty rather than the twenty this started at, because twenty turned
    //     out to be about four sessions, and a client opens one at launch, on
    //     every change to the library token and on Sync Now - a phone being
    //     backgrounded a few times locked itself out of its own server. The
    //     client also backs off for a full window when refused (see
    //     PeerSyncCoordinator.NoteThrottling).
    //   - Art, six hundred: one request per album tile when a batch is not
    //     available. Charged to bulk, the art throttled the sync - the 429
    //     landed on GET /library.
    //   - Media, two hundred and forty: a probe, a body GET and a reopen or
    //     two per track, two tracks in flight with decode-ahead. Charged to
    //     bulk, playing an album spent the sync budget several times over.
    //
    // Keyed by source address until step 2, which meant every listener behind
    // one proxy shared each of them.

    // Composed from the same two pieces the route is mapped from, so renaming
    // it can't silently drop cover art back onto the bulk plane - the filter sees
    // a whole path, MapGet sees a suffix, and they cannot disagree.
    private const string GroupPrefix = "/api/flower/v1";
    private const string CoverArtRoute = "/cover-art";
    private const string CoverArtPath = GroupPrefix + CoverArtRoute;
    private const string CoverArtBatchRoute = "/cover-art/batch";
    private const string CoverArtBatchPath = GroupPrefix + CoverArtBatchRoute;
    private const string StreamRoute = "/stream";
    private const string StreamPath = GroupPrefix + StreamRoute;
    private const string DownloadRoute = "/download";
    private const string DownloadPath = GroupPrefix + DownloadRoute;

    // What each route may be sent, so a rejection is a 413 before the body is
    // buffered rather than a read that runs to the process-wide ceiling first.
    // Set from what a 16,000-track library actually produces (measured by
    // serializing the real contracts, 2026-10-02), with a margin:
    //
    //   - /playlists/apply and /track-state keep the process-wide 20 MB
    //     (Program.cs). A full track-state restatement is 5.3 MB at that size
    //     with every owner field set, so a tighter cap would refuse a large
    //     library's honest report, and every session after it.
    //   - /log/report is 4 MB, twice what a client puts in one report (see
    //     LibrarySyncService.NextLogReport).
    //   - Everything else is 256 KB: 500 play events are 82 KB, a full
    //     Continue Playing exchange 93 KB, a cover-art batch a few hundred
    //     bytes, and the GETs carry nothing.
    private const long MaxBodyBytes = 20 * 1024 * 1024;
    private const long MaxLogReportBytes = 4 * 1024 * 1024;
    private const long MaxSmallBodyBytes = 256 * 1024;

    private static long MaxBodyFor(PathString path)
    {
        if (path.Equals(GroupPrefix + "/playlists/apply", StringComparison.OrdinalIgnoreCase)
            || path.Equals(GroupPrefix + "/track-state", StringComparison.OrdinalIgnoreCase))
        {
            return MaxBodyBytes;
        }

        return path.Equals(GroupPrefix + "/log/report", StringComparison.OrdinalIgnoreCase)
            ? MaxLogReportBytes
            : MaxSmallBodyBytes;
    }

    // The wire format is whatever the client's FlowerJsonContext writes:
    // PascalCase (no naming policy) with nulls omitted. Reflection-based here
    // Reflection-based because this host is neither trimmed nor AOT-compiled.
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
            var gate = services.GetRequiredService<RequestGate>();
            var now = DateTimeOffset.UtcNow;

            // A stream ticket, for the media routes and nothing else. The
            // browser head signs every other request in this group with a
            // WebCrypto key (BrowserPeerCredentials), but the thing that opens
            // a stream URL is an <audio> element, which presents no headers of
            // its own - so the tab signs a request for a ticket and hands the
            // element a URL carrying it. See StreamTicketService.
            //
            // Tried before the signature so a ticketed request never reaches
            // the signature path at all, and scoped by IsMedia so a ticket
            // cannot be spent on the catalog, the playlists or the log. A
            // media GET carries no body, so there is nothing for a ticket to
            // leave unsigned. Charged to the device that minted the ticket.
            if (IsMedia(http.Request.Path)
                && !RequestGate.HasUnstatedBody(http)
                && services.GetRequiredService<StreamTicketService>().TryRedeem(
                    http.Request.Query["ticket"].ToString(), http.Request.Query["id"].ToString(), now,
                    out var minter))
            {
                if (!gate.ChargeDevice(RequestGate.Plane.Media, minter, now))
                    return RateLimitResponse.TooManyRequests(http);

                // For the stream's log line: who the ticket was minted for,
                // which is the one identity a ticketed request can truthfully
                // be given - it carries no signature of its own, so any
                // fingerprint header on it is a claim nothing checked.
                http.Items[TicketMinterKey] = minter;
                return await next(context);
            }

            // A signature, and only a signature. The browser head pulls its
            // whole library through GET /library below and used to be admitted
            // here on an admin-session bearer token instead, because
            // .NET-for-WebAssembly cannot sign - it signs with a WebCrypto key
            // now like everything else (see BrowserPeerCredentials).
            var admitted = await gate.AdmitAsync(http, PlaneFor(http.Request.Path), MaxBodyFor(http.Request.Path), logger);

            // Refused as a problem document whose code says why - see
            // FlowerProblem. An unknown device is device-unknown, a 401 like
            // every other authentication failure; it was this group's 403, and
            // a client unpaired on that status alone, which a stale timestamp
            // could never trigger but anything on the path answering 403
            // could. The code is what a client reads now, and step 4 makes
            // the server sign the one refusal a client may unpair on.
            if (admitted.Outcome != RequestGate.Outcome.Admitted)
                return Problems.ForGate(admitted, http);

            // Who the gate actually let through, for the handlers below to
            // attribute a write to. Not the same as the request's own
            // X-Flower-Fingerprint header, which is a claim rather than a
            // finding: this is the fingerprint whose signature actually
            // verified.
            context.HttpContext.Items[AuthenticatedFingerprintKey] = admitted.Fingerprint;

            return await next(context);
        });

        sync.MapGet("/library", GetLibrary);
        sync.MapGet("/playlists", GetPlaylists);
        // Lambdas rather than method-group references purely so the logger
        // captured above reaches these three: a bare, non-generic ILogger is
        // not something the container can resolve as a handler parameter.
        sync.MapPost("/playlists/apply",
            (HttpContext context, Library library, TrustedPeerStore trustedPeers) =>
                ApplyPlaylists(context, library, trustedPeers, logger));
        sync.MapPost("/plays",
            (HttpContext context, PlayReportService plays, TrustedPeerStore trustedPeers) =>
                ReportPlays(context, plays, trustedPeers, logger));
        sync.MapPost("/track-state",
            (HttpContext context, Library library, TrustedPeerStore trustedPeers) =>
                ReportTrackState(context, library, trustedPeers, logger));
        sync.MapPost("/album-progress",
            (HttpContext context, AlbumProgressLedger ledger, TrustedPeerStore trustedPeers) =>
                ExchangeAlbumProgress(context, ledger, trustedPeers, logger));
        sync.MapPost("/log/report",
            (HttpContext context, ClientLogStore logs) => ReportLog(context, logs, logger));
        // What this server already holds for the caller, so a client that has
        // just started up knows where to resume from instead of re-offering
        // its whole retained week. Only needed once per session - every
        // /log/report answers with the same shape.
        sync.MapGet("/log/watermark", GetLogWatermark);

        // Album art behind this group's gate. Unlike
        // playback, art needs no stream ticket to get through it, because
        // AlbumArtLoader fetches it with an HttpClient that can send the header
        // (an <audio> element is what cannot). Deliberately the existing
        // handler rather than a second implementation of "an album's art".
        sync.MapGet(CoverArtRoute, MediaEndpoints.GetCoverArt);

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

        // The bytes themselves (see MediaEndpoints): the door every Flower client
        // uses, and the only one a stream ticket opens.
        sync.MapGet(StreamRoute, MediaEndpoints.Stream);
        sync.MapGet(DownloadRoute, MediaEndpoints.Download);
    }

    // Both cover-art routes are the art plane - the batch one especially,
    // since it exists so art stops competing with playback, and putting it
    // back in the bulk budget would undo exactly that.
    private static RequestGate.Plane PlaneFor(PathString path)
    {
        if (path.Equals(CoverArtPath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(CoverArtBatchPath, StringComparison.OrdinalIgnoreCase))
        {
            return RequestGate.Plane.Art;
        }

        return IsMedia(path) ? RequestGate.Plane.Media : RequestGate.Plane.Bulk;
    }

    // The two routes a stream ticket may open, and the only ones. A ticket is
    // issued for one track id and authenticates nothing else on this group -
    // see StreamTicketService, and the gate above where this is applied.
    private static bool IsMedia(PathString path) =>
        path.Equals(StreamPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(DownloadPath, StringComparison.OrdinalIgnoreCase);

    // Deliberately built on MediaEndpoints.CoverArtCandidates, the same
    // "which files is this id's art in" rule the single-id route and the admin
    // replace route both use. A second answer to that question is how a batch
    // starts returning different pictures from the endpoint it is meant to
    // replace.
    private static IResult GetCoverArtBatch(HttpContext context, Library library)
    {
        var ids = ReadBatchRequest(context);
        if (ids == null)
            return Problems.BadRequest();

        var entries = new List<(string Id, byte[] Bytes)>(ids.Count);
        var total = 0;

        foreach (var id in ids)
        {
            byte[] bytes = [];
            foreach (var candidate in MediaEndpoints.CoverArtCandidates(id, library))
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

    internal const string AuthenticatedFingerprintKey = "flower.auth.fingerprint";
    internal const string TicketMinterKey = "flower.auth.ticket-minter";

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
            // Only tracks this server actually has a file for: a placeholder
            // learned from somewhere else is not this device's to advertise.
            var songs = library.Snapshot.Albums
                .SelectMany(album => album.Tracks)
                .Where(track => track.Path != null)
                // Under this server's own fingerprint, so a client merging
                // this manifest files the counts as *this device's* rather
                // than its own - see Track.RemotePlayCounts.
                .Select(track => LibraryDtoMapper.ToTrackDto(track, signingKey.Fingerprint, options.CurrentValue.LibraryPaths))
                .ToList();
            // What was removed on purpose, so a device holding its own copy of
            // one drops it too - see LibrarySyncManifestDto.Removed.
            var removed = library.RemovedTracks
                .Where(r => r.Deliberate)
                .Select(r => r.Track.Id.ToKey())
                .ToList();
            return JsonSerializer.Serialize(new LibrarySyncManifestDto(signingKey.Fingerprint, songs, removed), JsonOptions);
        });

        return Results.Text(json, "application/json");
    }

    // The caller's listener's playlists and nobody else's - see Listeners. A
    // guest device sees the playlists it made; every admin device sees the
    // owner's.
    private static IResult GetPlaylists(
        HttpContext context, Library library, DeviceSigningKey signingKey, TrustedPeerStore trustedPeers)
    {
        if (ListenerOf(context, trustedPeers) is not { } listener)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

        return Results.Text(
            JsonSerializer.Serialize(
                PlaylistSyncMapper.ToManifest(signingKey.Fingerprint, PlaylistSyncMapper.For(library.Playlists, listener)),
                JsonOptions),
            "application/json");
    }

    // Whose playlists and shelf a request reads and writes: decided from the
    // fingerprint the signature proved, never from the body. Null only when
    // the gate let nothing through by signature, which none of the routes
    // asking does.
    private static string? ListenerOf(HttpContext context, TrustedPeerStore trustedPeers) =>
        context.Items[AuthenticatedFingerprintKey] is string { Length: > 0 } fingerprint
            ? Listeners.For(fingerprint, trustedPeers.IsAdmin(fingerprint))
            : null;

    // The initiator resolved every conflict before POSTing here (see
    // PlaylistSyncService), so no second merge runs on this end - but the push
    // is not taken wholesale either: see PlaylistSyncMapper.ApplyPushedManifest
    // for what is kept, and why.
    //
    // Applied to the caller's listener alone (see Listeners): a guest device
    // pushing ids that belong to the owner gets them back in Refused, and the
    // owner's playlists are untouched.
    private static async Task<IResult> ApplyPlaylists(
        HttpContext context, Library library, TrustedPeerStore trustedPeers, ILogger logger)
    {
        if (ListenerOf(context, trustedPeers) is not { } listener)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

        using var reader = new StreamReader(context.Request.Body);
        var manifest = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (manifest == null)
            return Problems.BadRequest();

        // Persists itself, through the same PlaylistRepository the client's
        // own Library writes through.
        var result = PlaylistSyncMapper.ApplyPushedManifest(library, manifest, listener, logger);

        logger.LogInformation(
            "Applied {Count} playlist(s) pushed by {Fingerprint} for listener {Listener}",
            result.Installed.Count, context.Items[AuthenticatedFingerprintKey], listener);

        return Results.Json(new PlaylistApplyResponseDto(result.Refused), JsonOptions);
    }

    // A browser tab's plays, counted here because there is nowhere else for
    // them to be counted - see IPlayReporter. Not the peer-to-peer path: two
    // desktops exchange durable per-device totals through the library manifest
    // instead (Track.RemotePlayCounts), which is the better instrument for
    // both sides that can keep one.
    //
    // Gated like every other route in this group, on a trusted peer's
    // signature - and then split by who signed. An admin's tab is the owner
    // listening, and moves this library's own count; anyone else's plays are
    // filed under that device (see PlayReportService.Apply). Any paired device
    // used to be able to inflate the server's own counts through here, which
    // docs/TRUST-BOUNDARY-PLAN.md step 1 closed.

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
            return Problems.BadRequest();

        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

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
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

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
        HttpContext context, PlayReportService plays, TrustedPeerStore trustedPeers, ILogger logger)
    {
        if (context.Items[AuthenticatedFingerprintKey] is not string { Length: > 0 } fingerprint)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

        using var reader = new StreamReader(context.Request.Body);
        PlayReportDto? report;
        try
        {
            report = JsonSerializer.Deserialize<PlayReportDto>(
                await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        }
        catch (JsonException)
        {
            return Problems.BadRequest();
        }

        // Bounded because every event becomes a dedupe entry this server keeps
        // for hours - see PlayReportDto.MaxEvents.
        if (report?.Plays == null || !report.IsWithinLimits())
            return Problems.BadRequest();

        var applied = plays.Apply(report, fingerprint, trustedPeers.IsAdmin(fingerprint), DateTimeOffset.UtcNow);

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
            return Problems.BadRequest();

        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

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

    // The Continue Playing shelf - see AlbumProgressProtocol. Whose shelf a
    // request reads and writes is decided here from the fingerprint the
    // signature proved, never from the body: every admin device shares the
    // owner's, and any other device has one to itself, so a listener's phone
    // can neither read nor move the owner's cassette.
    private static async Task<IResult> ExchangeAlbumProgress(
        HttpContext context, AlbumProgressLedger ledger, TrustedPeerStore trustedPeers, ILogger logger)
    {
        if (context.Items[AuthenticatedFingerprintKey] is not string fingerprint || fingerprint.Length == 0)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);

        using var reader = new StreamReader(context.Request.Body);
        AlbumProgressExchangeDto? exchange;
        try
        {
            exchange = JsonSerializer.Deserialize<AlbumProgressExchangeDto>(
                await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        }
        catch (JsonException)
        {
            return Problems.BadRequest();
        }

        if (exchange?.Albums is not { } albums || albums.Count > AlbumProgressProtocol.MaxEntries
            || albums.Any(a => string.IsNullOrEmpty(a.AlbumId) || a.AlbumId.Length > 64
                || a.TrackId?.Length > 64 || !double.IsFinite(a.PositionSeconds) || a.PositionSeconds < 0))
            return Problems.BadRequest();

        var shelf = Listeners.For(fingerprint, trustedPeers.IsAdmin(fingerprint));
        var merged = ledger.Exchange(shelf, albums);

        logger.LogDebug("Exchanged album progress with {Fingerprint}: {Sent} sent, {Held} held", fingerprint, albums.Count, merged.Count);
        return Results.Json(new AlbumProgressExchangeDto(merged), JsonOptions);
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
