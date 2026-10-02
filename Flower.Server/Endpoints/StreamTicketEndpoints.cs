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
    // The request is a query string and nothing else.
    private const long MaxBodyBytes = 4 * 1024;

    public static void MapStreamTicketEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(StreamTicketEndpoints));

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        app.MapPost("/api/flower/v1/stream-tickets", async (
            HttpContext context, RequestGate gate, StreamTicketService tickets, string id) =>
        {
            // A signature, from the browser too: it holds a WebCrypto keypair
            // and pairs like any other device (see BrowserPeerCredentials). The
            // ticket is still needed, because what cannot authenticate itself
            // here is the <audio> element, not the tab that owns it.
            //
            // Through the same gate as the sync group, and charged to the
            // device's media plane: minting a ticket is the first request of a
            // playback, and until docs/TRUST-BOUNDARY-PLAN.md step 2 this route
            // had no budget at all. Outside that group still, because it
            // answers an unknown device 401 where the group answers 403, and
            // which code means what is step 3's to settle, not this one's.
            var admitted = await gate.AdmitAsync(context, RequestGate.Plane.Media, MaxBodyBytes, logger);
            switch (admitted.Outcome)
            {
                case RequestGate.Outcome.LengthRequired:
                    return Results.StatusCode(StatusCodes.Status411LengthRequired);
                case RequestGate.Outcome.TooLarge:
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                case RequestGate.Outcome.Throttled:
                    return RateLimitResponse.TooManyRequests(context);
                case RequestGate.Outcome.Unknown:
                case RequestGate.Outcome.BadSignature:
                    return Results.Unauthorized();
            }

            var fingerprint = admitted.Fingerprint!;

            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "A track id is required." });

            // Kept with the ticket for its lifetime, so bounded like the event
            // ids PlayReportDto bounds - a catalog id is far shorter.
            if (id.Length > PlayReportDto.MaxIdLength)
                return Results.BadRequest(new { error = "That is not a track id." });

            var (ticket, expiresAt) = tickets.Issue(id, fingerprint);

            // The whole point is a URL that can be dropped straight into an
            // <audio src>, so hand back the assembled thing rather than a bare
            // token every caller would have to concatenate identically.
            // Scoping the ticket to this group means it cannot be spent
            // anywhere but here.
            var url = $"/api/flower/v1/stream?id={Uri.EscapeDataString(id)}&ticket={Uri.EscapeDataString(ticket)}";
            return Results.Json(new StreamTicketResponse(ticket, expiresAt, url), jsonOptions);
        });
    }
}
