using Flower.Services;

namespace Flower.Tests;

// PairingInvite is the QR code's contents and the link a user pastes instead
// of scanning one - written by Flower.Server, read by every Flower head, so
// the round trip is the contract (SYNC-PLAN.md, "Passwordless by design").
public class PairingInviteTests
{
    [Fact]
    public void Round_trips_through_its_uri_form()
    {
        var invite = new PairingInvite("100.64.1.2:4533", "K7M2P9QX", "ab12cd34");

        var parsed = PairingInvite.TryParse(invite.ToString());

        Assert.Equal(invite, parsed);
    }

    [Fact]
    public void Carries_the_server_fingerprint()
    {
        // The field that makes the QR a mutual bootstrap rather than a
        // one-directional one: without it the new device has no way to know
        // which server it just handed its public key to.
        var text = new PairingInvite("host:4533", "CODE1234", "server-fingerprint").ToString();

        Assert.Contains("fp=server-fingerprint", text);
    }

    [Fact]
    public void Escapes_values_that_would_otherwise_break_the_query()
    {
        var invite = new PairingInvite("host name:4533", "CODE&MORE", "fp=weird");

        Assert.Equal(invite, PairingInvite.TryParse(invite.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri at all")]
    [InlineData("https://example.com/pair?host=h&code=c&fp=f")]      // wrong scheme
    [InlineData("flower://join?host=h&code=c&fp=f")]                  // wrong host
    public void Rejects_anything_that_is_not_a_pairing_link(string? text)
    {
        Assert.Null(PairingInvite.TryParse(text));
    }

    [Theory]
    [InlineData("flower://pair?code=c&fp=f")]   // no host
    [InlineData("flower://pair?host=h&fp=f")]   // no code
    [InlineData("flower://pair?host=h&code=c")] // no fingerprint
    public void Rejects_an_invite_missing_any_required_field(string text)
    {
        // The fingerprint case is the load-bearing one: an invite without it
        // could only be completed by trusting whatever answers at that
        // address, and silently degrading to trust-on-first-use is exactly
        // what the field exists to prevent.
        Assert.Null(PairingInvite.TryParse(text));
    }

    [Fact]
    public void Tolerates_surrounding_whitespace_from_a_paste()
    {
        var invite = new PairingInvite("host:4533", "CODE1234", "fp");

        Assert.Equal(invite, PairingInvite.TryParse($"  {invite}\n"));
    }
}

// PairingEntry is the client half of the fingerprint the invite carries. The
// server has always minted it (AdminEndpoints.BuildInvite, Program.cs's startup
// banner); until this existed nothing read it back, so pairing was
// trust-on-first-use over whatever answered at the address - the exact failure
// PairingInvite's own comment says the field exists to prevent.
public class PairingEntryTests
{
    // A real P-256 point, so the fingerprint under test is computed the same
    // way SignedRequestCanonicalizer computes every other one rather than over
    // bytes that only look like a key.
    private static string KeyOf(int seed)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        System.Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        System.Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return System.Convert.ToBase64String(raw);
    }

    private static string FingerprintOf(string keyBase64) =>
        SignedRequestCanonicalizer.ComputeFingerprint(System.Convert.FromBase64String(keyBase64));

    [Fact]
    public void A_bare_code_carries_no_expectation()
    {
        var entry = PairingEntry.Parse("  K7M2P  ");

        Assert.Equal("K7M2P", entry.Code);
        Assert.Null(entry.ExpectedFingerprint);
    }

    [Fact]
    public void An_invite_carries_the_code_and_the_fingerprint_it_named()
    {
        var entry = PairingEntry.Parse(new PairingInvite("host:4533", "K7M2P", "abc123").ToString());

        Assert.Equal("K7M2P", entry.Code);
        Assert.Equal("abc123", entry.ExpectedFingerprint);
    }

    [Fact]
    public void A_bare_code_accepts_whatever_key_answered()
    {
        // Not an endorsement of the shape, just what it is: five characters
        // read down a phone line say nothing about which server should answer,
        // so this path stays trust-on-first-use and the invite is the upgrade.
        Assert.Null(PairingEntry.Parse("K7M2P").RejectionFor(KeyOf(1)));
    }

    [Fact]
    public void An_invite_accepts_the_server_whose_key_matches()
    {
        var key = KeyOf(2);
        var entry = PairingEntry.Parse(new PairingInvite("h", "C", FingerprintOf(key)).ToString());

        Assert.Null(entry.RejectionFor(key));
    }

    [Fact]
    public void An_invite_refuses_a_server_serving_a_different_key()
    {
        // The machine-in-the-middle case, and the whole reason the field is on
        // the wire: the impostor serves its own key over the same plain-HTTP
        // connection the real key would have arrived on, so the only thing
        // that can tell them apart is the fingerprint that travelled by QR.
        var entry = PairingEntry.Parse(new PairingInvite("h", "C", FingerprintOf(KeyOf(3))).ToString());

        var rejection = entry.RejectionFor(KeyOf(4));

        Assert.NotNull(rejection);
        Assert.Contains("not the server that invite came from", rejection);
    }

    [Fact]
    public void A_matching_fingerprint_is_compared_without_regard_to_case()
    {
        var key = KeyOf(5);
        var entry = new PairingEntry("C", FingerprintOf(key).ToUpperInvariant());

        Assert.Null(entry.RejectionFor(key));
    }

    [Fact]
    public void An_invite_refuses_a_server_that_has_served_no_key_yet()
    {
        // Refused rather than waved through. An invite naming a fingerprint
        // asked for a check, and skipping it because the thing to check has
        // not arrived is how the check stops existing.
        var entry = new PairingEntry("C", "abc123");

        Assert.NotNull(entry.RejectionFor(""));
    }

    [Fact]
    public void An_invite_refuses_a_key_that_is_not_a_key()
    {
        Assert.NotNull(new PairingEntry("C", "abc123").RejectionFor("not base64 at all!"));
    }

    [Fact]
    public void FingerprintOf_agrees_with_the_canonicalizer_and_rejects_non_keys()
    {
        var key = KeyOf(6);

        Assert.Equal(FingerprintOf(key), PairingEntry.FingerprintOf(key));
        Assert.Null(PairingEntry.FingerprintOf("not base64 at all!"));
    }
}
