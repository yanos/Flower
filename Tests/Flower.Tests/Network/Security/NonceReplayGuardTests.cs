using System;

using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Network.Security;

// The replay guard is written to before a signature is checked, so a caller
// who cannot sign chooses its keys - docs/CODE-REVIEW-2026-09.md B2 and C4.
public class NonceReplayGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_nonce_is_refused_the_second_time_for_the_same_device_only()
    {
        var guard = new NonceReplayGuard();

        Assert.True(guard.TryRecord("fp-1", "nonce", Start));
        Assert.False(guard.TryRecord("fp-1", "nonce", Start));
        Assert.True(guard.TryRecord("fp-2", "nonce", Start));
    }

    // Throttled, so not every insert walks the whole dictionary - but still
    // swept, or the throttle would be a leak.
    [Fact]
    public void Expired_nonces_are_still_swept_out()
    {
        var guard = new NonceReplayGuard();
        for (var i = 0; i < 100; i++)
            guard.TryRecord("fp-1", $"nonce-{i}", Start);

        guard.TryRecord("fp-1", "later", Start.AddMinutes(5));

        Assert.Equal(1, guard.Count);
    }

    [Fact]
    public void An_oversized_nonce_is_never_stored()
    {
        var guard = new NonceReplayGuard();

        Assert.False(guard.TryRecord("fp-1", new string('n', 30_000), Start));
        Assert.Equal(0, guard.Count);
    }

    // Refused as malformed, not as a replay: the refusal names the reason, and
    // this one is not that the nonce was seen.
    [Fact]
    public void The_verifier_refuses_an_oversized_nonce_before_recording_it()
    {
        var signer = TestSigningKey.Create();
        var guard = new NonceReplayGuard();
        var (signature, timestamp, _) = signer.Sign("GET", "/api/flower/v1/library", [], []);

        var check = SignatureVerifier.Check(
            "GET", "/api/flower/v1/library", [], [],
            timestamp, new string('n', 30_000), signature, signer.PublicKeyBase64,
            DateTimeOffset.UtcNow, guard, signer.Fingerprint);

        Assert.Equal(SignatureCheck.Invalid, check);
        Assert.Equal(0, guard.Count);
    }
}
