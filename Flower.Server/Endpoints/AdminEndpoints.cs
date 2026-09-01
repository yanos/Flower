using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting.Server;

using Microsoft.Extensions.Options;

using Flower.Importer;
using Flower.Models;
using Flower.Logging;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt, bool GrantsAdmin, string Invite, string BrowserUrl);
public sealed record TrustedDeviceResponse(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, bool IsAdmin);
public sealed record SubsonicCredentialResponse(
    string Username, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? Password);
public sealed record CoverArtWriteResponse(int Written, int Total);
public sealed record LibraryStatusResponse(bool Rescanning, int TrackCount, DateTimeOffset? LastCompletedAt, string? LastError);
public sealed record LogEntryResponse(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception);

// A device's pushed log, plus when it arrived - the timestamp matters here in
// a way it does not for the server's own live log: these lines are as old as
// that device's last sync, and a reader who does not know that will misread a
// stale snapshot as a current one.
public sealed record DeviceLogResponse(
    string Fingerprint, string Alias, DateTimeOffset ReceivedAt, IReadOnlyList<LogEntryResponse> Entries);

// The server's own log, read as a delta: the entries after whatever sequence
// the caller last saw, plus the sequence to hand back next time. A reader
// watching the tail asks every couple of seconds, and re-sending the whole
// buffer each time to append two lines to it is the thing this avoids - see
// InMemoryLogStore.SnapshotAfter for why LastSequence is the store's own
// high-water mark rather than the last entry returned.
public sealed record LogSliceResponse(long LastSequence, IReadOnlyList<LogEntryResponse> Entries);

