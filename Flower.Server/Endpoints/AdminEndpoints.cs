using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Flower.Logging;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt, bool GrantsAdmin, string Invite);
public sealed record TrustedDeviceResponse(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, bool IsAdmin);
public sealed record SubsonicCredentialResponse(
    string Username, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? Password);
public sealed record AdminSessionResponse(string Token, DateTimeOffset ExpiresAt, string Url);
public sealed record LibraryStatusResponse(bool Rescanning, int TrackCount, DateTimeOffset? LastCompletedAt, string? LastError);
public sealed record LogEntryResponse(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception);

// The settings an operator may change from the browser, which is exactly the
// operator-editable half of FlowerServerOptions - DataDirectory is excluded
// deliberately (it is what located the file these are written to; see
// ServerSettingsWriter), and so is WebUiPath, which is part of how the server was
// deployed rather than something the page served from it should be able to move
// out from under itself.
//
// RestartRequired names the fields whose new value is on disk and bound but not
// yet acted on, so the page can say so instead of appearing to have done nothing:
// MdnsAdvertiser reads its options once, when the hosted service starts.
public sealed record ServerSettingsResponse(
    string Alias,
    string AdvertisedHost,
    bool AdvertiseOnLan,
    bool TrustTailscaleRange,
    IReadOnlyList<string> AllowedCidrs,
    IReadOnlyList<string> LibraryPaths,
    string DataDirectory,
    string? Version,
    IReadOnlyList<string>? RestartRequired = null);

