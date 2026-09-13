using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;


namespace Flower.Server.Tests;

// The security-critical services had no coverage at all before this project
// existed (ARCHITECTURE-REVIEW Tier 5.1) - these are the "a wrong answer here
// lets a stranger in" paths, so they get tested for what they reject as much
// as for what they accept.

public class PairingCodeServiceTests
{
    [Fact]
    public void A_generated_code_can_be_consumed_exactly_once()
    {
        var service = new PairingCodeService();
        var (code, expiresAt) = service.GenerateCode();

        Assert.True(expiresAt > DateTimeOffset.UtcNow);
        Assert.True(service.TryConsume(code, out _));
        // Single-use is the entire security property: a code overheard or
        // reused must not pair a second device.
        Assert.False(service.TryConsume(code, out _));
    }

    [Fact]
    public void An_ordinary_code_does_not_confer_admin()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode();

        Assert.True(service.TryConsume(code, out var grantsAdmin));
        Assert.False(grantsAdmin);
    }

    [Fact]
    public void An_admin_granting_code_reports_that_at_redemption()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode(grantsAdmin: true);

        Assert.True(service.TryConsume(code, out var grantsAdmin));
        Assert.True(grantsAdmin);
    }

    [Fact]
    public void A_rejected_code_never_reports_admin()
    {
        // grantsAdmin must be false on every failure path, not left at
        // whatever the caller passed in - a caller that ignores the return
        // value must still not end up granting anything.
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode(grantsAdmin: true);
        Assert.True(service.TryConsume(code, out _));

        Assert.False(service.TryConsume(code, out var replayed));
        Assert.False(replayed);
        Assert.False(service.TryConsume("NOTACODE", out var unknown));
        Assert.False(unknown);
    }

    [Fact]
    public void Consumption_is_case_whitespace_and_separator_insensitive()
    {
        var service = new PairingCodeService();
        var (code, _) = service.GenerateCode();

        // The code is read off a screen and typed in by hand, sometimes copied
        // with the dashes the admin UI groups it with.
        var typed = $"  {code[..4].ToLowerInvariant()}-{code[4..].ToLowerInvariant()}  ";
        Assert.True(service.TryConsume(typed, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NOTACODE")]
    public void An_unknown_code_is_rejected(string? code)
    {
        Assert.False(new PairingCodeService().TryConsume(code, out _));
    }

    [Fact]
    public void Codes_avoid_visually_ambiguous_characters()
    {
        var service = new PairingCodeService();

        // 0/O and 1/I are excluded so an operator reading a code aloud or
        // off a screen can't produce an unredeemable one.
        for (var i = 0; i < 200; i++)
        {
            var (code, _) = service.GenerateCode();
            Assert.Equal(5, code.Length);
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I');
            Assert.All(code, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }
    }

    [Fact]
    public void Outstanding_codes_are_independent_of_each_other()
    {
        var service = new PairingCodeService();
        var (first, _) = service.GenerateCode();
        var (second, _) = service.GenerateCode(grantsAdmin: true);

        Assert.NotEqual(first, second);
        Assert.True(service.TryConsume(first, out var firstAdmin));
        Assert.False(firstAdmin);
        // Burning one code must not invalidate another still-outstanding one,
        // nor leak its grant into the other's answer.
        Assert.True(service.TryConsume(second, out var secondAdmin));
        Assert.True(secondAdmin);
    }
}

public class StreamTicketServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_ticket_redeems_for_the_track_it_was_minted_for()
    {
        var service = new StreamTicketService();
        var (ticket, expiresAt) = service.Issue("track-1", "fingerprint-a");

        Assert.True(expiresAt > Now);
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
    }

    [Fact]
    public void A_ticket_is_reusable_within_its_lifetime()
    {
        // Unlike a pairing code: one playback is many requests (an initial
        // probe, then a range request per seek), so burning the ticket on
        // first use would break playback immediately.
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.True(service.TryRedeem(ticket, "track-1", Now));
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
        Assert.True(service.TryRedeem(ticket, "track-1", Now));
    }

    [Fact]
    public void A_ticket_does_not_unlock_a_different_track()
    {
        // The whole reason a ticket is acceptable as a bearer token in a URL:
        // it is a key to one track, not to the library.
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, "track-2", Now));
    }

    [Fact]
    public void An_expired_ticket_is_rejected()
    {
        var service = new StreamTicketService();
        var (ticket, expiresAt) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, "track-1", expiresAt.AddSeconds(1)));
    }

    [Theory]
    [InlineData("", "track-1")]
    [InlineData(null, "track-1")]
    [InlineData("not-a-ticket", "track-1")]
    public void An_unknown_ticket_is_rejected(string? ticket, string trackId)
    {
        Assert.False(new StreamTicketService().TryRedeem(ticket, trackId, Now));
    }

    [Fact]
    public void A_ticket_without_a_track_id_is_rejected()
    {
        var service = new StreamTicketService();
        var (ticket, _) = service.Issue("track-1", "fingerprint-a");

        Assert.False(service.TryRedeem(ticket, null, Now));
        Assert.False(service.TryRedeem(ticket, "", Now));
    }

    [Fact]
    public void Revoking_a_peer_invalidates_the_tickets_it_minted_and_no_others()
    {
        // Otherwise "revoke this device" would leave its already-minted stream
        // URLs playable for the rest of their lifetime, which makes the revoke
        // button a promise the server doesn't keep.
        var service = new StreamTicketService();
        var (revoked, _) = service.Issue("track-1", "fingerprint-a");
        var (kept, _) = service.Issue("track-2", "fingerprint-b");

        Assert.Equal(1, service.RevokeFor("fingerprint-a"));
        Assert.False(service.TryRedeem(revoked, "track-1", Now));
        Assert.True(service.TryRedeem(kept, "track-2", Now));
    }

    [Fact]
    public void Every_issued_ticket_is_distinct()
    {
        var service = new StreamTicketService();
        var tickets = Enumerable.Range(0, 50).Select(_ => service.Issue("track-1", "fp").Ticket).ToList();

        Assert.Equal(tickets.Count, tickets.Distinct().Count());
        // 32 random bytes, hex-encoded.
        Assert.All(tickets, t => Assert.Equal(64, t.Length));
    }
}
