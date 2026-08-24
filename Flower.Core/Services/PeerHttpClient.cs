using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Flower.Services;

// Every HttpClient this app points at a Flower server, built in one place so
// they all agree about which certificates are acceptable.
//
// What it adds over `new HttpClient()` is a single rule: a certificate that
// validates the ordinary way is fine, and so is one whose public key is a key
// this device has already paired with. Nothing else is. That second clause is
// what lets a self-hosted server serve https with no certificate authority,
// no domain and no configuration - see DeviceCertificate for why the key is
// already on disk, and docs/REMOTE-TRANSPORT-PLAN.md's certificate section for
// why this is the floor rather than a workaround.
//
// It is deliberately *not* "trust anything self-signed". An unpinned
// self-signed certificate is refused exactly as it would be by any other
// client, because the whole argument for skipping the warning a browser would
// show is that a paired client is not in a browser's position: it knows which
// server it means. Take the pin away and that argument goes with it.
public static class PeerHttpClient
{
    // Answers "is this the public key of a server this device has paired
    // with?", in DeviceCertificate.PublicKeyRaw's encoding.
    //
    // A settable static rather than an injected dependency, because the
    // clients it has to reach are themselves static or self-constructed -
    // AlbumArtLoader holds one in a static field, NetworkDiscoveryService and
    // OpenSubsonicClient each build their own when not handed one - and
    // threading a service through all of them to answer one predicate would be
    // a larger change than the feature. Set once at startup from
    // TrustedPeerStore (see App.axaml.cs); read at callback time, so the order
    // of construction does not matter and a peer paired later is picked up
    // without rebuilding anything.
    //
    // Null means no pinning is possible yet, and the effect of that is a
    // refusal rather than a pass: a null predicate cannot vouch for a
    // certificate, so only ordinary chain validation can let one through.
    public static Func<byte[], bool>? IsPinnedServerKey { get; set; }

    public static HttpClient Create(TimeSpan? timeout = null)
    {
        // Under WebAssembly there is no TLS stack to configure: fetch is the
        // browser's, the browser decided about the certificate before any of
        // this ran, and setting the callback throws
        // PlatformNotSupportedException rather than being ignored. A tab is
        // also the one caller that has nothing to pin with - see
        // BrowserPeerCredentials - so there is nothing lost here either.
        if (OperatingSystem.IsBrowser())
            return timeout is { } browserTimeout ? new HttpClient { Timeout = browserTimeout } : new HttpClient();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                IsAcceptable(certificate, errors),
        };

        return timeout is { } value ? new HttpClient(handler) { Timeout = value } : new HttpClient(handler);
    }

    // Split out from the callback so it can be tested without a TLS handshake.
    public static bool IsAcceptable(X509Certificate2? certificate, SslPolicyErrors errors)
    {
        // The ordinary case, and the one that has to keep working unchanged: a
        // real certificate from a real authority, for a server reached by name
        // through a tunnel or a reverse proxy. Also every non-Flower host any
        // of these clients ever talks to.
        if (errors == SslPolicyErrors.None)
            return true;

        // Not "the chain is untrusted, therefore pin instead" - any failure at
        // all lands here, including a name mismatch, which is expected: a
        // certificate minted for a machine name is dialled at a bare IP as
        // often as not. The pin subsumes both, because a matching key is a
        // stronger statement than either check it replaces.
        if (certificate == null)
            return false;

        var publicKey = DeviceCertificate.PublicKeyRawOf(certificate);
        return publicKey != null && IsPinnedServerKey?.Invoke(publicKey) == true;
    }
}
