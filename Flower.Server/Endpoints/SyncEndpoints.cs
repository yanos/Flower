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
            if (DeviceSignatureAuth.VerifyTrustedPeer(http.Request, body, trustedPeers, replayGuard) == null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return await next(context);
        });

        sync.MapGet("/library", GetLibrary);
        sync.MapGet("/playlists", GetPlaylists);
        sync.MapPost("/playlists/apply", ApplyPlaylists);
    }

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
                .Select(SubsonicMapper.ToChild)
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
            playlists.Count, DeviceSignatureAuth.GetIdentityValue(context.Request, "X-Flower-Fingerprint"));

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
