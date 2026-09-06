using System.Text.Json;

using Flower.Persistence;

using Flower.Server.Endpoints;

namespace Flower.Server.Subsonic;

public sealed record SubsonicCredentialResponse(
    string Username, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? Password);

// Issuing, listing and revoking the credentials third-party OpenSubsonic clients
// authenticate with - SYNC-PLAN.md's path B, and the only guessable credential
// this server has.
//
// Mapped onto the admin group rather than owning one, so there is still exactly
// one admin gate, one admin budget and one place that decides who counts as an
// administrator. What makes it belong here anyway is the other direction: these
// routes exist only because /rest does, and they are meaningless the moment it
// is gone.
public static class SubsonicCredentialEndpoints
{
    public static void MapSubsonicCredentialEndpoints(
        this RouteGroupBuilder authenticated, ILogger logger, JsonSerializerOptions jsonOptions)
    {
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
                context.Items[AdminEndpoints.AdminFingerprintKey], credential.Username, credential.Label);
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
                context.Items[AdminEndpoints.AdminFingerprintKey], username);

            return Results.NoContent();
        });
    }
}