public sealed record ServerSettingsUpdate(
    string? Alias,
    string? AdvertisedHost,
    bool? AdvertiseOnLan,
    bool? TrustTailscaleRange,
    IReadOnlyList<string>? AllowedCidrs,
    IReadOnlyList<string>? LibraryPaths);

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
// The one caller that cannot sign is a browser (no asymmetric crypto in
// .NET-for-WebAssembly at all), so it presents an AdminSessionService token minted
// for it by a device that can. That is a derived authority, not a second
// mechanism: the token is only ever as good as the admin peer behind it, and
// IsAdmin is re-checked against the trust store on every request carrying one.
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var authenticated = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var trustedPeers = services.GetRequiredService<TrustedPeerStore>();
            var replayGuard = services.GetRequiredService<NonceReplayGuard>();
            var sessions = services.GetRequiredService<AdminSessionService>();

            var fingerprint = sessions.Resolve(http.Request.Headers[AdminSessionHeader], DateTimeOffset.UtcNow);
            if (fingerprint == null)
            {
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

                fingerprint = DeviceSignatureAuth.VerifyTrustedPeer(http.Request, body, trustedPeers, replayGuard);
                if (fingerprint == null)
                    return Results.Unauthorized();
            }

            // Authenticated as *a* peer is not authorized as an admin: a paired
            // phone can sign a perfectly valid request to these routes, and must
            // still be turned away. Checked live rather than baked into the
            // session token, so demoting or revoking a device takes effect on its
            // next request instead of at token expiry.
            // StatusCode(403), not Results.Forbid(): Forbid() runs the ASP.NET
            // Core authentication stack's forbid handler, and this app registers
            // no authentication scheme at all - it authenticates by device
            // signature - so it throws rather than answering, turning every
            // "paired but not an admin" refusal into a 500.
            if (!IsAdmin(fingerprint, trustedPeers))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

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
            return Results.Json(new PairingCodeResponse(code, expiresAt, grantsAdmin, invite.ToString()), jsonOptions);
        });

        // Mints the token the browser settings page runs on. Deliberately refused
        // to a caller that is itself only holding a session token: a bearer token
        // that can mint its own successor is not short-lived in any meaningful
        // sense. Re-minting is one click on a device that holds a real key.
        authenticated.MapPost("/sessions", (HttpContext context, AdminSessionService sessions) =>
        {
            if (context.Request.Headers.ContainsKey(AdminSessionHeader))
                return Results.BadRequest(new { error = "An admin session cannot mint another one." });

            var fingerprint = (string)context.Items[AdminFingerprintKey]!;
            var (token, expiresAt) = sessions.Issue(fingerprint);
            var request = context.Request;
            var url = $"{request.Scheme}://{request.Host}/#admin={token}&page=settings";
            return Results.Json(new AdminSessionResponse(token, expiresAt, url), jsonOptions);
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
            StreamTicketService tickets, AdminSessionService sessions) =>
        {
            // Revoking the key this very request was signed with would lock the
            // caller out mid-session, and is far more likely a misclick on the
            // wrong row than a deliberate act. Removing another admin is still
            // allowed.
            if (string.Equals(context.Items[AdminFingerprintKey] as string, fingerprint, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "A device cannot revoke itself." });

            await store.RevokeAsync(fingerprint);
            // Otherwise "revoke this device" would leave its already-minted
            // stream URLs playable, and any browser session it handed out still
            // able to administer, for the rest of their lifetime.
            tickets.RevokeFor(fingerprint);
            sessions.RevokeFor(fingerprint);
            return Results.NoContent();
        });

        // Path B (SYNC-PLAN.md): third-party Subsonic clients can't hold a
        // keypair, so they get a generated credential from the same admin
        // surface instead - one issuer, one list, one revoke button.
        authenticated.MapPost("/subsonic-credentials", async (SubsonicCredentialStore store, string? label) =>
        {
            var credential = await store.IssueAsync(label ?? "Subsonic client");
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

        authenticated.MapDelete("/subsonic-credentials/{username}", async (string username, SubsonicCredentialStore store) =>
        {
            return await store.RevokeAsync(username) ? Results.NoContent() : Results.NotFound();
        });

        authenticated.MapGet("/settings", (IOptionsMonitor<FlowerServerOptions> options) =>
            Results.Json(Describe(options.CurrentValue), jsonOptions));

        // Read from the raw (buffered, rewound) stream rather than a bound
        // parameter - see the filter above for why no route here may bind a body.
        authenticated.MapPut("/settings", async (
            HttpContext context, IOptionsMonitor<FlowerServerOptions> options, ILoggerFactory loggerFactory) =>
        {
            ServerSettingsUpdate? update;
            try
            {
                update = await JsonSerializer.DeserializeAsync<ServerSettingsUpdate>(
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
                AllowedCidrs = [.. before.AllowedCidrs],
                LibraryPaths = [.. before.LibraryPaths],
            };

            if (update.Alias is { } alias && alias.Trim() != before.Alias)
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

            if (values.Count > 0)
            {
                await ServerSettingsWriter.WriteAsync(before.DataDirectory, values, context.RequestAborted);
                loggerFactory.CreateLogger(typeof(AdminEndpoints)).LogInformation(
                    "{Fingerprint} updated server settings: {Keys}",
                    context.Items[AdminFingerprintKey], string.Join(", ", values.Keys));
            }

            return Results.Json(Describe(after) with { RestartRequired = restartRequired }, jsonOptions);
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

        // This server's own log, from the same in-memory buffer the app's Log
        // window reads (AppLogging.Initialize wires the sink in Program.cs), so a
        // headless box can be diagnosed from a browser instead of by SSHing in to
        // tail a file.
        authenticated.MapGet("/logs", (int? limit) =>
        {
            var entries = InMemoryLogStore.Instance.Snapshot();
            var take = Math.Clamp(limit ?? 500, 1, entries.Count == 0 ? 1 : entries.Count);
            var lines = entries
                .Skip(Math.Max(0, entries.Count - take))
                .Select(e => new LogEntryResponse(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception))
                .ToList();
            return Results.Json(lines, jsonOptions);
        });
    }

    internal const string AdminFingerprintKey = "Flower.AdminFingerprint";

    // Where a browser session token travels. A header, not a query parameter:
    // unlike a stream ticket it is never dropped into a media element's URL, so
    // there is no reason to let it end up in a referrer or a proxy access log.
    internal const string AdminSessionHeader = "X-Flower-Admin-Session";

    // Matches the process-wide Kestrel ceiling (see Program.cs). Nothing here is
    // remotely near it - a settings body is a few hundred bytes - but a signed
    // route has to buffer whatever arrives before it can verify it, so it needs a
    // stated limit rather than an implicit one.
    private const long MaxBodyBytes = 20 * 1024 * 1024;

    private static bool IsAdmin(string fingerprint, TrustedPeerStore trustedPeers) =>
        fingerprint == AdminSessionService.ConsoleFingerprint || trustedPeers.IsAdmin(fingerprint);

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

    private static ServerSettingsResponse Describe(FlowerServerOptions options) =>
        new(options.Alias,
            options.AdvertisedHost,
            options.AdvertiseOnLan,
            options.TrustTailscaleRange,
            options.AllowedCidrs.ToList(),
            options.LibraryPaths.ToList(),
            options.DataDirectory,
            typeof(AdminEndpoints).Assembly.GetName().Version?.ToString());

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
