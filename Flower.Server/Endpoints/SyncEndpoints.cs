using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Models;
using Flower.Persistence;
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
//
// Not here, and not accidentally omitted: pair-request (this server pairs by
// code instead - see PairingEndpoints), unpair-notify (nothing server-side
// initiates a revoke that way yet; the admin API revokes directly), and
// log/report (the client's ShareLogsWithPairedServer feature targets an app
// peer's ClientLogStore, which has no equivalent here).
public static class SyncEndpoints
{
    // Matches SyncHttpServer's own Bulk category: these are a handful of large
    // requests per sync session, not a stream of small ones.
    private static readonly RateLimiter BulkLimiter = new(max: 20, TimeSpan.FromSeconds(60));

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
        var sync = app.MapGroup("/api/flower/v1").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var key = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!BulkLimiter.TryAcquire(key, DateTimeOffset.UtcNow))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

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
            var sessions = services.GetRequiredService<AdminSessionService>();
            // 403 only for a caller this server genuinely has no key on file
            // for - a client treats that as "revoked" and unpairs itself. A
            // signature that just failed to verify (commonly a stale
            // timestamp, after the caller suspended mid-request) is a 401:
            // this attempt failed, the pairing is untouched.
            //
            // A signature *or* a live admin session, because the browser head
            // pulls its whole library through GET /library below and has no key
            // to sign with - see PeerOrSessionAuth for what that widening costs.
            var auth = PeerOrSessionAuth.Authenticate(
                http.Request, body, trustedPeers, replayGuard, sessions, DateTimeOffset.UtcNow);
            if (auth.Failure == PeerAuthFailure.NotTrusted)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (auth.Failure != PeerAuthFailure.None)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            // Who the gate actually let through, for the handlers below to
            // attribute a write to. Not the same as the request's own
            // X-Flower-Fingerprint header: a browser tab sends no fingerprint
            // at all (see AdminSessionCredentials - an identity claim taken on
            // trust from an unsigned caller is worse than none), so reading the
            // header logged every tab's write as "pushed by null".
            context.HttpContext.Items[AuthenticatedFingerprintKey] = auth.Fingerprint;

            return await next(context);
        });

        sync.MapGet("/library", GetLibrary);
        sync.MapGet("/playlists", GetPlaylists);
        sync.MapPost("/playlists/apply", ApplyPlaylists);
        sync.MapPost("/plays", ReportPlays);

        // The same album art /rest/getCoverArt serves, behind this group's gate
        // instead of the Subsonic one. A browser tab holds a session token and
        // no signing key, so /rest is a door it cannot open - and unlike
        // playback, art needs no stream ticket to get through this one, because
        // AlbumArtLoader fetches it with an HttpClient that can send the header
        // (an <audio> element is what cannot). Deliberately the existing
        // handler rather than a second implementation of "an album's art".
        sync.MapGet("/cover-art", SubsonicEndpoints.GetCoverArt);
    }

    private const string AuthenticatedFingerprintKey = "flower.auth.fingerprint";

    // Conditional on Library.ChangeToken, served as the ETag, exactly as
    // SyncHttpServer does it: a client that sends back the token it already
    // holds gets a 304 and no body. Worth as much here as there - this is
    // megabytes of JSON at a real library size, rebuilt for every peer that
    // asks otherwise - so the serialized body is cached alongside the token it
    // was built from, and several peers missing the cache at once still only
    // build it once.
    private static IResult GetLibrary(
        HttpContext context, Library library, DeviceSigningKey signingKey, LibraryManifestCache cache)
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
                .Select(track => SubsonicMapper.ToChild(track, signingKey.Fingerprint))
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
    private static async Task<IResult> ApplyPlaylists(HttpContext context, Library library, ILoggerFactory loggerFactory)
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

        loggerFactory.CreateLogger(typeof(SyncEndpoints)).LogInformation(
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
    // A tab is authenticated by session token, not by signature, which is what
    // makes this route reachable from one at all - the same widening GET
    // /library already relies on (see PeerOrSessionAuth). Worth naming what
    // that means here specifically: a caller through this route can inflate
    // this server's play counts. That is a nuisance, not a disclosure, and it
    // is bounded by the same session the tab needs to see the library at all.
    private static async Task<IResult> ReportPlays(
        HttpContext context, PlayReportService plays, ILoggerFactory loggerFactory)
    {
        using var reader = new StreamReader(context.Request.Body);
        var report = JsonSerializer.Deserialize<PlayReportDto>(
            await reader.ReadToEndAsync(context.RequestAborted), JsonOptions);
        if (report == null)
            return Results.BadRequest();

        var applied = plays.Apply(report, DateTimeOffset.UtcNow);

        loggerFactory.CreateLogger(typeof(SyncEndpoints)).LogInformation(
            "Applied {AppliedCount} of {ReportedCount} play event(s) reported by {Fingerprint}",
            applied, report.Plays.Count, context.Items[AuthenticatedFingerprintKey]);

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
