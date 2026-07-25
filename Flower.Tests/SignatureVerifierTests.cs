using System;
using System.Security.Cryptography;

using Flower.Services;

namespace Flower.Tests;

public class SignatureVerifierTests
{
    private static (DeviceSigningKey Signer, string PublicKeyBase64) MakeSigner()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        var signer = new DeviceSigningKey(ecdsa, raw);
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
