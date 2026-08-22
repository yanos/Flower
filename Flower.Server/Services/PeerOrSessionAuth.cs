using Microsoft.AspNetCore.Http;

using Flower.Persistence;
using Flower.Server.Endpoints;
using Flower.Services;

namespace Flower.Server.Services;

// The sync surface's gate: a signed request from a trusted peer, or - failing
// that - a live admin session token standing in for the device that minted it.
//
// The fallback exists because the browser cannot sign anything.
// .NET-for-WebAssembly has no asymmetric crypto at all (see AdminSessionService
// for the full account), so a browser tab has no key to authenticate a catalog
// read or a ticket mint with, and without one it is a music player with an
// empty library and nothing it can play.
//
// This deliberately widens the admin-session bearer past /api/admin, which
// AdminSessionService's own comment records as a boundary, so it is worth
// stating what the trade actually is. In favour: both routes reached this way
// are read-and-play rather than administration, the token is still short-lived,
// still dies with the device that minted it, and LanGuard still keeps it
// unusable from off the LAN. Against: it is a bearer token, and it is now a
// bearer token for the catalog rather than for a settings page. The principled
// answer is the non-extractable WebCrypto keypair SYNC-PLAN.md's "the browser is
// a device" describes, which is not a drop-in - DeviceSigningKey.Sign is
// synchronous and crypto.subtle is not, so adopting it re-shapes every signing
// call site. This is the interim, and the seam is narrow enough (one call, two
// routes) that replacing it later is a local change.
//
// Note the asymmetry with /api/admin: there, a resolved session still has to
// clear IsAdmin. Here it only has to still be a peer this server trusts,
// because that is all a signature would have proved either. The console session
// minted at first run is trusted the same way it is for admin - it can never
// match a TrustedPeer, so it is answered for directly.
public static class PeerOrSessionAuth
{
    public static PeerAuthResult Authenticate(
        HttpRequest request, byte[] body, TrustedPeerStore trustedPeers,
        NonceReplayGuard replayGuard, AdminSessionService sessions, DateTimeOffset now)
    {
        var auth = DeviceSignatureAuth.AuthenticateTrustedPeer(request, body, trustedPeers, replayGuard);
        if (auth.Failure == PeerAuthFailure.None)
            return auth;

        var fingerprint = sessions.Resolve(request.Headers[AdminEndpoints.AdminSessionHeader], now);
        if (fingerprint == null)
            return auth;

        // Re-checked live rather than trusted from the token, for the same
        // reason the admin filter re-checks IsAdmin: revoking a device already
        // kills its sessions (AdminSessionService.RevokeFor), but a gate that
        // depends on that having been called is a gate that fails open the day
        // someone adds a second revocation path.
        if (fingerprint != AdminSessionService.ConsoleFingerprint && trustedPeers.GetPublicKey(fingerprint) == null)
            return new PeerAuthResult(null, PeerAuthFailure.NotTrusted);

        return new PeerAuthResult(fingerprint, PeerAuthFailure.None);
    }
}
