using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Flower.Persistence;

namespace Flower.Services;

// The one IPeerCredentials every Flower app head uses: this device's own
// keypair, signing each request as it goes out (see DeviceSigningKey /
// SignatureVerifier). Lives here rather than in Flower.Core beside the
// interface only because DeviceIdentity and AppSettings do.
//
// Emits the same four identity params on every call, which the five call sites
// this replaced did not: the sync services sent Fingerprint/Alias/Role,
// PeerOpenSubsonicClientFactory sent those plus PublicKey, and
// ServerAdminClient.SignWith sent Fingerprint/Alias/PublicKey but no Role. The
// differences were accidental rather than meaningful, and unifying them is
// provably inert in both directions:
//
//   - Nothing is signed differently. SignedRequestCanonicalizer excludes every
//     X-Flower-* param from the canonical query precisely so the header and
//     query-string transports produce identical bytes, so adding one changes
//     nothing about what is signed or verified.
//   - Nothing is *read* differently. An X-Flower-PublicKey is only ever
//     consulted by the self-signed pairing check (PeerSignatureAuth.
//     VerifySelfSigned, reached from the pairing routes alone); a gated
//     endpoint looks the trusted key up by fingerprint and ignores it
//     entirely. X-Flower-Role is read only by SyncHttpServer's own role check,
//     which admin calls never reach.
//
// So the uniform set costs a few bytes per request and removes the standing
// question of which subset a new call site is supposed to copy.
public sealed class SignedDeviceCredentials(
    DeviceIdentity identity, DeviceSigningKey signingKey, AppSettings appSettings) : IPeerCredentials
{
    // Already-completed, always: this device holds its own key in-process, so
    // there is nothing to await. The task in the interface is there for the
    // browser, whose key lives behind crypto.subtle - see IPeerCredentials.
    public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
    {
        // Read live off AppSettings rather than captured at construction: this
        // instance outlives a role change (Settings can flip this device
        // between Client and Server - see SyncRolePolicy), and a stale role
        // would misdescribe the caller for the rest of the session.
        var identityParams = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", identity.Fingerprint),
            ("X-Flower-Alias", identity.Alias),
            ("X-Flower-Role", appSettings.IsServer ? "server" : "client"),
            ("X-Flower-PublicKey", signingKey.PublicKeyBase64),
        };

        // The identity params go into the signature input as well as onto the
        // request. They are filtered back out by the canonicalizer (see the
        // class comment), so this is not what makes them trustworthy - it just
        // keeps one call shape for both transports.
        var (signature, timestamp, nonce) = signingKey.Sign(
            method, absolutePath, query.Concat(identityParams), body);

        return Task.FromResult<IReadOnlyList<(string Key, string Value)>>(
        [
            .. identityParams,
            ("X-Flower-Signature", signature),
            ("X-Flower-Timestamp", timestamp),
            ("X-Flower-Nonce", nonce),
        ]);
    }
}
