using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flower.Server.Services;

// Short-lived capability URLs for the in-browser player (SYNC-PLAN.md,
// "The in-browser player: stream tickets").
//
// Path A gives the browser a real device keypair, so it signs its API calls
// exactly like a phone does. What it cannot do is sign the request an
// <audio src="..."> element issues on its own: that fetch is made by the
// browser's media stack, and there is no hook to attach an X-Flower-Signature
// header to it. Note this is a property of the media element, not of how the
// browser authenticates - the passkey design this replaced needed the same
// bridge, so unifying on keys neither created nor removed this work.
//
// So the player makes one normally-signed call to mint a ticket, then puts the
// ticket in the media URL's query string. The ticket is a bearer token by
// necessity, which is why it is narrow in all three dimensions that matter:
// bound to one track id, expiring in minutes, and traceable to the peer that
// minted it.
//
// Deliberately *not* single-use, unlike a pairing code. A media element issues
// many requests for one playback - an initial probe, then a range request per
// seek - and burning the ticket on the first would break playback immediately.
// Bounded lifetime is what limits it instead.
public sealed class StreamTicketService
{
    // Long enough to cover playing a track through and seeking around inside
    // it, short enough that a leaked media URL is worthless well before anyone
    // could pass it along. Playback of a longer track re-mints transparently:
    // the player holds a signing key, so minting is a background call and not
    // something the user experiences.
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();

    private sealed record Ticket(string TrackId, string Fingerprint, DateTimeOffset ExpiresAt);

    public (string Ticket, DateTimeOffset ExpiresAt) Issue(string trackId, string fingerprint)
    {
        Prune();
        var value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow + TicketLifetime;
        _tickets[value] = new Ticket(trackId, fingerprint, expiresAt);
        return (value, expiresAt);
    }

    // The track id check is the whole point: a ticket minted for one track must
    // not become a general-purpose key to the library, so an otherwise-valid
    // ticket presented against a different id fails exactly like a forged one.
    public bool TryRedeem(string? ticket, string? trackId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket) || string.IsNullOrEmpty(trackId))
            return false;
        if (!_tickets.TryGetValue(ticket, out var entry))
            return false;
        if (entry.ExpiresAt <= now)
            return false;

        return string.Equals(entry.TrackId, trackId, StringComparison.Ordinal);
    }

    // Called when a peer is revoked: the peer's signing key stops working
    // immediately, but any ticket it already minted would otherwise stay good
    // for the rest of its lifetime, which would make "revoke this device" a
    // promise the server doesn't quite keep.
    public int RevokeFor(string fingerprint)
    {
        var revoked = 0;
        foreach (var (value, entry) in _tickets)
        {
            if (entry.Fingerprint == fingerprint && _tickets.TryRemove(value, out _))
                revoked++;
        }
        return revoked;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (value, entry) in _tickets)
        {
            if (entry.ExpiresAt <= now)
                _tickets.TryRemove(value, out _);
        }
    }
}
