using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// New-device half of the admin-issued pairing-code flow (SYNC-PLAN.md's
// "Passwordless by design", path A), and now the only way anything pairs with
// anything: there used to be a device-to-device route that held a request open
// for a live 60-second approval prompt, which needs a human in front of the
// thing being paired with. This trades that prompt for a code the admin already
// vetted out-of-band, which a headless box can actually issue.
public static class PairingEndpoints
{
    // Deliberately tight: a code is only valid for ~10 minutes total, so a hard
    // per-IP cap here is
    // most of what stands between "brute-forceable 8-char code" and not - see
    // PairingCodeService's alphabet/length for the resulting keyspace.
    private static readonly RateLimiter RedeemRateLimiter = new(max: 5, TimeSpan.FromSeconds(60));

    // Largest legitimate body here is empty - the redeem request carries
    // everything it needs in headers/query - but the signature covers the body
    // hash regardless,
    // so this still has to read *a* body, even a zero-length one, the same way.
    private const long MaxBodyBytes = 4 * 1024;

    public static void MapPairingEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PairingEndpoints));

        app.MapPost("/api/flower/v1/pair-redeem", async (
            HttpContext context, TrustedPeerStore trustedPeerStore, PairingCodeService pairingCodes,
            NonceReplayGuard replayGuard) =>
        {
            var request = context.Request;
            var key = RateLimiter.KeyFor(context.Connection.RemoteIpAddress);
            var now = DateTimeOffset.UtcNow;
            if (!RedeemRateLimiter.TryAcquire(key, now))
            {
                // Warning, unlike the sync group's own rate limit: redeeming is
                // a rare, deliberate act, so nothing legitimate does it fast
                // enough to be throttled. Being here means somebody is working
                // through pairing codes - which is also exactly why the line
                // itself is throttled: the caller controls how often this
                // happens, and one burst should cost one line plus a count, not
                // one line per attempt.
                if (RateLimitLogThrottle.ShouldLog(key, now, out var suppressed))
                {
                    RateLimitLogThrottle.Prune(now);
                    logger.LogWarning(
                        "Rate-limited a pairing redemption from {RemoteAddress}.{AlsoSuppressed}",
                        key, suppressed == 0 ? "" : $" ({suppressed} more since the last one.)");
                }

                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            using var bodyStream = new MemoryStream();
            await request.Body.CopyToAsync(bodyStream, cancellationToken: context.RequestAborted);
            if (bodyStream.Length > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var body = bodyStream.ToArray();

            var fingerprint = DeviceSignatureAuth.VerifySelfSigned(request, body, replayGuard, logger);
            if (fingerprint == null)
                return Results.Unauthorized();

            var code = DeviceSignatureAuth.GetIdentityValue(request, "X-Flower-PairingCode");
            // grantsAdmin comes back from the code itself, not from anything
            // the redeeming device said: a device asking to be an admin is not
            // evidence that it should be one. See PairingCodeService.
            if (!pairingCodes.TryConsume(code, out var grantsAdmin))
            {
                // Warning: the caller proved it holds the key it claims, so
                // this is a real device presenting a code that is not good.
                // Usually a typo or a code that expired while the person was
                // still typing it - but it is also exactly what guessing looks
                // like, and the code itself is deliberately not logged.
                logger.LogWarning(
                    "Rejected a pairing redemption from {RemoteAddress} by {Fingerprint}: "
                    + "the pairing code is invalid, expired, or already used.",
                    RemoteAddress(context), fingerprint);
                return Results.BadRequest(new { error = "Invalid, expired, or already-used pairing code." });
            }

            var publicKeyBase64 = DeviceSignatureAuth.GetIdentityValue(request, "X-Flower-PublicKey")!;
            var alias = DeviceSignatureAuth.GetIdentityValue(request, "X-Flower-Alias");
            if (string.IsNullOrEmpty(alias))
                alias = fingerprint;

            await trustedPeerStore.ApproveAsync(fingerprint, alias, publicKeyBase64, grantsAdmin);

            // The one line in this file that matters most. A redemption is how
            // a device gains durable access to the whole library, and with
            // grantsAdmin, to this server's settings - so it is an audit
            // record, at Information, and it says whether admin was granted.
            logger.LogInformation(
                "Paired {Alias} ({Fingerprint}) from {RemoteAddress}. Admin: {GrantsAdmin}.",
                alias, fingerprint, RemoteAddress(context), grantsAdmin);
            // The redeemer needs to know whether it may show the admin UI, and
            // this is the only moment it can learn that without already being
            // able to call an admin route.
            return Results.Ok(new { fingerprint, isAdmin = grantsAdmin });
        });
    }

    private static readonly RefusalLogThrottle RateLimitLogThrottle = new();

    private static string RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";
}
