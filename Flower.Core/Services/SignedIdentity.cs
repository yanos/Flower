using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Flower.Services;

// Values stay raw until the header transport encodes them. The query transport
// applies its own URI encoding to the same values.
public static class SignedIdentity
{
    public const string PairingPath = "/api/flower/v1/pair-redeem";

    // Every name a signed identity travels under, as a header or in the
    // query (the two transports above).
    public const string HeaderPrefix = "X-Flower-";
    public const string FingerprintHeader = HeaderPrefix + "Fingerprint";
    public const string AliasHeader = HeaderPrefix + "Alias";
    public const string PublicKeyHeader = HeaderPrefix + "PublicKey";
    public const string SignatureHeader = HeaderPrefix + "Signature";
    public const string TimestampHeader = HeaderPrefix + "Timestamp";
    public const string NonceHeader = HeaderPrefix + "Nonce";
    public const string PairingCodeHeader = HeaderPrefix + "PairingCode";

    public static IReadOnlyList<(string Key, string Value)> Create(
        string fingerprint, string alias, string publicKey,
        (string Signature, string Timestamp, string Nonce) signed) =>
    [
        (FingerprintHeader, fingerprint),
        (AliasHeader, alias),
        (PublicKeyHeader, publicKey),
        (SignatureHeader, signed.Signature),
        (TimestampHeader, signed.Timestamp),
        (NonceHeader, signed.Nonce),
    ];

    public static HttpRequestMessage PairingRequest(
        Uri origin, string code, IReadOnlyList<(string Key, string Value)> credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, PairingPath));
        AddHeaders(request, [.. credentials, (PairingCodeHeader, code)]);
        return request;
    }

    // Who a device says it is, proving nothing: what /info is sent by a device
    // with no credentials to sign with yet.
    public static void AddClaim(HttpRequestMessage request, string fingerprint, string alias) =>
        AddHeaders(request, [(FingerprintHeader, fingerprint), (AliasHeader, alias)]);

    // The header transport. A header is ASCII and an alias is typed by a
    // person, so every value is percent-encoded (IdentityHeaderEncoding) -
    // an accent left raw throws in HttpClient before the request is sent.
    // The query transport escapes for itself and must not come through here.
    public static void AddHeaders(HttpRequestMessage request, IEnumerable<(string Key, string Value)> identity)
    {
        foreach (var (key, value) in identity)
            request.Headers.TryAddWithoutValidation(key, IdentityHeaderEncoding.Encode(value));
    }
}
