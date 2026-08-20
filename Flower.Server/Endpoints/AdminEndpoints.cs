using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt, bool GrantsAdmin, string Invite);
public sealed record TrustedDeviceResponse(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, bool IsAdmin);
public sealed record SubsonicCredentialResponse(
    string Username, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? Password);

// The admin API: issuing pairing codes, listing and revoking devices, and
// minting the per-client credentials third-party Subsonic clients need.
//
// There is no login route here, and no admin password anywhere in this
// project. Under SYNC-PLAN.md's "Passwordless by design" the browser admin UI
// holds a non-extractable WebCrypto keypair and pairs by redeeming a code
// exactly like a phone does, so these routes are gated by the same
// device-signature check as everything else (DeviceSignatureAuth) plus
// TrustedPeer.IsAdmin. That collapses what used to be two authentication
// mechanisms into one, and it is why AdminAuthService - bearer tokens, a
// login endpoint, a configured username and password - was deleted outright
// rather than kept as a fallback.
//
// Every route takes its inputs as query parameters and no request body. That
// is not a stylistic choice: a signature covers method, path, query and a hash
// of the body, and minimal-API model binding consumes the body stream before
// an endpoint filter can see it, so a body-carrying admin route would have to
// buffer and re-read the request just to be verifiable. The inputs here are a
// label and a boolean; the query string carries them fine, and pair-redeem
// already works this way for the same reason.
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

            // Handlers here read nothing from the body, and the signature is
            // computed over an empty one - so anything actually sent would be
            // unverified bytes riding along on a verified request. Refuse it
            // rather than ignore it.
            if (http.Request.ContentLength is > 0)
                return Results.BadRequest(new { error = "Admin requests do not take a body." });

            var fingerprint = DeviceSignatureAuth.VerifyTrustedPeer(http.Request, [], trustedPeers, replayGuard);
            if (fingerprint == null)
                return Results.Unauthorized();

            // Authenticated as *a* peer is not authorized as an admin: a paired
            // phone can sign a perfectly valid request to these routes, and
            // must still be turned away.
            if (!trustedPeers.IsAdmin(fingerprint))
                return Results.Forbid();

            http.Items[AdminFingerprintKey] = fingerprint;
            return await next(context);
        });

        // grantsAdmin is set by the issuer, never claimed by the redeemer -
        // see PairingCodeService. This is the "add another admin browser" path
        // as well as the ordinary "add a device" one.
        authenticated.MapPost("/pairing-codes", (
            HttpContext context, PairingCodeService pairing, DeviceSigningKey signingKey,
            IOptions<FlowerServerOptions> options, bool grantsAdmin = false) =>
        {
            var (code, expiresAt) = pairing.GenerateCode(grantsAdmin);
            var invite = BuildInvite(context, signingKey, options.Value, code);
            return Results.Json(new PairingCodeResponse(code, expiresAt, grantsAdmin, invite.ToString()), jsonOptions);
        });

        authenticated.MapGet("/devices", (TrustedPeerStore store) =>
        {
            var devices = store.Load()
                .Select(p => new TrustedDeviceResponse(p.Fingerprint, p.Alias, p.ApprovedAt, p.IsAdmin))
                .ToList();
            return Results.Json(devices, jsonOptions);
        });

        authenticated.MapDelete("/devices/{fingerprint}", async (
            string fingerprint, HttpContext context, TrustedPeerStore store, StreamTicketService tickets) =>
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
    }

    internal const string AdminFingerprintKey = "Flower.AdminFingerprint";

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
