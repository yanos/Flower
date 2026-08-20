using System;
using System.Security.Cryptography;

using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

public class SignatureVerifierTests
{
    private static (DeviceSigningKey Signer, string PublicKeyBase64) MakeSigner()
    {
        var signer = TestSigningKey.Create();
        return (signer, signer.PublicKeyBase64);
    }

    [Fact]
    public void ComputeFingerprint_is_deterministic_and_16_bytes_hex()
    {
        var (_, publicKeyBase64) = MakeSigner();
        var raw = Convert.FromBase64String(publicKeyBase64);

        var fp1 = SignedRequestCanonicalizer.ComputeFingerprint(raw);
        var fp2 = SignedRequestCanonicalizer.ComputeFingerprint(raw);

        Assert.Equal(fp1, fp2);
        Assert.Equal(32, fp1.Length); // 16 bytes, hex-encoded
    }

    [Fact]
    public void Sign_then_Verify_round_trips()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var body = "{\"hello\":\"world\"}"u8.ToArray();
        var (signature, timestamp, nonce) = signer.Sign("POST", "/api/flower/v1/playlists/apply", [], body);
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "POST", "/api/flower/v1/playlists/apply", [], body,
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.True(ok);
    }

    // The regression behind "Could not reach <server>: Wrong username or
    // password": a caller signs its identity params (see
    // PeerOpenSubsonicClientFactory) regardless of how they travel, but a
    // server only sees them in the query when the caller put them there
    // (BuildUrl's LibVLC case) - never when they went out as headers
    // (SendAsync). So the canonical query must ignore X-Flower-* entirely,
    // making both transports verify against the same bytes.
    [Fact]
    public void A_signature_over_identity_params_verifies_whether_they_travel_as_headers_or_query()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        (string Key, string Value)[] request = [("id", "42"), ("f", "json")];
        (string Key, string Value)[] identity =
        [
            ("X-Flower-Fingerprint", signer.Fingerprint),
            ("X-Flower-PublicKey", publicKeyBase64),
        ];
        var (signature, timestamp, nonce) = signer.Sign("GET", "/rest/stream", [.. request, .. identity], []);

        // Headers: the server's query has the request params only.
        var asHeaders = SignatureVerifier.Verify(
            "GET", "/rest/stream", request, [],
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, new NonceReplayGuard(), signer.Fingerprint);

        // Query string: the same identity, plus the signature params, all in
        // the URL the server parses.
        var asQuery = SignatureVerifier.Verify(
            "GET", "/rest/stream",
            [.. request, .. identity, ("X-Flower-Signature", signature), ("X-Flower-Timestamp", timestamp), ("X-Flower-Nonce", nonce)],
            [], timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, new NonceReplayGuard(), signer.Fingerprint);

        Assert.True(asHeaders);
        Assert.True(asQuery);
    }

    [Fact]
    public void Verify_fails_when_the_method_differs_from_what_was_signed()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("GET", "/rest/stream", [], []);
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "POST", "/rest/stream", [], [],
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_fails_when_the_path_differs_from_what_was_signed()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("GET", "/rest/stream", [], []);
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "GET", "/rest/getCoverArt", [], [],
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_fails_when_a_query_parameter_is_tampered_with()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("GET", "/rest/stream", [("id", "track-1")], []);
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "GET", "/rest/stream", [("id", "track-2")], [],
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_fails_when_the_body_is_tampered_with()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("POST", "/api/flower/v1/log/report", [], "original"u8.ToArray());
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "POST", "/api/flower/v1/log/report", [], "tampered"u8.ToArray(),
            timestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_fails_when_the_timestamp_is_outside_the_clock_skew_window()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, _, nonce) = signer.Sign("GET", "/api/flower/v1/library", [], []);
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString();
        var guard = new NonceReplayGuard();

        var ok = SignatureVerifier.Verify(
            "GET", "/api/flower/v1/library", [], [],
            staleTimestamp, nonce, signature, publicKeyBase64,
            DateTimeOffset.UtcNow, guard, "fp-1");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_fails_on_a_replayed_nonce_for_the_same_fingerprint()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("GET", "/api/flower/v1/playlists", [], []);
        var guard = new NonceReplayGuard();
        var now = DateTimeOffset.UtcNow;

        var first = SignatureVerifier.Verify("GET", "/api/flower/v1/playlists", [], [], timestamp, nonce, signature, publicKeyBase64, now, guard, "fp-1");
        var replay = SignatureVerifier.Verify("GET", "/api/flower/v1/playlists", [], [], timestamp, nonce, signature, publicKeyBase64, now, guard, "fp-1");

        Assert.True(first);
        Assert.False(replay);
    }

    [Fact]
    public void Verify_allows_the_same_nonce_string_from_a_different_fingerprint()
    {
        var (signer, publicKeyBase64) = MakeSigner();
        var (signature, timestamp, nonce) = signer.Sign("GET", "/api/flower/v1/playlists", [], []);
        var guard = new NonceReplayGuard();
        var now = DateTimeOffset.UtcNow;

        // Same nonce string, but recorded under two different fingerprints -
        // replay tracking is scoped per-fingerprint (see NonceReplayGuard),
        // so this must not collide with fp-1's own usage above.
        guard.TryRecord("fp-1", nonce, now);
        var forOtherFingerprint = guard.TryRecord("fp-2", nonce, now);

        Assert.True(forOtherFingerprint);
    }
}
