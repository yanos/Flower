using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;

namespace Flower.Services;

// A server's signature over its own answer - docs/TRUST-BOUNDARY-PLAN.md step 4.
//
// Every request a device makes is signed, and until this no answer was. That
// mattered for exactly one kind of answer: the ones that end a pairing. A
// client unpaired itself on /info saying trustsCaller: false, or on a refusal
// saying device-unknown, and neither was authenticated - /info is plain HTTP
// on a LAN, a status can come from anything on the path, and a Cloudflare
// tunnel ends TLS at Cloudflare, so even an https origin does not prove an
// answer came from Flower.Server. Anything that could answer at the server's
// address could unpair every listener.
//
// So the server signs those answers with the key whose hash is the
// fingerprint a client pinned at pairing (PairingEntry), and a client acts on
// one only when the signature checks out. The signed form is the request
// canonicalizer's own, so there is one canonical form rather than two:
//
//     RESPONSE
//     <the request's path>
//     status=<status>
//     <sha256 of the response body>
//     <the server's timestamp>
//     <the request's own X-Flower-Nonce>
//
// The request's nonce is what makes it an answer to *this* request: a client
// signs every attempt with a fresh one, so a "you are revoked" captured from
// some earlier exchange - before a re-pairing, say - matches nothing a client
// will ever send again. That is also why the timestamp is not checked against
// a clock: freshness comes from the nonce, and a client's clock is the thing
// most likely to be wrong.
public static class ServerResponseSignature
{
    public const string SignatureHeader = "X-Flower-Server-Signature";
    public const string TimestampHeader = "X-Flower-Server-Timestamp";

    private const string Method = "RESPONSE";

    public static byte[] Canonical(string requestPath, int status, byte[] body, string timestamp, string requestNonce) =>
        SignedRequestCanonicalizer.Build(Method, requestPath, [("status", status.ToString())], body, timestamp, requestNonce);

    // The check itself, from parts. The key must hash to the fingerprint the
    // caller expected - the one pinned at pairing - or nothing is believed:
    // anyone can sign with a key of their own.
    public static bool Verify(
        string requestPath, int status, byte[] body, string? timestamp, string? signatureBase64,
        string? requestNonce, string? publicKeyBase64, string? expectedFingerprint)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(requestNonce)
            || string.IsNullOrEmpty(publicKeyBase64) || string.IsNullOrEmpty(expectedFingerprint))
        {
            return false;
        }

        if (PairingEntry.FingerprintOf(publicKeyBase64) is not { } fingerprint
            || !string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!SignatureVerifier.TryParsePublicKey(publicKeyBase64, out var point))
            return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
        return ecdsa.VerifyData(
            Canonical(requestPath, status, body, timestamp, requestNonce),
            signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    // The same, read off a response a client received: the path and nonce
    // come from the request it answers (HttpResponseMessage.RequestMessage),
    // which the client signed itself, so neither is the server's to choose.
    // body is the response's bytes, already read.
    public static bool IsSignedBy(HttpResponseMessage response, byte[] body, string? publicKeyBase64, string? expectedFingerprint)
    {
        if (response.RequestMessage is not { RequestUri: { } uri } request)
            return false;

        return Verify(
            uri.AbsolutePath, (int)response.StatusCode, body,
            HeaderValue(response, TimestampHeader), HeaderValue(response, SignatureHeader),
            RequestNonce(request), publicKeyBase64, expectedFingerprint);
    }

    // The nonce a request was signed with: a header, percent-encoded
    // (IdentityHeaderEncoding), or a query parameter - the same
    // header-else-query rule the server reads it by.
    private static string? RequestNonce(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues("X-Flower-Nonce", out var values) && values.FirstOrDefault() is { Length: > 0 } header)
            return IdentityHeaderEncoding.Decode(header);

        foreach (var pair in request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && Uri.UnescapeDataString(pair[..separator]) == "X-Flower-Nonce")
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
