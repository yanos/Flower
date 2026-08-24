using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Flower.Services;

// The bridge between the device keypair this codebase already has and the TLS
// certificate a server needs in order to be reached over https.
//
// The point of routing one through the other is that it makes the certificate
// carry no new trust at all. A client that has paired with a server already
// stores that server's public key (TrustedPeerStore.TrustedPeer.PublicKey,
// obtained by redeeming a pairing code and pinned by fingerprint since
// PairingInvite was introduced). If the certificate the server presents is
// built from that same key, then "is this the server I paired with?" is a byte
// comparison against something already on disk - no certificate authority, no
// second fingerprint in the invite, no extra field in any store, and nothing
// for the operator to configure. See docs/REMOTE-TRANSPORT-PLAN.md's
// certificate section, which decided the shape; this is where it lands.
//
// A real, publicly-trusted certificate remains the optional upgrade for the two
// callers that cannot pin - a browser tab and a third-party OpenSubsonic client
// - and needs none of this: it arrives as a file and is validated the ordinary
// way. See docs/SELF-HOSTING.md.
public static class DeviceCertificate
{
    // The raw uncompressed SEC1 point (0x04 || X(32) || Y(32)) that this
    // codebase uses as a public key everywhere it travels - DeviceKeyStore
    // writes this form, SignatureVerifier.TryParsePublicKey reads it, and
    // TrustedPeer.PublicKey is its base64. Kept here so the certificate path
    // and the signing path cannot drift into two different encodings of the
    // same key and silently fail to match.
    public static byte[] PublicKeyRaw(ECDsa ecdsa)
    {
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return raw;
    }

    // The same value read back off a certificate, or null when the certificate
    // is not one of ours to begin with - an RSA certificate from a real CA, or
    // a curve other than P-256. Null is an ordinary answer here, not a fault:
    // the caller's next move is to fall back to ordinary chain validation.
    public static byte[]? PublicKeyRawOf(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa == null)
            return null;

        var parameters = ecdsa.ExportParameters(false);
        // A P-384 or P-521 key would export X and Y of a different length, and
        // packing those into the 65-byte P-256 layout would either throw or
        // quietly produce a value that matches nothing. Neither is worth
        // risking for a certificate that cannot have come from DeviceKeyStore.
        if (parameters.Q.X is not { Length: 32 } || parameters.Q.Y is not { Length: 32 })
            return null;

        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(parameters.Q.X, 0, raw, 1, 32);
        Buffer.BlockCopy(parameters.Q.Y, 0, raw, 33, 32);
        return raw;
    }

    // A self-signed certificate over the device's own signing key, covering
    // every name and address a client might dial this server on.
    //
    // The subject alternative names are a courtesy rather than the security
    // boundary - a pinning client compares the key and does not care what the
    // certificate claims to be. They matter for the *other* direction: a stack
    // that validates normally (curl, a browser being clicked through, a
    // third-party client with the certificate manually trusted) rejects a name
    // mismatch outright, so leaving them off would turn a warning into a hard
    // failure for anyone taking that route.
    public static X509Certificate2 CreateSelfSigned(
        ECDsa key, string commonName, IEnumerable<string> dnsNames, IEnumerable<IPAddress> ipAddresses,
        DateTimeOffset? notBefore = null, TimeSpan? lifetime = null)
    {
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}"), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        var any = false;
        foreach (var dnsName in dnsNames)
        {
            subjectAlternativeNames.AddDnsName(dnsName);
            any = true;
        }
        foreach (var ipAddress in ipAddresses)
        {
            subjectAlternativeNames.AddIpAddress(ipAddress);
            any = true;
        }
        // A certificate with an empty SAN extension is worse than one with no
        // SAN extension at all - some stacks treat the empty list as "matches
        // nothing" rather than falling back to the common name.
        if (any)
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var from = notBefore ?? DateTimeOffset.UtcNow;
        // Backdated an hour so a client whose clock is behind the server's does
        // not reject a certificate minted seconds ago as not yet valid. The
        // signed-request path already carries the same allowance for the same
        // reason (SignatureVerifier's timestamp window).
        return request.CreateSelfSigned(from.AddHours(-1), from + (lifetime ?? TimeSpan.FromDays(3650)));
    }
}
