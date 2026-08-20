using System.Text.Json;

using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// The identity half of LAN discovery. mDNS (MdnsAdvertiser) only tells a client
// that something is listening at an address; this is where it learns what that
// something is, and it is what turns a bare mDNS hit into a sidebar row - a
// client discards any peer whose handshake it cannot resolve (see
// NetworkDiscoveryService.ResolveAliasAsync).
//
// The response shape is shared with the app's own SyncHttpServer
// (SyncInfoResponseDto in Flower.Core), because the client cannot tell - and
// should not have to - whether the peer answering is another Flower app or a
// headless server. IsServer is the one field that differs, and it is always
// true here.
public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this WebApplication app)
    {
        // Deliberately ungated, exactly as SyncHttpServer's is: a peer has to
        // learn our fingerprint and public key here before either side can
        // evaluate trust at all. LanGuard still fronts it, so this is reachable
        // from the local network and not the open internet.
        //
        // TrustsCaller is the useful part for an already-paired client: it polls
        // this every ~5s, so a device this server has revoked finds out on its
        // own timetable rather than at its next failed sync. Null when the
        // caller did not identify itself, so an anonymous probe reads as
        // "unknown", never as a rejection.
        app.MapGet(SyncProtocol.InfoPath, (
            HttpContext context, IOptions<FlowerServerOptions> options,
            DeviceSigningKey signingKey, TrustedPeerStore trustedPeers, Library library) =>
        {
            var callerFingerprint = context.Request.Headers["X-Flower-Fingerprint"].ToString();
            var response = new SyncInfoResponseDto(
                MdnsAdvertiser.InstanceName(options.Value),
                "2.0",
                null,
                "server",
                signingKey.Fingerprint,
                signingKey.PublicKeyBase64,
                IsServer: true,
                Download: false,
                string.IsNullOrEmpty(callerFingerprint) ? null : trustedPeers.IsTrusted(callerFingerprint),
                library.ChangeToken);

            return Results.Json(response, SyncProtocolJsonContext.Default.SyncInfoResponseDto);
        });
    }
}
