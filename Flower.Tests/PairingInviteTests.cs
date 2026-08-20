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
