using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

public sealed record StreamTicketResponse(string Ticket, DateTimeOffset ExpiresAt, string Url);

// Mints the capability URLs the in-browser player needs (SYNC-PLAN.md, "The
// in-browser player: stream tickets"). See StreamTicketService for why an
// <audio> element cannot simply sign its own requests the way every other call
// from the web UI does.
//
// Any trusted peer may mint, not only an admin: playing a track is not an
// administrative act, and a paired phone falling back to a ticket for its own
// media element is the same situation as the browser's.
public static class StreamTicketEndpoints
{
    public static void MapStreamTicketEndpoints(this WebApplication app)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        app.MapPost("/api/flower/v1/stream-tickets", (
            HttpContext context, TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard,
            StreamTicketService tickets, string id) =>
        {
            // A signature, from the browser too: it holds a WebCrypto keypair
            // and pairs like any other device (see BrowserPeerCredentials). The
            // ticket is still needed, because what cannot authenticate itself
            // here is the <audio> element, not the tab that owns it.
            var auth = DeviceSignatureAuth.AuthenticateTrustedPeer(
                context.Request, [], trustedPeers, replayGuard);
            if (auth.Fingerprint == null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "A track id is required." });

            var (ticket, expiresAt) = tickets.Issue(id, auth.Fingerprint);

            // The whole point is a URL that can be dropped straight into an
            // <audio src>, so hand back the assembled thing rather than a bare
            // token every caller would have to concatenate identically.
            var url = $"/rest/stream?id={Uri.EscapeDataString(id)}&ticket={Uri.EscapeDataString(ticket)}";
            return Results.Json(new StreamTicketResponse(ticket, expiresAt, url), jsonOptions);
        });
    }
}
