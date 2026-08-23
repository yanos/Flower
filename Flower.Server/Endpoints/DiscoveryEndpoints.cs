using System.Text.Json;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
        // Two halves, though, and only the first is for strangers. The rest -
        // TrustsCaller and the address list - is answered only to a caller whose
        // signature actually verifies against a key this server has on file.
        //
        // The claimed X-Flower-Fingerprint header is not enough to earn that and
        // never was: a fingerprint is public (it is in this very response, and
        // in every pairing invite), so gating on one only asks the caller to
        // repeat something it read. That was tolerable while LanGuard meant this
        // route could not be reached from outside the LAN at all; it stops being
        // tolerable the moment a remote transport is switched on, and the
        // address list - which now includes this server's tailnet address - is
        // the thing it would hand over. See docs/OPEN-INTERNET-REVIEW.md.
        //
        // TrustsCaller is the useful part for an already-paired client: it polls
        // this every ~5s, so a device this server has revoked finds out on its
        // own timetable rather than at its next failed sync. Null when the
        // caller did not prove who it is, so an anonymous probe reads as
        // "unknown", never as a rejection.
        app.MapGet(SyncProtocol.InfoPath, (
            HttpContext context, IOptions<FlowerServerOptions> options,
            DeviceSigningKey signingKey, TrustedPeerStore trustedPeers, Library library,
            NonceReplayGuard replayGuard, IServer boundServer) =>
        {
            // GET, so the signed body is always empty.
            var caller = DeviceSignatureAuth.AuthenticateTrustedPeer(
                context.Request, [], trustedPeers, replayGuard);
            var callerIsTrusted = caller.Failure == PeerAuthFailure.None;
            // Whether the caller said anything about itself at all, kept apart
            // from whether it proved it: AuthenticateTrustedPeer answers both
            // with NotTrusted, but a probe that claimed no identity has not been
            // rejected and must not be told it was.
            var callerClaimedIdentity = !string.IsNullOrEmpty(
                DeviceSignatureAuth.GetIdentityValue(context.Request, "X-Flower-Fingerprint"));
            var response = new SyncInfoResponseDto(
                MdnsAdvertiser.InstanceName(options.Value),
                "2.0",
                null,
                "server",
                signingKey.Fingerprint,
                signingKey.PublicKeyBase64,
                IsServer: true,
                Download: false,
                // Only NotTrusted is a statement about the caller. A signature
                // that merely failed to verify says nothing about whether this
                // server trusts them, so it must not be reported as "no" - see
                // PeerAuthFailure.
                !callerClaimedIdentity ? null : caller.Failure switch
                {
                    PeerAuthFailure.None => true,
                    // No key on file for the fingerprint claimed - never
                    // approved, or approved and since revoked. The durable
                    // statement, and the one a revoked device needs to hear.
                    PeerAuthFailure.NotTrusted => false,
                    // A key *is* on file; this one request just didn't verify.
                    // Says nothing about the pairing, so it reads as unknown.
                    _ => null,
                },
                library.ChangeToken,
                // Every address this server thinks it can be reached on,
                // including its tailnet one - the whole point being that a
                // client which paired here on the LAN keeps working after it
                // leaves. See LocalAddresses and REMOTE-ACCESS-PLAN.md.
                //
                // Verified peers only. This is the one field in the handshake
                // that describes the server's own network position rather than
                // its identity, and it is only ever of use to a peer that has
                // paired - which is exactly the peer that can sign for it.
                callerIsTrusted
                    ? LocalAddresses.Reachable(
                        MdnsAdvertiser.AdvertisablePort(
                            boundServer.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
                            ?? SyncProtocol.DefaultPort,
                        options.Value.AdvertisedHost)
                    : null);

            return Results.Json(response, SyncProtocolJsonContext.Default.SyncInfoResponseDto);
        });
    }
}
