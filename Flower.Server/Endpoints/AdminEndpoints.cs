using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record LoginResponse(string Token);
public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAt);
public sealed record TrustedDeviceResponse(string Fingerprint, string Alias, DateTimeOffset ApprovedAt);

// Browser/admin session auth + pairing-code issuance (SYNC-PLAN.md's
// "Pairing redesign" section) - the admin-facing half of step 3's build-order
// item. The new-device half (redeeming a code) is PairingEndpoints, kept
// separate since it's unauthenticated-by-admin-token by nature.
public static class AdminEndpoints
{
    // Same numbers as SyncHttpServer's own pre-trust rate limiters
    // (_infoRateLimiter/_pairRateLimiter) - an unauthenticated caller here can
    // mint a fresh attempt per request, so source IP is what actually costs
    // them anything.
    private static readonly RateLimiter LoginRateLimiter = new(max: 10, TimeSpan.FromSeconds(60));

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // /login deliberately isn't mapped on the "authenticated" group below -
        // a RouteGroupBuilder's conventions (including AddEndpointFilter) apply
        // to every endpoint ever mapped on that builder instance regardless of
        // Map-vs-AddEndpointFilter call order, so putting /login on the same
        // group as the bearer-auth filter would gate the very endpoint that's
        // supposed to hand out the token in the first place.
        app.MapPost("/api/admin/login", (LoginRequest request, HttpContext context, AdminAuthService auth) =>
        {
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!LoginRateLimiter.TryAcquire(key, DateTimeOffset.UtcNow))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            if (!auth.ValidateCredentials(request.Username, request.Password))
                return Results.Unauthorized();

            return Results.Json(new LoginResponse(auth.IssueToken()), jsonOptions);
        });

        var authenticated = app.MapGroup("/api/admin").AddEndpointFilter(async (context, next) =>
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<AdminAuthService>();
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..]
                : null;
            if (!auth.ValidateToken(token))
                return Results.Unauthorized();
            return await next(context);
        });

        authenticated.MapPost("/pairing-codes", (PairingCodeService pairing) =>
        {
            var (code, expiresAt) = pairing.GenerateCode();
            return Results.Json(new PairingCodeResponse(code, expiresAt), jsonOptions);
        });

        authenticated.MapGet("/devices", (TrustedPeerStore store) =>
        {
            var devices = store.Load()
                .Select(p => new TrustedDeviceResponse(p.Fingerprint, p.Alias, p.ApprovedAt))
                .ToList();
            return Results.Json(devices, jsonOptions);
        });

        authenticated.MapDelete("/devices/{fingerprint}", async (string fingerprint, TrustedPeerStore store) =>
        {
            await store.RevokeAsync(fingerprint);
            return Results.NoContent();
        });
    }
}
