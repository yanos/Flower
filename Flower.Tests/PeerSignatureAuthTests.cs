using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Direct coverage of the check both servers now share (Tier 2.2). The
// end-to-end behaviour is asserted over a real socket in
// SyncHttpServerRoundTripTests and through the real route table in
// Flower.Server.Tests; what those two cannot show is that the *same* code is
// answering, which is the entire point of the extraction - so the cases here
// are the ones that used to have two implementations to keep in step,
// especially the header-else-query rule that decides where an attacker is
// allowed to put an identity.
public class PeerSignatureAuthTests
{
    private const string Path = "/api/flower/v1/pair-request";

    private static SignedRequest Request(
        DeviceSigningKey key,
        IEnumerable<(string Key, string Value)>? query = null,
        byte[]? body = null,
        string? fingerprintOverride = null,
        string? publicKeyOverride = null,
        bool identityInQuery = false)
    {
        var pairs = (query ?? []).ToList();
        var bytes = body ?? [];
        var (signature, timestamp, nonce) = key.Sign("POST", Path, pairs, bytes);

        var identity = new Dictionary<string, string>
        {
            ["X-Flower-Fingerprint"] = fingerprintOverride ?? key.Fingerprint,
            ["X-Flower-PublicKey"] = publicKeyOverride ?? key.PublicKeyBase64,
            ["X-Flower-Signature"] = signature,
            ["X-Flower-Timestamp"] = timestamp,
            ["X-Flower-Nonce"] = nonce,
        };

        // The signature covers the query, so identity params moved into the
        // URL have to be there *before* it is computed - which is why the
        // caller asks for this shape up front rather than after the fact.
        if (identityInQuery)
        {
            pairs.AddRange(identity.Select(kv => (kv.Key, kv.Value)));
            (signature, timestamp, nonce) = key.Sign("POST", Path, pairs, bytes);
            var index = pairs.FindIndex(p => p.Key == "X-Flower-Signature");
            pairs[index] = ("X-Flower-Signature", signature);
            pairs[pairs.FindIndex(p => p.Key == "X-Flower-Timestamp")] = ("X-Flower-Timestamp", timestamp);
            pairs[pairs.FindIndex(p => p.Key == "X-Flower-Nonce")] = ("X-Flower-Nonce", nonce);
            return new SignedRequest("POST", Path, pairs, bytes, _ => null);
        }

        return new SignedRequest("POST", Path, pairs, bytes, name => identity.GetValueOrDefault(name));
    }

    [Fact]
    public void A_correctly_self_signed_request_yields_its_fingerprint()
    {
        var key = TestSigningKey.Create();

        Assert.Equal(key.Fingerprint,
            PeerSignatureAuth.VerifySelfSigned(Request(key), new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    // The identity may travel in the URL instead of headers, because a URL
    // handed to LibVLC to fetch can't carry headers at all (see
    // SignedRequest.Identity). That path has to verify exactly as well as the
    // header one - not merely be accepted.
    [Fact]
    public void An_identity_supplied_entirely_in_the_query_verifies_the_same_way()
    {
        var key = TestSigningKey.Create();

        Assert.Equal(key.Fingerprint,
            PeerSignatureAuth.VerifySelfSigned(Request(key, identityInQuery: true), new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    // A caller that puts a *different* value in the query than in the header
    // must not be able to choose which one gets checked.
    [Fact]
    public void A_header_wins_over_a_query_param_of_the_same_name()
    {
        var key = TestSigningKey.Create();
        var request = Request(key, query: [("X-Flower-Fingerprint", "some-other-device")]);

        Assert.Equal(key.Fingerprint,
            PeerSignatureAuth.VerifySelfSigned(request, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_fingerprint_that_is_not_the_hash_of_the_offered_key_is_refused()
    {
        var key = TestSigningKey.Create();
        var other = TestSigningKey.Create();
        var request = Request(key, fingerprintOverride: other.Fingerprint);

        Assert.Null(PeerSignatureAuth.VerifySelfSigned(request, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]                    // valid base64, wrong length
    public void A_malformed_public_key_is_refused_rather_than_thrown_on(string publicKey)
    {
        var key = TestSigningKey.Create();
        var request = Request(key, publicKeyOverride: publicKey);

        Assert.Null(PeerSignatureAuth.VerifySelfSigned(request, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_trusted_peer_is_verified_against_the_key_on_file_not_the_one_it_offers()
    {
        var key = TestSigningKey.Create();
        var impostor = TestSigningKey.Create();

        // Signed by the impostor, but claiming the trusted device's
        // fingerprint - the store's key is what decides.
        var request = Request(impostor, fingerprintOverride: key.Fingerprint);

        Assert.Null(PeerSignatureAuth.VerifyTrustedPeer(
            request, _ => key.PublicKeyBase64, new NonceReplayGuard(), DateTimeOffset.UtcNow));
        Assert.Equal(key.Fingerprint, PeerSignatureAuth.VerifyTrustedPeer(
            Request(key), _ => key.PublicKeyBase64, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_fingerprint_with_no_key_on_file_fails_like_a_stranger()
    {
        var key = TestSigningKey.Create();

        Assert.Null(PeerSignatureAuth.VerifyTrustedPeer(
            Request(key), _ => null, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_replayed_nonce_is_refused_the_second_time()
    {
        var key = TestSigningKey.Create();
        var guard = new NonceReplayGuard();
        var request = Request(key);

        Assert.NotNull(PeerSignatureAuth.VerifySelfSigned(request, guard, DateTimeOffset.UtcNow));
        Assert.Null(PeerSignatureAuth.VerifySelfSigned(request, guard, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_request_outside_the_clock_skew_window_is_refused()
    {
        var key = TestSigningKey.Create();
        var request = Request(key);

        Assert.Null(PeerSignatureAuth.VerifySelfSigned(
            request, new NonceReplayGuard(), DateTimeOffset.UtcNow + SignatureVerifier.ClockSkewWindow + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_body_the_signature_was_not_made_for_is_refused()
    {
        var key = TestSigningKey.Create();
        var signed = Request(key, body: "{\"alias\":\"Phone\"}"u8.ToArray());
        var tampered = new SignedRequest(signed.Method, signed.Path, signed.Query, "{\"alias\":\"Laptop\"}"u8.ToArray(),
            signed.Identity);

        Assert.Null(PeerSignatureAuth.VerifySelfSigned(tampered, new NonceReplayGuard(), DateTimeOffset.UtcNow));
    }
}
