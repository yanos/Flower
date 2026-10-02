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
public sealed record TrustedDeviceResponse(
    string Fingerprint, string Alias, DateTimeOffset ApprovedAt, bool IsAdmin, DateTimeOffset? LastSeenAt, bool HasLog);
public sealed record CoverArtWriteResponse(int Written, int Total);
// UnwritableFolders is the library folders the last scan could not write to -
// see LibraryWriteAccess. Empty on a server that can write to all of them.
public sealed record LibraryStatusResponse(
    bool Rescanning, int TrackCount, DateTimeOffset? LastCompletedAt, string? LastError,
    IReadOnlyList<string> UnwritableFolders);
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

// The admin API: issuing pairing codes, listing and revoking devices, and - for
// the browser settings page - reading and writing this server's own
// configuration, triggering a rescan and reading its log.
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
    // Budgets are RequestGate's, charged per device once a request verifies
    // (docs/TRUST-BOUNDARY-PLAN.md step 2): the admin plane at 120 a minute,
    // sized for a person driving the settings page - which opens by fetching
    // devices, settings and the log at once - and the upload plane at 3000.
    //
    // Uploads have a budget of their own for the reason cover art and
    // playback each have one on the sync surface: a device sending an album is
    // two requests a song, back to back, and charged to the admin budget it
    // would lock the owner out of the settings page for as long as the album
    // took. Wide, because the traffic is legitimate and fast on a LAN, and
    // because what makes an upload expensive to receive is closed off before
    // the budget is even relevant: see the filter, which will not buffer a
    // body for a caller that has not at least named an admin device.
    //
    // The admin surface had no budget at all until docs/OPEN-INTERNET-REVIEW.md
    // went looking for one, and then it was keyed by source address.

    private const string UploadsRoute = "/library/uploads";
    private const string UploadsPath = "/api/admin" + UploadsRoute;
    private const string MoveRoute = "/library/move";
    private const string MovePath = "/api/admin" + MoveRoute;
    private const string TagsRoute = "/library/tags";
    private const string TagsPath = "/api/admin" + TagsRoute;
    private const string ArtworkRoute = "/library/artwork";
    private const string ArtworkPath = "/api/admin" + ArtworkRoute;

    // What a device keeping this server's files in step with its own sends:
    // files, and - one request a song - the news that a file has moved. A
    // renamed album folder is as many of those as it has tracks, which is why
    // moves are charged here and not to the settings page's budget.
    private static bool IsUpload(PathString path) =>
        path.StartsWithSegments(UploadsPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(MovePath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(TagsPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(ArtworkPath, StringComparison.OrdinalIgnoreCase);

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
            var isUpload = IsUpload(http.Request.Path);
            var services = http.RequestServices;
            var trustedPeers = services.GetRequiredService<TrustedPeerStore>();

            // An upload is the one request here whose body is megabytes by
            // design, and the gate buffers a known device's body before it
            // checks the signature. So before that: is the fingerprint this
            // request claims an admin's? It proves nothing - a claim is a
            // header - but it is a lookup rather than a read, and it means the
            // only callers this server will hold eight megabytes for are ones
            // that know which devices administer it. The signature still
            // decides. A fingerprint with no key on file is the gate's to
            // refuse, as a stranger.
            if (isUpload
                && DeviceSignatureAuth.GetIdentityValue(http.Request, "X-Flower-Fingerprint") is { Length: > 0 } claimed
                && trustedPeers.GetPublicKey(claimed) != null
                && !trustedPeers.IsAdmin(claimed))
            {
                return Problems.NotAdmin();
            }

            // Signed requests may carry a body (PUT /settings does), and the
            // gate buffers it so the signature - which covers a hash of it -
            // can be checked. No handler below binds the body as a parameter,
            // which is what makes this possible at all: minimal APIs bind
            // parameters *before* endpoint filters run, so a body-bound
            // parameter would have consumed the stream before this could ever
            // see it.
            var admitted = await services.GetRequiredService<RequestGate>().AdmitAsync(
                http, isUpload ? RequestGate.Plane.Upload : RequestGate.Plane.Admin, MaxBodyBytes, logger);
            if (admitted.Outcome != RequestGate.Outcome.Admitted)
                return Problems.ForGate(admitted, http);

            var fingerprint = admitted.Fingerprint!;

            // Authenticated as *a* peer is not authorized as an admin: a paired
            // phone can sign a perfectly valid request to these routes, and must
            // still be turned away.
            // A 403 written here, not Results.Forbid(): Forbid() runs the ASP.NET
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
                return Problems.NotAdmin();
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

        authenticated.MapGet("/devices", (TrustedPeerStore store, ClientLogStore logs) =>
        {
            var devices = store.Load()
                .Select(p => new TrustedDeviceResponse(
                    p.Fingerprint, p.Alias, p.ApprovedAt, p.IsAdmin, p.LastSeenAt, logs.Has(p.Fingerprint)))
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
                return Problems.BadRequest("A device cannot revoke itself.");

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

        authenticated.MapGet("/settings", async (
            HttpContext context, IOptionsMonitor<FlowerServerOptions> options, IServer boundServer,
            PublicAddressProbe publicAddress, PublicReachability publicReachability, DeviceSigningKey signingKey,
            MdnsAdvertiser advertiser, IConfiguration configuration) =>
            Results.Json(
                await DescribeAsync(options.CurrentValue, boundServer, publicAddress, publicReachability, advertiser, configuration, signingKey, context.RequestAborted),
                jsonOptions));

        // Read from the raw (buffered, rewound) stream rather than a bound
        // parameter - see the filter above for why no route here may bind a body.
        authenticated.MapPut("/settings", async (
            HttpContext context, IOptionsMonitor<FlowerServerOptions> options, IConfiguration configuration,
            LibraryRescanCoordinator rescans, IServer boundServer, PublicAddressProbe publicAddress,
            PublicReachability publicReachability, DeviceSigningKey signingKey, MdnsAdvertiser advertiser) =>
        {
            ServerSettingsUpdateDto? update;
            try
            {
                update = await JsonSerializer.DeserializeAsync<ServerSettingsUpdateDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Problems.BadRequest(ex.Message);
            }

            if (update == null)
                return Problems.BadRequest("A settings body is required.");

            var before = options.CurrentValue;
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

            // A setting something above flower-server.json sets is left alone:
            // the write would be outranked the moment it landed, and the page
            // greys the field out for that reason (SettingsOverrides).
            var locked = SettingsOverrides.Describe(configuration);
            bool Editable(string option) => !locked.ContainsKey(option);

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
            if (update.Alias is { } alias && alias.Trim() != MdnsAdvertiser.InstanceName(before) && Editable(nameof(FlowerServerOptions.Alias)))
            {
                after.Alias = alias.Trim();
                values[nameof(FlowerServerOptions.Alias)] = JsonValue.Create(after.Alias);
            }
            if (update.AdvertisedHost is { } advertisedHost && advertisedHost.Trim() != before.AdvertisedHost && Editable(nameof(FlowerServerOptions.AdvertisedHost)))
            {
                after.AdvertisedHost = advertisedHost.Trim();
                values[nameof(FlowerServerOptions.AdvertisedHost)] = JsonValue.Create(after.AdvertisedHost);
            }
            if (update.AdvertiseOnLan is { } advertiseOnLan && advertiseOnLan != before.AdvertiseOnLan && Editable(nameof(FlowerServerOptions.AdvertiseOnLan)))
            {
                after.AdvertiseOnLan = advertiseOnLan;
                values[nameof(FlowerServerOptions.AdvertiseOnLan)] = JsonValue.Create(advertiseOnLan);
            }
            if (update.TrustTailscaleRange is { } trustTailscale && trustTailscale != before.TrustTailscaleRange && Editable(nameof(FlowerServerOptions.TrustTailscaleRange)))
            {
                after.TrustTailscaleRange = trustTailscale;
                values[nameof(FlowerServerOptions.TrustTailscaleRange)] = JsonValue.Create(trustTailscale);
            }
            // No restart entry: the gate in Program.cs reads this per request
            // through IOptionsMonitor, so it is shut - or opened - by the time
            // this response is written.
            if (update.AllowPublicAccess is { } allowPublic && allowPublic != before.AllowPublicAccess && Editable(nameof(FlowerServerOptions.AllowPublicAccess)))
            {
                after.AllowPublicAccess = allowPublic;
                values[nameof(FlowerServerOptions.AllowPublicAccess)] = JsonValue.Create(allowPublic);
            }
            if (update.AllowedCidrs is { } allowedCidrs && !Same(allowedCidrs, before.AllowedCidrs) && Editable(nameof(FlowerServerOptions.AllowedCidrs)))
            {
                after.AllowedCidrs = Normalize(allowedCidrs);
                values[nameof(FlowerServerOptions.AllowedCidrs)] = ToJsonArray(after.AllowedCidrs);
            }
            if (update.LibraryPaths is { } libraryPaths && !Same(libraryPaths, before.LibraryPaths) && Editable(nameof(FlowerServerOptions.LibraryPaths)))
            {
                after.LibraryPaths = Normalize(libraryPaths);
                values[nameof(FlowerServerOptions.LibraryPaths)] = ToJsonArray(after.LibraryPaths);
            }

            if (update.IntegrateWithITunes is { } integrate && integrate != before.IntegrateWithITunes && Editable(nameof(FlowerServerOptions.IntegrateWithITunes)))
            {
                after.IntegrateWithITunes = integrate;
                values[nameof(FlowerServerOptions.IntegrateWithITunes)] = JsonValue.Create(integrate);
            }
            if (update.SyncPlayCountFromITunes is { } syncPlayCount && syncPlayCount != before.SyncPlayCountFromITunes && Editable(nameof(FlowerServerOptions.SyncPlayCountFromITunes)))
            {
                after.SyncPlayCountFromITunes = syncPlayCount;
                values[nameof(FlowerServerOptions.SyncPlayCountFromITunes)] = JsonValue.Create(syncPlayCount);
            }
            if (update.SyncDateAddedFromITunes is { } syncDateAdded && syncDateAdded != before.SyncDateAddedFromITunes && Editable(nameof(FlowerServerOptions.SyncDateAddedFromITunes)))
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

            return Results.Json(
                await DescribeAsync(after, boundServer, publicAddress, publicReachability, advertiser, configuration, signingKey, context.RequestAborted),
                jsonOptions);
        });

        authenticated.MapGet("/library", (LibraryRescanCoordinator rescans, LibraryWriteAccess writeAccess) =>
            Results.Json(DescribeLibrary(rescans, writeAccess), jsonOptions));

        // Answered as soon as the scan is *started*, not when it finishes - a
        // full scan of a NAS share outlasts any sensible request timeout. The
        // page polls GET /library above for the rest.
        authenticated.MapPost("/library/rescan", (LibraryRescanCoordinator rescans, LibraryWriteAccess writeAccess) =>
        {
            rescans.TryStart();
            return Results.Json(DescribeLibrary(rescans, writeAccess), jsonOptions);
        });

        // "Remove from Library" from an admin device - see LibraryRemoval, which
        // the device runs on its own copy of the same songs once this answers.
        //
        // An owner's act for the same reason the cover-art write below is one,
        // and more so: with DeleteFiles it deletes files from this machine's
        // disk. What bounds that is what can be named - a catalog id, resolved
        // against this library - so the only files a caller can reach are ones
        // this server already serves; no path ever arrives from the wire.
        authenticated.MapPost("/library/remove", async (HttpContext context, Library library) =>
        {
            LibraryRemovalRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LibraryRemovalRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request?.TrackIds is not { Count: > 0 } ids)
                return Problems.BadRequest("Name at least one track to remove.");

            var tracks = ids
                .Select(library.Find)
                .OfType<Track>()
                .DistinctBy(t => t.Id)
                .ToList();
            var result = LibraryRemoval.Remove(library, tracks, request.DeleteFiles, logger);

            logger.LogInformation(
                "{Fingerprint} removed {Removed} of {Asked} track(s) from the library ({Trashed} file(s) to the trash, {Deleted} deleted, {NotDeleted} left where they were)",
                context.Items[AdminFingerprintKey], result.Removed, ids.Count, result.FilesTrashed, result.FilesDeleted, result.FilesNotDeleted.Count);

            return Results.Json(
                new LibraryRemovalResponseDto(result.Removed, result.FilesTrashed, result.FilesDeleted, result.FilesNotDeleted.Count), jsonOptions);
        });

        // A file from an owner's device, taken into this library - see
        // LibraryIngest, which is the whole of what happens and why the file
        // arrives in pieces. These two routes only translate: a body into a
        // call, and an outcome into a status the device can act on.
        //
        // The first names the file and learns where to start from; each PUT
        // after it carries the next piece, and the one that completes the file
        // answers with the song.
        //
        // Not while a scan is running. A device uploads what the catalog does
        // not list, and a server part-way through its first scan lists almost
        // nothing - of a folder that may well already hold every one of those
        // songs. Taking files then would fill the library with second copies
        // of music the scan was about to find. The device stops, and the
        // catalog token moving when the scan ends is what starts it again.
        authenticated.MapPost(UploadsRoute, async (HttpContext context, LibraryIngest ingest, LibraryRescanCoordinator rescans) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);

            LibraryUploadRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LibraryUploadRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request == null)
                return Problems.BadRequest("Name the file to upload.");

            var result = await ingest.BeginAsync(request, context.RequestAborted);
            LogUpload(logger, context, request.RelativePath, result);
            return ToResult(result, jsonOptions);
        });

        authenticated.MapPut(UploadsRoute + "/{uploadId}", async (
            string uploadId, long? offset, HttpContext context, LibraryIngest ingest, LibraryRescanCoordinator rescans) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);

            // Optional in the signature and required here: a parameter the
            // framework requires is bound - and refused - before the endpoint
            // filter runs, so a stranger leaving it out was answered on its
            // merits before anyone had asked who it was.
            if (offset is not { } from)
                return Problems.BadRequest("Say where this piece starts, as offset.");

            // The gate refuses a body that does not state its length (411),
            // and an empty one is no piece of a file.
            if (context.Request.ContentLength is not > 0)
                return Problems.BadRequest("A piece of the file is required, with its length.");

            // Already in memory - the filter buffered it to check the
            // signature - and already bounded by MaxBodyBytes there.
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);

            var result = await ingest.AppendAsync(
                uploadId, from, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.RequestAborted);
            LogUpload(logger, context, uploadId, result);
            return ToResult(result, jsonOptions);
        });

        // An owner's device moved or renamed one of its own files; this
        // server's copy follows - see LibraryIngest.MoveAsync. Kept from
        // running during a scan for the reason an upload is: the scan would
        // find one file missing and another new, and settle it differently.
        authenticated.MapPost(MoveRoute, async (HttpContext context, LibraryIngest ingest, LibraryRescanCoordinator rescans) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);

            LibraryMoveRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LibraryMoveRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is not { TrackId.Length: > 0, RelativePath.Length: > 0 })
                return Problems.BadRequest("Name the song and where its file has moved to.");

            var result = await ingest.MoveAsync(request, context.RequestAborted);
            if (result.Outcome != IngestOutcome.Completed)
            {
                logger.LogInformation("Refused a move from {Fingerprint} ({TrackId}): {Outcome} - {Error}",
                    context.Items[AdminFingerprintKey], request.TrackId, result.Outcome, result.Error);
            }

            return ToResult(result, jsonOptions);
        });

        // Tags edited on an owner's device - in Track Info, on a desktop or a
        // phone, of a song it has a file for or one it only streams - written
        // into this server's files, and from here served to every device (see
        // TrackTags.ApplyEdits, and Track.TagsEditedAt for how they then
        // travel). Not during a scan, which is busy reading the very tags
        // this writes and would publish whichever it happened to read.
        authenticated.MapPost(TagsRoute, async (HttpContext context, Library library, LibraryRescanCoordinator rescans) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);

            LibraryTagEditsRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<LibraryTagEditsRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request?.Edits is not { Count: > 0 } edits || edits.Any(e => e?.Tags == null || string.IsNullOrEmpty(e.TrackId)))
                return Problems.BadRequest("Name at least one song and its tags.");

            var response = TrackTags.ApplyEdits(library, edits, logger);

            logger.LogInformation(
                "{Fingerprint} edited the tags of {Applied} of {Sent} song(s) ({NotWritten} could not be written)",
                context.Items[AdminFingerprintKey], response.Applied, edits.Count, response.NotWritten.Count);
            return Results.Json(response, jsonOptions);
        });

        // Artwork changed on an owner's device, for the songs it changed it on
        // (TrackArtwork.ApplyEdit). The picture is the body; which songs and
        // when are in the query, which the signature covers like the body.
        // DELETE is the same statement about no picture at all.
        authenticated.MapPut(ArtworkRoute, async (
            HttpContext context, Library library, LibraryRescanCoordinator rescans, string? ids, string? editedAt) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);
            if (ParseArtworkEdit(ids, editedAt) is not { } edit)
                return Problems.BadRequest("Name the songs and when the artwork was changed.");

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();

            // Sniffed rather than trusted, as the album route below does and
            // for its reason: the type ends up inside the tag.
            if (LocalAlbumArtReader.MimeTypeForBytes(bytes) is not { } mimeType)
                return Problems.BadRequest("That body is not an image Flower can read.");

            var response = TrackArtwork.ApplyEdit(library, edit.Ids, edit.At, new LocalAlbumArt(bytes, mimeType), logger);
            logger.LogInformation("{Fingerprint} changed the artwork of {Applied} of {Sent} song(s)",
                context.Items[AdminFingerprintKey], response.Applied, edit.Ids.Count);
            return Results.Json(response, jsonOptions);
        });

        authenticated.MapDelete(ArtworkRoute, (
            HttpContext context, Library library, LibraryRescanCoordinator rescans, string? ids, string? editedAt) =>
        {
            if (rescans.IsRunning)
                return ScanInProgress(jsonOptions);
            if (ParseArtworkEdit(ids, editedAt) is not { } edit)
                return Problems.BadRequest("Name the songs and when the artwork was removed.");

            var response = TrackArtwork.ApplyEdit(library, edit.Ids, edit.At, art: null, logger);
            logger.LogInformation("{Fingerprint} removed the artwork of {Applied} of {Sent} song(s)",
                context.Items[AdminFingerprintKey], response.Applied, edit.Ids.Count);
            return Results.Json(response, jsonOptions);
        });

        // The other half of Remove from Library: the files removed and kept,
        // and the way back for them. Restore touches no file - it only takes
        // paths off the list the scans consult - so the paths it is handed need
        // no checking beyond that: one that is not on the list does nothing.
        authenticated.MapGet("/library/removed", (Library library) =>
            Results.Json(
                library.ExcludedPaths
                    .Select(e => new RemovedFileDto(e.Path, e.ExcludedAt, File.Exists(e.Path)))
                    .ToList(),
                jsonOptions));

        authenticated.MapPost("/library/removed/restore", async (
            HttpContext context, Library library, LibraryRescanCoordinator rescans) =>
        {
            RestoreRemovedFilesRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<RestoreRemovedFilesRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request?.Paths is not { Count: > 0 } paths)
                return Problems.BadRequest("Name at least one file to restore.");

            var restored = library.RestoreExcludedPaths(paths);

            // A restored file is back in the library the moment a scan finds
            // it, and nothing else would scan until the next startup.
            if (restored > 0)
                rescans.TryStart();

            logger.LogInformation("{Fingerprint} restored {Restored} removed file(s) to the library",
                context.Items[AdminFingerprintKey], restored);
            return Results.Json(new RestoreRemovedFilesResponseDto(restored), jsonOptions);
        });

        // And the way out that frees the space: the files named are deleted
        // for good. A removal never does this by itself - not one made here
        // with the files kept, and not one that arrived from a device whose
        // own copy was deleted - so this is the one place the owner says
        // "those, permanently". Only paths on the list are touched (see
        // LibraryRemoval.DeleteRemovedFiles), so nothing arrives from the wire
        // that can reach a file an admin had not already removed.
        authenticated.MapPost("/library/removed/delete", async (
            HttpContext context, Library library, IOptionsMonitor<FlowerServerOptions> options) =>
        {
            DeleteRemovedFilesRequestDto? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<DeleteRemovedFilesRequestDto>(
                    context.Request.Body, jsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request?.Paths is not { Count: > 0 } paths)
                return Problems.BadRequest("Name at least one file to delete.");

            var (deleted, notDeleted) = LibraryRemoval.DeleteRemovedFiles(
                library, paths, options.CurrentValue.LibraryPaths, logger);

            logger.LogInformation("{Fingerprint} permanently deleted {Deleted} removed file(s) ({NotDeleted} could not be deleted)",
                context.Items[AdminFingerprintKey], deleted, notDeleted);
            return Results.Json(new DeleteRemovedFilesResponseDto(deleted, notDeleted), jsonOptions);
        });

        // Album art, written into the server's own files.
        //
        // This is the admin surface's one *content* write, and it is on the admin
        // surface because it is an owner's act, not a listener's: a whole-file
        // rewrite does not belong behind the credential a player browses with.
        // TrustedPeer.IsAdmin is the right gate for it.
        //
        // Addressed by the same id the cover-art read takes - an album id
        // or a song id - and it writes into exactly the files that read would
        // have consulted (MediaEndpoints.CoverArtCandidates). That symmetry
        // is the whole point: art is addressed per album on the way out (see
        // LibraryDtoMapper's CoverArt field), so writing it into one track of an
        // album would leave the album still serving whichever other file the
        // read path happened to reach first, and look to the caller like the
        // change had been silently dropped.
        authenticated.MapPut("/cover-art", async (HttpContext context, Library library, string? id) =>
        {
            if (string.IsNullOrEmpty(id))
                return Problems.BadRequest("An album or song id is required.");

            var contentType = context.Request.ContentType?.Split(';')[0].Trim();

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
                return Problems.BadRequest("An image body is required.");

            // Sniffed rather than trusted, and the request is refused when the
            // two disagree with each other about nothing recognisable: the MIME
            // type ends up inside the tag, where every later reader believes it,
            // so an "image/jpeg" header over a zip file would poison the file
            // rather than fail here.
            var sniffed = LocalAlbumArtReader.MimeTypeForBytes(bytes);
            if (sniffed == null)
                return Problems.BadRequest("That body is not an image Flower can read.");

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
                ? Problems.BadRequest("An album or song id is required.")
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
                return Problems.NotFound();

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
    // Null for a request that names no song or no moment.
    private static (List<string> Ids, DateTimeOffset At)? ParseArtworkEdit(string? ids, string? editedAt)
    {
        var parsed = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parsed.Count == 0
            || !DateTimeOffset.TryParse(editedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at))
        {
            return null;
        }

        return (parsed, at);
    }

    private static IResult WriteCoverArt(
        string id, Library library, ILogger logger, JsonSerializerOptions jsonOptions, Func<string, bool> write)
    {
        var candidates = MediaEndpoints.CoverArtCandidates(id, library);
        if (candidates.Count == 0)
            return Problems.NotFound("No track on this server has that id.");

        var written = 0;
        var repainted = new List<(Track Track, DateTimeOffset EditedAt, string? FileStamp)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            if (candidate.Path is { Length: > 0 } path && write(path))
            {
                written++;
                repainted.Add((candidate, now, null));
            }
        }

        // Dated, so every device holding its own copy of one of these files
        // fetches the new picture into it (Track.ArtEditedAt). Until this,
        // changing a cover here changed it for whoever streamed the album and
        // for nobody who had downloaded it.
        library.ApplySyncedArt(repainted);

        if (written == 0)
            return Problems.Of(StatusCodes.Status500InternalServerError, ProblemCodes.ServerError,
                "The artwork could not be written to any of those files.");

        logger.LogInformation("Album art for {Id} rewritten on {Written} of {Total} files.",
            id, written, candidates.Count);
        return Results.Json(new CoverArtWriteResponse(written, candidates.Count), jsonOptions);
    }

    // Each refusal as the status that says what to do about it: 400 and 409
    // are about this file and final, 503 is about this server and worth
    // stopping the whole batch for, 404 means begin again, and 422 means send
    // it again.
    private static IResult ToResult(IngestResult result, JsonSerializerOptions jsonOptions) => result.Outcome switch
    {
        IngestOutcome.Accepted or IngestOutcome.Completed when result.Moved != null => Results.Json(result.Moved, jsonOptions),
        IngestOutcome.Accepted or IngestOutcome.Completed => Results.Json(result.Status, jsonOptions),
        IngestOutcome.Conflict => Problems.Of(StatusCodes.Status409Conflict, ProblemCodes.Conflict, result.Error),
        IngestOutcome.Unavailable => Problems.Of(StatusCodes.Status503ServiceUnavailable, ProblemCodes.Unavailable, result.Error),
        IngestOutcome.UnknownUpload => Problems.Of(StatusCodes.Status404NotFound, ProblemCodes.NotFound, result.Error),
        IngestOutcome.Corrupt => Problems.Of(StatusCodes.Status422UnprocessableEntity, ProblemCodes.Corrupt, result.Error),
        _ => Problems.BadRequest(result.Error),
    };

    // Its own code rather than unavailable: this one passes by itself, and a
    // device waits for the library token to move rather than for an operator.
    private static IResult ScanInProgress(JsonSerializerOptions jsonOptions) =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, ProblemCodes.Scanning,
            "This server is scanning its library; uploads wait until it has finished.");

    // A song arriving is worth a line; a piece of one is not. Refusals are
    // logged whichever step they happen at, since a refusal is the end of
    // that file's upload.
    private static void LogUpload(ILogger logger, HttpContext context, string what, IngestResult result)
    {
        if (result.Outcome == IngestOutcome.Completed)
        {
            logger.LogInformation("{Fingerprint} uploaded {TrackId} to the library",
                context.Items[AdminFingerprintKey], result.Status?.TrackId);
        }
        else if (result.Outcome != IngestOutcome.Accepted)
        {
            logger.LogInformation("Refused an upload from {Fingerprint} ({What}): {Outcome} - {Error}",
                context.Items[AdminFingerprintKey], what, result.Outcome, result.Error);
        }
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

    private static LibraryStatusResponse DescribeLibrary(LibraryRescanCoordinator rescans, LibraryWriteAccess writeAccess) =>
        new(rescans.IsRunning, rescans.TrackCount, rescans.LastCompletedAt, rescans.LastError, writeAccess.UnwritableFolders);

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
    //
    // The reachability check runs first because it is what looks the public
    // address up, and the address list below includes the origin built from it
    // - so a page that has just opened the door shows the address it opened it
    // at, in both places, on the same response.
    private static async Task<ServerSettingsDto> DescribeAsync(
        FlowerServerOptions options, IServer boundServer, PublicAddressProbe publicAddress,
        PublicReachability publicReachability, MdnsAdvertiser advertiser, IConfiguration configuration,
        DeviceSigningKey signingKey, CancellationToken ct)
    {

        var reachability = await publicReachability.CheckAsync(options, ct);

        return new(MdnsAdvertiser.InstanceName(options),
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
            DiscoveryEndpoints.ReachableOrigins(boundServer, options, publicReachability.OriginFor(options)),
            await publicAddress.GetAsync(ct),
            Fingerprint: signingKey.Fingerprint,
            Overridden: SettingsOverrides.Describe(configuration),
            AdvertisedAs: advertiser.ClaimedInsteadOf == MdnsAdvertiser.InstanceName(options) ? advertiser.Name : null,
            PublicOrigin: reachability?.Origin,
            PublicReachability: reachability?.Status.ToString());
    }

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
