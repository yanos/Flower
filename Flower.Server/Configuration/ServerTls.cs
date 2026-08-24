using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Flower.Services;

namespace Flower.Server.Configuration;

// Where this server's https listener gets its certificate.
//
// Two sources, in priority order, and the first one is the whole reason this
// feature needs no configuration:
//
//   1. A certificate file the operator named (CertificatePath). This is the
//      real, publicly-trusted certificate - from Let's Encrypt, `tailscale
//      cert`, or a domain they own - and it is the only thing that satisfies
//      the two callers that cannot pin: a browser tab and a third-party
//      OpenSubsonic client. See docs/SELF-HOSTING.md.
//
//   2. Failing that, a self-signed certificate over this server's own device
//      key. No file, no authority, no domain, nothing to configure - and a
//      paired Flower client accepts it because it already holds that key. See
//      DeviceCertificate, which explains why that is a pin rather than a
//      shrug, and PeerHttpClient, which is the other half.
//
// Both land in the same place - a certificate Kestrel serves - which is what
// docs/REMOTE-TRANSPORT-PLAN.md's certificate section meant by "one mechanism
// with a configuration switch, not three code paths".
public static class ServerTls
{
    // Minted fresh on every start rather than cached to disk, which is a
    // deliberate trade rather than an oversight. The certificate carries no
    // trust of its own - the key inside it does, and that key is persistent
    // (DeviceKeyStore) - so a new certificate over the same key is the same
    // identity to every pinning client, and pins nothing differently. What it
    // buys is that the subject alternative names are always this machine's
    // current addresses: a laptop that moved networks, a container that got a
    // new bridge address, a machine that grew an interface. A cached file
    // would go stale there and fail validation for exactly the non-pinning
    // callers it was supposed to help.
    //
    // The cost is that a browser clicked through the interstitial gets asked
    // again after a restart, which is a browser those callers should be
    // reaching over a real certificate anyway.
    public static X509Certificate2 SelfSigned(ECDsa deviceKey, string commonName)
    {
        var certificate = DeviceCertificate.CreateSelfSigned(
            deviceKey,
            commonName,
            // "localhost" so that the machine running the server can reach its
            // own https listener by the name that makes a browser treat it as
            // a secure context, which is what crypto.subtle requires - the
            // same constraint WebUiHosting.BrowserOriginFor exists for.
            dnsNames: [commonName, "localhost"],
            ipAddresses: [IPAddress.Loopback, IPAddress.IPv6Loopback, .. LocalAddresses.Own()]);

        // Round-tripped through PKCS#12 rather than handed to Kestrel as
        // created. On Windows a certificate straight out of CreateSelfSigned
        // carries an ephemeral key that SChannel refuses to use for a TLS
        // server ("the credentials supplied to the package were not
        // recognized"), and the export/import is the standard way to get one
        // it will accept. It is a no-op everywhere else, so it is done
        // unconditionally rather than behind an OS check that would only be
        // exercised on the platform least likely to be tested here.
        var exported = certificate.Export(X509ContentType.Pkcs12);
        certificate.Dispose();
        return X509CertificateLoader.LoadPkcs12(exported, password: null);
    }

    // A PEM certificate/key pair, which is the shape everything that issues one
    // produces - certbot, `tailscale cert`, and Caddy's exported files alike.
    public static X509Certificate2 FromFiles(string certificatePath, string keyPath)
    {
        // Round-tripped for the same reason as above: X509Certificate2
        // .CreateFromPemFile also produces an ephemeral key.
        using var pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
