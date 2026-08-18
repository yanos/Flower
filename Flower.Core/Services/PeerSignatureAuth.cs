using System;
using System.Collections.Generic;

namespace Flower.Services;

// One signed request, decoupled from whichever HTTP stack delivered it -
// HttpListener in SyncHttpServer, Kestrel/Minimal-API in Flower.Server. Both
// stacks describe the same five things; only the accessors differ, so only
// the accessors are per-stack (see SyncHttpServer.ToSignedRequest and
// Flower.Server's DeviceSignatureAuth.ToSignedRequest, both one method).
//
// Query is materialized rather than lazy because it is enumerated more than
// once (identity lookup below, then the canonical string) and because a
// signed request must be verified against exactly the bytes it arrived with.
public sealed class SignedRequest
{
    private readonly Func<string, string?> _header;

    public SignedRequest(string method, string path, IReadOnlyList<(string Key, string Value)> query, byte[] body, Func<string, string?> header)
    {
        Method = method;
        Path = path;
        Query = query;
        Body = body;
        _header = header;
    }

    public string Method { get; }
    public string Path { get; }
    public IReadOnlyList<(string Key, string Value)> Query { get; }
    public byte[] Body { get; }

    // Header if present, else the same name as a query param - see
    // OpenSubsonicClient.BuildUrl's own doc comment: a URL handed to
    // something else to fetch (LibVLC playing GetStreamUrl directly) can't
    // carry custom headers, so the identity (and the signature, timestamp,
    // nonce and public key with it) travels as a query param there instead.
    // Header wins when both are somehow present. This fallback *policy* is
    // the part that must not be written twice - it decides what an attacker
    // is allowed to put where.
    public string? Identity(string name)
    {
        if (_header(name) is { Length: > 0 } header)
            return header;

        foreach (var (key, value) in Query)
        {
            if (key == name)
                return value;
        }

        return null;
    }
}

// The two device-identity checks the sync protocol defines, shared by both
// servers. These were hand-copied between SyncHttpServer and Flower.Server's
// DeviceSignatureAuth, down to the header/query fallback helper - which for
// security-critical code means a fix to one silently leaves the other wrong
// (ARCHITECTURE-REVIEW Tier 2.2).
public static class PeerSignatureAuth
{
    // Proof-of-possession, for pair-request/unpair-notify/pair-redeem: the
    // caller must hold the private key matching the public key it's offering,
    // and the fingerprint it claims must actually be that key's hash. Verified
    // against the offered key itself rather than a trust-store lookup, since
    // for a not-yet-paired device there is nothing to look up yet. Returns the
    // verified fingerprint, or null on any failure.
    public static string? VerifySelfSigned(SignedRequest request, NonceReplayGuard replayGuard, DateTimeOffset now)
    {
        var fingerprint = request.Identity("X-Flower-Fingerprint");
        var publicKeyBase64 = request.Identity("X-Flower-PublicKey");
        if (string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(publicKeyBase64))
            return null;

        byte[] publicKeyRaw;
        try
        {
            publicKeyRaw = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (publicKeyRaw.Length != 65 || publicKeyRaw[0] != 0x04)
            return null;
        if (SignedRequestCanonicalizer.ComputeFingerprint(publicKeyRaw) != fingerprint)
            return null;

        return Verify(request, publicKeyBase64, fingerprint, replayGuard, now);
    }

    // The gated-endpoint check: the caller must be signature-verified against
    // the public key that was captured for this fingerprint at the moment it
    // was actually approved - never a cached /info value, which is why the key
    // arrives as a lookup over the trust store rather than off the request.
    // A fingerprint with no key on file fails exactly like an outright
    // stranger. Returns the verified fingerprint, or null.
    public static string? VerifyTrustedPeer(SignedRequest request, Func<string, string?> publicKeyForFingerprint, NonceReplayGuard replayGuard, DateTimeOffset now)
    {
        var fingerprint = request.Identity("X-Flower-Fingerprint");
        if (string.IsNullOrEmpty(fingerprint))
            return null;

        var publicKey = publicKeyForFingerprint(fingerprint);
        if (publicKey == null)
            return null;

        return Verify(request, publicKey, fingerprint, replayGuard, now);
    }

    private static string? Verify(SignedRequest request, string publicKeyBase64, string fingerprint, NonceReplayGuard replayGuard, DateTimeOffset now)
    {
        var verified = SignatureVerifier.Verify(
            request.Method, request.Path, request.Query, request.Body,
            request.Identity("X-Flower-Timestamp"), request.Identity("X-Flower-Nonce"),
            request.Identity("X-Flower-Signature"), publicKeyBase64,
            now, replayGuard, fingerprint);

        return verified ? fingerprint : null;
    }
}
