using System.Collections.Generic;
using System.Linq;

using Flower.Persistence;

namespace Flower.Services;

// Builds an OpenSubsonicClient pointed at a peer's embedded SyncHttpServer
// host, with the same signed peer-identity credentials every other peer-to-
// peer call uses (see SignedRequestCanonicalizer/DeviceSigningKey) rather
// than real OpenSubsonic credentials - both LibraryDownloadService (the
// download-button feature) and PeerLibraryViewModel (live browsing/
// streaming, unrestricted by role - see SyncRolePolicy) go through this one
// factory rather than each duplicating the same construction.
public static class PeerOpenSubsonicClientFactory
{
    public static OpenSubsonicClient Create(DiscoveredDevice peer, DeviceIdentity identity, AppSettings appSettings, DeviceSigningKey signingKey) =>
        new($"http://{peer.EndPoint}", username: "", password: "",
            peerIdentityParams: (method, path, extraParams, body) =>
            {
                var identityParams = new List<(string Key, string Value)>
                {
                    ("X-Flower-Fingerprint", identity.Fingerprint),
                    ("X-Flower-Alias", identity.Alias),
                    ("X-Flower-Role", appSettings.IsServer ? "server" : "client"),
                    ("X-Flower-PublicKey", signingKey.PublicKeyBase64),
                };
                var (signature, timestamp, nonce) = signingKey.Sign(method, path, extraParams.Concat(identityParams), body);
                return identityParams.Concat(
                [
                    ("X-Flower-Signature", signature),
                    ("X-Flower-Timestamp", timestamp),
                    ("X-Flower-Nonce", nonce),
                ]);
            });
}