// The admin API: issuing pairing codes, listing and revoking devices, minting the
// per-client credentials third-party Subsonic clients need, and - for the browser
// settings page - reading and writing this server's own configuration, triggering
// a rescan and reading its log.
//
// There is no login route here, and no admin password anywhere in this project.
// Under SYNC-PLAN.md's "Passwordless by design" a device pairs by redeeming a code
// and then signs every request with its keypair, so these routes are gated by the
// same device-signature check as everything else (DeviceSignatureAuth) plus
// TrustedPeer.IsAdmin. That collapses what used to be two authentication
// mechanisms into one, and it is why AdminAuthService - bearer tokens, a login
// endpoint, a configured username and password - was deleted outright rather than
// kept as a fallback.
//
// A browser is not an exception to any of that any more. It used to be - it held
// a server-minted session token because .NET-for-WebAssembly has no asymmetric
// crypto - and that token was the last bearer credential in the project. It now
// generates a non-extractable P-256 keypair through WebCrypto, redeems a pairing
// code like any other device, and signs (see BrowserPeerCredentials, and
// docs/OPEN-INTERNET-REVIEW.md finding 7 for why a bearer token in a URL was the
// thing standing between this server and a remote transport).
public static class AdminEndpoints
{
    // The admin surface had no budget at all until docs/OPEN-INTERNET-REVIEW.md
    // went looking for one. Severity is low - every route below is gated on a
    // device signature or a live session, and an unknown fingerprint is refused
    // by a dictionary lookup before any ECDSA verification happens, so a flood
    // of unauthenticated requests is cheap to turn away. But this is the one
    // surface where a single request triggers a rescan or writes settings, and
    // "cheap to refuse" is an argument for a generous ceiling, not for none.
    //
    // Keyed by source IP, like every other pre-auth budget: the filter runs
    // before authentication, so there is no verified identity to key by yet.
    // Sized for a human driving the settings page - which opens by fetching
    // devices, credentials, settings and the log at once - rather than for a
    // poll loop, since nothing polls these routes.
    private static readonly RateLimiter RequestLimiter = new(max: 120, TimeSpan.FromSeconds(60));

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminEndpoints));

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var authenticated = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!RequestLimiter.TryAcquire(RateLimiter.KeyFor(http.Connection.RemoteIpAddress), DateTimeOffset.UtcNow))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var services = http.RequestServices;
            var trustedPeers = services.GetRequiredService<TrustedPeerStore>();
            var replayGuard = services.GetRequiredService<NonceReplayGuard>();

            // Signed requests may now carry a body (PUT /settings does), so
            // it has to be buffered before the signature - which covers a
            // hash of it - can be checked. No handler below binds the body as
            // a parameter, which is what makes this possible at all: minimal
            // APIs bind parameters *before* endpoint filters run, so a
            // body-bound parameter would have consumed the stream before this
            // could ever see it.
            byte[] body = [];
            if (http.Request.ContentLength is > 0)
            {
                if (http.Request.ContentLength > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                http.Request.EnableBuffering();
                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
                body = buffer.ToArray();
                http.Request.Body.Position = 0;
            }

            var fingerprint = DeviceSignatureAuth.VerifyTrustedPeer(
                http.Request, body, trustedPeers, replayGuard, logger);
            if (fingerprint == null)
                return Results.Unauthorized();

            // Authenticated as *a* peer is not authorized as an admin: a paired
            // phone can sign a perfectly valid request to these routes, and must
            // still be turned away.
            // StatusCode(403), not Results.Forbid(): Forbid() runs the ASP.NET
            // Core authentication stack's forbid handler, and this app registers
            // no authentication scheme at all - it authenticates by device
            // signature - so it throws rather than answering, turning every
            // "paired but not an admin" refusal into a 500.
            if (!trustedPeers.IsAdmin(fingerprint))
            {
                // Warning, and deliberately louder than a failed signature:
                // this caller *is* a paired device and its signature verified,
                // it simply is not an admin. A phone reaching for /api/admin is
                // either a bug or the most interesting thing in the log.
                logger.LogWarning(
                    "Refused {Method} {Path} for {Fingerprint} from {RemoteAddress}: "
                    + "the device is paired and verified, but is not an admin.",
                    http.Request.Method, http.Request.Path.Value, fingerprint,
                    http.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Items[AdminFingerprintKey] = fingerprint;
            return await next(context);
        });

        // grantsAdmin is set by the issuer, never claimed by the redeemer -
        // see PairingCodeService. This is the "add another admin device" path
        // as well as the ordinary "add a device" one.
        authenticated.MapPost("/pairing-codes", (
            HttpContext context, PairingCodeService pairing, DeviceSigningKey signingKey,
            IOptionsMonitor<FlowerServerOptions> options, bool grantsAdmin = false) =>
        {
            var (code, expiresAt) = pairing.GenerateCode(grantsAdmin);
            var invite = BuildInvite(context, signingKey, options.CurrentValue, code);
            // Two renderings of one code, because the two things that redeem it
            // cannot read the same thing. A Flower app scans or types the
            // flower:// invite; a browser tab needs a link it can be opened at,
            // and redeems the code from the fragment on first load (see
            // BrowserPeerCredentials). Both consume the same single-use code, so
            // whichever gets there first is the device that pairs.
            var browserUrl = WebUiHosting.BuildBrowserPairingUrl(
                WebUiHosting.BrowserOriginFor(context.Request), code);

            // Audited because of what it can become: whoever redeems this gets
            // the library, and with grantsAdmin, this server's settings. The
            // code itself is never logged - it is a live credential until it is
            // redeemed or expires, which is exactly why Program.cs prints its
            // startup code to the console instead of through the logger.
            logger.LogInformation(
                "{Fingerprint} issued a pairing code (admin: {GrantsAdmin}) expiring at {ExpiresAt}.",
                context.Items[AdminFingerprintKey], grantsAdmin, expiresAt);

            return Results.Json(
                new PairingCodeResponse(code, expiresAt, grantsAdmin, invite.ToString(), browserUrl), jsonOptions);
        });

        authenticated.MapGet("/devices", (TrustedPeerStore store) =>
        {
            var devices = store.Load()
                .Select(p => new TrustedDeviceResponse(p.Fingerprint, p.Alias, p.ApprovedAt, p.IsAdmin))
                .ToList();
            return Results.Json(devices, jsonOptions);
        });

        authenticated.MapDelete("/devices/{fingerprint}", async (
            string fingerprint, HttpContext context, TrustedPeerStore store,
            StreamTicketService tickets) =>
        {
            // Revoking the key this very request was signed with would lock the
            // caller out mid-session, and is far more likely a misclick on the
            // wrong row than a deliberate act. Removing another admin is still
            // allowed.
            if (string.Equals(context.Items[AdminFingerprintKey] as string, fingerprint, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "A device cannot revoke itself." });

            await store.RevokeAsync(fingerprint);
            // Otherwise "revoke this device" would leave its already-minted
            // stream URLs playable for the rest of their lifetime.
            tickets.RevokeFor(fingerprint);

            // The counterpart to the pairing line above: access granted and
            // access taken away should both be recoverable from the log, not
            // just inferable from a device having gone quiet.
            logger.LogInformation(
                "{Admin} revoked device {Fingerprint} and its outstanding stream tickets.",
                context.Items[AdminFingerprintKey], fingerprint);

            return Results.NoContent();
        });

        // Path B (SYNC-PLAN.md): third-party Subsonic clients can't hold a
        // keypair, so they get a generated credential from the same admin
        // surface instead - one issuer, one list, one revoke button.
        authenticated.MapPost("/subsonic-credentials", async (
            HttpContext context, SubsonicCredentialStore store, string? label) =>
        {
            var credential = await store.IssueAsync(label ?? "Subsonic client");

            // Username and label only. The password is in the response body and
            // nowhere else by design (see below), and writing it here would
            // undo that.
            logger.LogInformation(
                "{Fingerprint} issued Subsonic credential {Username} ({Label}).",
                context.Items[AdminFingerprintKey], credential.Username, credential.Label);
            // The only response that ever carries the password: it is not
            // retrievable afterwards through /subsonic-credentials below, so
            // the admin UI has to show it now or the user re-issues.
            return Results.Json(
                new SubsonicCredentialResponse(
                    credential.Username, credential.Label, credential.CreatedAt, credential.LastSeenAt, credential.Password),
                jsonOptions);
        });

        authenticated.MapGet("/subsonic-credentials", (SubsonicCredentialStore store) =>
        {
            var credentials = store.Load()
                .Select(c => new SubsonicCredentialResponse(c.Username, c.Label, c.CreatedAt, c.LastSeenAt, Password: null))
                .ToList();
            return Results.Json(credentials, jsonOptions);
        });

        authenticated.MapDelete("/subsonic-credentials/{username}", async (
            string username, HttpContext context, SubsonicCredentialStore store) =>
        {
            if (!await store.RevokeAsync(username))
                return Results.NotFound();

            logger.LogInformation(
                "{Fingerprint} revoked Subsonic credential {Username}.",
                context.Items[AdminFingerprintKey], username);

            return Results.NoContent();
        });

        authenticated.MapGet("/settings", async (
            HttpContext context, IOptionsMonitor<FlowerServerOptions> options, IServer boundServer,
            PublicAddressProbe publicAddress) =>
            Results.Json(
                await DescribeAsync(options.CurrentValue, boundServer, publicAddress, context.RequestAborted),
                jsonOptions));

        // Read from the raw (buffered, rewound) stream rather than a bound
        // parameter - see the filter above for why no route here may bind a body.
        authenticated.MapPut("/settings", async (
            HttpContext context, IOptionsMonitor<FlowerServerOptions> options, IConfiguration configuration,
            LibraryRescanCoordinator rescans, IServer boundServer, PublicAddressProbe publicAddress) =>
        {
            ServerSettingsUpdateDto? update;
            try
            {
                update = await JsonSerializer.DeserializeAsync<ServerSettingsUpdateDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (update == null)
                return Results.BadRequest(new { error = "A settings body is required." });

            var before = options.CurrentValue;
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var restartRequired = new List<string>();

            // Applied over a copy rather than re-read afterwards. Re-reading looks
            // more honest but is not: flower-server.json is watched with
            // reloadOnChange, and that watcher is debounced, so CurrentValue right
            // after the write is still the *old* value more often than not - the
            // page would show the change reverting and then quietly reappearing.
            var after = new FlowerServerOptions
            {
                DataDirectory = before.DataDirectory,
                WebUiPath = before.WebUiPath,
                Alias = before.Alias,
                AdvertisedHost = before.AdvertisedHost,
                AdvertiseOnLan = before.AdvertiseOnLan,
                TrustTailscaleRange = before.TrustTailscaleRange,
                AllowPublicAccess = before.AllowPublicAccess,
                AllowedCidrs = [.. before.AllowedCidrs],
                LibraryPaths = [.. before.LibraryPaths],
                IntegrateWithITunes = before.IntegrateWithITunes,
                SyncPlayCountFromITunes = before.SyncPlayCountFromITunes,
                SyncDateAddedFromITunes = before.SyncDateAddedFromITunes,
            };

            // Compared against the *resolved* name, not the configured one: an
            // unset Alias reads as the machine name everywhere it is used, and
            // that is what the page was shown (see Describe). Comparing against
            // the raw empty string would make simply opening the settings page
            // and pressing OK write the machine name into flower-server.json and
            // announce a restart to apply a change nobody made.
            if (update.Alias is { } alias && alias.Trim() != MdnsAdvertiser.InstanceName(before))
            {
                after.Alias = alias.Trim();
                values[nameof(FlowerServerOptions.Alias)] = JsonValue.Create(after.Alias);
                restartRequired.Add(nameof(FlowerServerOptions.Alias));
            }
            if (update.AdvertisedHost is { } advertisedHost && advertisedHost.Trim() != before.AdvertisedHost)
            {
                after.AdvertisedHost = advertisedHost.Trim();
                values[nameof(FlowerServerOptions.AdvertisedHost)] = JsonValue.Create(after.AdvertisedHost);
            }
            if (update.AdvertiseOnLan is { } advertiseOnLan && advertiseOnLan != before.AdvertiseOnLan)
            {
                after.AdvertiseOnLan = advertiseOnLan;
                values[nameof(FlowerServerOptions.AdvertiseOnLan)] = JsonValue.Create(advertiseOnLan);
                restartRequired.Add(nameof(FlowerServerOptions.AdvertiseOnLan));
            }
            if (update.TrustTailscaleRange is { } trustTailscale && trustTailscale != before.TrustTailscaleRange)
            {
                after.TrustTailscaleRange = trustTailscale;
                values[nameof(FlowerServerOptions.TrustTailscaleRange)] = JsonValue.Create(trustTailscale);
            }
            // No restart entry: the gate in Program.cs reads this per request
            // through IOptionsMonitor, so it is shut - or opened - by the time
            // this response is written.
            if (update.AllowPublicAccess is { } allowPublic && allowPublic != before.AllowPublicAccess)
            {
                after.AllowPublicAccess = allowPublic;
                values[nameof(FlowerServerOptions.AllowPublicAccess)] = JsonValue.Create(allowPublic);
            }
            if (update.AllowedCidrs is { } allowedCidrs && !Same(allowedCidrs, before.AllowedCidrs))
            {
                after.AllowedCidrs = Normalize(allowedCidrs);
                values[nameof(FlowerServerOptions.AllowedCidrs)] = ToJsonArray(after.AllowedCidrs);
            }
            if (update.LibraryPaths is { } libraryPaths && !Same(libraryPaths, before.LibraryPaths))
            {
                after.LibraryPaths = Normalize(libraryPaths);
                values[nameof(FlowerServerOptions.LibraryPaths)] = ToJsonArray(after.LibraryPaths);
            }

            if (update.IntegrateWithITunes is { } integrate && integrate != before.IntegrateWithITunes)
            {
                after.IntegrateWithITunes = integrate;
                values[nameof(FlowerServerOptions.IntegrateWithITunes)] = JsonValue.Create(integrate);
            }
            if (update.SyncPlayCountFromITunes is { } syncPlayCount && syncPlayCount != before.SyncPlayCountFromITunes)
            {
                after.SyncPlayCountFromITunes = syncPlayCount;
                values[nameof(FlowerServerOptions.SyncPlayCountFromITunes)] = JsonValue.Create(syncPlayCount);
            }
            if (update.SyncDateAddedFromITunes is { } syncDateAdded && syncDateAdded != before.SyncDateAddedFromITunes)
            {
                after.SyncDateAddedFromITunes = syncDateAdded;
                values[nameof(FlowerServerOptions.SyncDateAddedFromITunes)] = JsonValue.Create(syncDateAdded);
            }

            if (values.Count > 0)
            {
                await ServerSettingsWriter.WriteAsync(before.DataDirectory, values, context.RequestAborted);

                // Reloaded here rather than left to the file watcher: that watcher
                // is debounced, and the very next thing to happen is a rescan that
                // has to see these values - the folder that was just added, the
                // iTunes switch that was just turned on. Without this the scan
                // reads the previous configuration and appears to have ignored the
                // change, which is exactly what the page just promised it did.
                (configuration as IConfigurationRoot)?.Reload();

                logger.LogInformation(
                    "{Fingerprint} updated server settings: {Keys}",
                    context.Items[AdminFingerprintKey], string.Join(", ", values.Keys));
            }

            // Turning an iTunes switch on has no visible effect until something
            // scans - and unlike a library folder, which the page follows with its
            // own rescan call, nothing else here would ever trigger one. Started
            // from this side rather than asked of the caller because the caller
            // cannot tell that these three settings need it: TryStart is a no-op
            // while a scan is already running, so the page's own rescan after a
            // folder change does not turn into two.
            if (after.IntegrateWithITunes &&
                (values.ContainsKey(nameof(FlowerServerOptions.IntegrateWithITunes)) ||
                 values.ContainsKey(nameof(FlowerServerOptions.SyncPlayCountFromITunes)) ||
                 values.ContainsKey(nameof(FlowerServerOptions.SyncDateAddedFromITunes))))
            {
                rescans.TryStart();
            }

            var described = await DescribeAsync(after, boundServer, publicAddress, context.RequestAborted);
            return Results.Json(described with { RestartRequired = restartRequired }, jsonOptions);
        });

        authenticated.MapGet("/library", (LibraryRescanCoordinator rescans) =>
            Results.Json(
                new LibraryStatusResponse(rescans.IsRunning, rescans.TrackCount, rescans.LastCompletedAt, rescans.LastError),
                jsonOptions));

        // Answered as soon as the scan is *started*, not when it finishes - a
        // full scan of a NAS share outlasts any sensible request timeout. The
        // page polls GET /library above for the rest.
        authenticated.MapPost("/library/rescan", (LibraryRescanCoordinator rescans) =>
        {
            rescans.TryStart();
            return Results.Json(
                new LibraryStatusResponse(rescans.IsRunning, rescans.TrackCount, rescans.LastCompletedAt, rescans.LastError),
                jsonOptions);
        });

        // Album art, written into the server's own files.
        //
        // This is the admin surface's one *content* write, and it is here rather
        // than on /rest because it is an owner's act, not a listener's: the
        // Subsonic protocol has no route for replacing cover art, and inventing
        // one there would put a whole-file rewrite behind the same credential a
        // third-party player uses to browse. TrustedPeer.IsAdmin is the right
        // gate for it.
        //
        // Addressed by the same id GET /rest/getCoverArt reads at - an album id
        // or a song id - and it writes into exactly the files that read would
        // have consulted (SubsonicEndpoints.CoverArtCandidates). That symmetry
        // is the whole point: art is addressed per album on the way out (see
        // SubsonicMapper's CoverArt field), so writing it into one track of an
        // album would leave the album still serving whichever other file the
        // read path happened to reach first, and look to the caller like the
        // change had been silently dropped.
        authenticated.MapPut("/cover-art", async (HttpContext context, Library library, string? id) =>
        {
            if (string.IsNullOrEmpty(id))
                return Results.BadRequest(new { error = "An album or song id is required." });

            var contentType = context.Request.ContentType?.Split(';')[0].Trim();

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
                return Results.BadRequest(new { error = "An image body is required." });

            // Sniffed rather than trusted, and the request is refused when the
            // two disagree with each other about nothing recognisable: the MIME
            // type ends up inside the tag, where every later reader believes it,
            // so an "image/jpeg" header over a zip file would poison the file
            // rather than fail here.
            var sniffed = LocalAlbumArtReader.MimeTypeForBytes(bytes);
            if (sniffed == null)
                return Results.BadRequest(new { error = "That body is not an image Flower can read." });

            var mimeType = sniffed;
            if (contentType != null && !string.Equals(contentType, sniffed, StringComparison.OrdinalIgnoreCase))
                logger.LogDebug("Cover art for {Id} arrived as {Declared} but is really {Actual}; using the latter.",
                    id, contentType, sniffed);

            return WriteCoverArt(id, library, logger, jsonOptions, path => AlbumArtWriter.TryWrite(path, bytes, mimeType, logger));
        });

        // Removing art is a write like any other, and it is deliberately not a
        // PUT with an empty body: "replace this with nothing" and "there is
        // nothing here to send" are too easy to confuse when a request is
        // truncated in flight.
        authenticated.MapDelete("/cover-art", (Library library, string? id) =>
            string.IsNullOrEmpty(id)
                ? Results.BadRequest(new { error = "An album or song id is required." })
                : WriteCoverArt(id, library, logger, jsonOptions, path => AlbumArtWriter.TryRemove(path, logger)));

        // One paired device's rolling seven-day log, assembled from the
        // snapshots pushed at the end of its syncs (see SyncEndpoints'
        // /log/report and ClientLogStore). The whole
        // reason this exists: the owner of the server is the one who ends up
        // diagnosing a listener's phone, and the listener cannot be talked
        // through finding a log file.
        //
        // 404 rather than an empty list for a device that has not pushed yet -
        // "nothing has arrived from this device" and "this device logged
        // nothing" are different answers, and only the first one is worth
        // telling the reader to wait about.
        authenticated.MapGet("/devices/{fingerprint}/logs", (string fingerprint, int? limit, ClientLogStore logs) =>
        {
            if (logs.Get(fingerprint) is not { } snapshot)
                return Results.NotFound();

            var take = Math.Clamp(limit ?? 500, 1, snapshot.Entries.Count == 0 ? 1 : snapshot.Entries.Count);
            var lines = snapshot.Entries
                .Skip(Math.Max(0, snapshot.Entries.Count - take))
                .Select(e => new LogEntryResponse(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception))
                .ToList();
            return Results.Json(new DeviceLogResponse(snapshot.Fingerprint, snapshot.Alias, snapshot.ReceivedAt, lines), jsonOptions);
        });

        // This server's own log, from the same in-memory buffer the app's Log
        // window reads (AppLogging.Initialize wires the sink in Program.cs), so a
        // headless box can be diagnosed from a browser instead of by SSHing in to
        // tail a file.
        // after is the caller's cursor: omit it (or pass BeforeFirstSequence) for
        // the whole buffer, hand back the LastSequence of the previous response
        // to get only what has been logged since.
        authenticated.MapGet("/logs", (int? limit, long? after) =>
        {
            var slice = InMemoryLogStore.Instance.SnapshotAfter(after ?? InMemoryLogStore.BeforeFirstSequence);
            var take = Math.Max(1, limit ?? 500);
            var lines = slice.Entries
                .Skip(Math.Max(0, slice.Entries.Count - take))
                .Select(e => new LogEntryResponse(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception))
                .ToList();
            return Results.Json(new LogSliceResponse(slice.LastSequence, lines), jsonOptions);
        });
    }

    // Applies one art write to every file behind an id, and reports how many it
    // landed on. A partial success is still a 200 with a smaller count rather
    // than an error: the files that took the new picture really do have it, and
    // telling the caller "failed" would invite it to retry a write that has
    // already half happened.
    private static IResult WriteCoverArt(
        string id, Library library, ILogger logger, JsonSerializerOptions jsonOptions, Func<string, bool> write)
    {
        var candidates = SubsonicEndpoints.CoverArtCandidates(id, library);
        if (candidates.Count == 0)
            return Results.NotFound(new { error = "No track on this server has that id." });

        var written = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Path is { Length: > 0 } path && write(path))
                written++;
        }

        if (written == 0)
            return Results.Json(new { error = "The artwork could not be written to any of those files." },
                jsonOptions, statusCode: StatusCodes.Status500InternalServerError);

        logger.LogInformation("Album art for {Id} rewritten on {Written} of {Total} files.",
            id, written, candidates.Count);
        return Results.Json(new CoverArtWriteResponse(written, candidates.Count), jsonOptions);
    }

    internal const string AdminFingerprintKey = "Flower.AdminFingerprint";

    // Matches the process-wide Kestrel ceiling (see Program.cs). Nothing here is
    // remotely near it - a settings body is a few hundred bytes - but a signed
    // route has to buffer whatever arrives before it can verify it, so it needs a
    // stated limit rather than an implicit one.
    private const long MaxBodyBytes = 20 * 1024 * 1024;

    private static List<string> Normalize(IReadOnlyList<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    private static bool Same(IReadOnlyList<string> updated, IReadOnlyList<string> current) =>
        Normalize(updated).SequenceEqual(current, StringComparer.Ordinal);

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(JsonValue.Create(value));
        return array;
    }

    // The operator-editable half of FlowerServerOptions, as the shared wire
    // shape - DataDirectory, Version, Addresses and the public address ride
    // along read-only, and
    // WebUiPath deliberately does not appear at all: it is part of how the
    // server was deployed, not something the page served from it should move
    // out from under itself.
    //
    // The alias is reported resolved rather than as configured. Unset, it means
    // the machine name - that is what mDNS announces and what every client's
    // sidebar shows - and a settings page that answers "what is this server
    // called" with an empty box is wrong about a name that plainly exists.
    private static async Task<ServerSettingsDto> DescribeAsync(
        FlowerServerOptions options, IServer boundServer, PublicAddressProbe publicAddress, CancellationToken ct) =>
        new(MdnsAdvertiser.InstanceName(options),
            options.AdvertisedHost,
            options.AdvertiseOnLan,
            options.TrustTailscaleRange,
            options.AllowedCidrs.ToList(),
            options.LibraryPaths.ToList(),
            options.IntegrateWithITunes,
            options.SyncPlayCountFromITunes,
            options.SyncDateAddedFromITunes,
            Flower.Importer.Importer.TryResolveAppleMusicFolder(),
            ITunesIntegration.DescribeSource(),
            options.DataDirectory,
            AppVersion.Display,
            options.AllowPublicAccess,
            DiscoveryEndpoints.ReachableOrigins(boundServer, options),
            await publicAddress.GetAsync(ct));

    // The host in the invite is the address the admin's own browser reached
    // this server on, not a configured one: on a box with a LAN address, a
    // tailnet address and a container-internal address, that is the only one
    // known to actually work from outside. AdvertisedHost overrides it for the
    // reverse-proxy case, where the request's host is the proxy's idea of it.
    private static PairingInvite BuildInvite(
        HttpContext context, DeviceSigningKey signingKey, FlowerServerOptions options, string code)
    {
        var host = string.IsNullOrWhiteSpace(options.AdvertisedHost)
            ? context.Request.Host.Value ?? "localhost"
            : options.AdvertisedHost;

        return new PairingInvite(host, code, signingKey.Fingerprint);
    }
}
