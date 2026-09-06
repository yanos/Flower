using System;
using System.Collections.Generic;

namespace Flower.Services;

// How often a peer that is not answering gets dialled again, counted in rounds
// of the discovery poll rather than in seconds - the poll runs on a fixed
// cadence (see NetworkDiscoveryService.AliasPollInterval), so it is already the
// clock, and giving this one of its own would only be a second opinion about
// what time it is.
//
// The fixed cadence is right for a peer that answers, and right for a
// discovered peer that stops - the latter is pruned after
// MaxConsecutiveResolveFailures and simply ceases to be polled. It is wrong for
// exactly one case: a *remembered* peer, which is deliberately never pruned
// (see DiscoveredDevice.IsRemembered), because pruning one would destroy the
// only record of how to reach a server the user is not currently near.
//
// So a remembered address that cannot work from where the user is standing was
// re-resolved and re-dialled every five seconds for the life of the process - a
// DNS lookup, a TCP connect, a TLS handshake and an Ed25519 signature apiece.
// An address that goes to a timeout rather than a refusal (a tailnet address
// dialled from off the tailnet is the ordinary case) spends the client's whole
// PeerHttpClient timeout doing it, which at three seconds against a five-second
// cadence leaves the socket open more of the time than not. That was the
// largest single share of an idle Flower's own CPU.
//
// Backing off is the answer and giving up is not: the address is dead from
// here, not dead. So the interval grows and then stops - it never becomes
// "never" - and every signal that the reason for the failures may no longer
// hold throws the accumulated backoff away. See NetworkDiscoveryService's calls
// to Forget and ResetAll: a successful handshake, a return to the foreground, a
// peer appearing or moving on the LAN, and any address a user or the paired
// server hands us.
internal sealed class PeerRetrySchedule
{
    // Misses that cost nothing: the first few stay on the base cadence, so a
    // Wi-Fi hiccup or one answer that arrived just after the timeout never
    // costs a peer that is actually there more than a poll interval. Matches
    // MaxConsecutiveResolveFailures, which is the same judgement about the same
    // misses.
    public const int FreeAttempts = 3;

    // Where the doubling stops, in poll rounds - a minute at the poll's own
    // five-second cadence. The cap is what a client waits, worst case, to
    // notice that a remembered address has come alive, which is the case of
    // walking out of the house and the tailnet address becoming the route. A
    // twelvefold cut in dials that still bounds that wait at something a person
    // would call "a moment" - and the resets above mean it is rarely what is
    // actually waited on.
    public const int MaxRounds = 12;

    private readonly object _gate = new();

    // Keyed by mDNS instance name, the same key _knownDevices uses.
    private readonly Dictionary<string, Attempt> _attempts = [];

    private sealed class Attempt
    {
        public int Failures;

        // Rounds still to be skipped before this peer is dialled again.
        public int Skips;
    }

    // Whether to dial this peer in the round now happening, and - because
    // asking is what advances this peer's place in the schedule - a round is
    // consumed by asking. The poll loop asks once per peer per round, which is
    // what makes that honest.
    //
    // A peer nothing is known about is always due: never-failed and forgotten
    // are the same answer here, which is what makes Forget a reset.
    public bool DueThisRound(string instanceName)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(instanceName, out var attempt))
                return true;

            if (attempt.Skips > 0)
            {
                attempt.Skips--;
                return false;
            }

            // Re-armed before the dial rather than after it comes back. The
            // dial is asynchronous and the next round may arrive before it
            // fails, and a peer whose every round is due while one attempt is
            // still in flight is the treadmill this class exists to stop.
            attempt.Skips = SkipsAfter(attempt.Failures);
            return true;
        }
    }

    public void RecordFailure(string instanceName)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(instanceName, out var attempt))
                _attempts[instanceName] = attempt = new Attempt();

            attempt.Failures++;
            attempt.Skips = SkipsAfter(attempt.Failures);
        }
    }

    public void RecordSuccess(string instanceName) => Forget(instanceName);

    // Drops a peer's backoff, so it is due in the next round and starts over.
    // Also how a peer that is gone for good stops occupying an entry.
    public void Forget(string instanceName)
    {
        lock (_gate)
            _attempts.Remove(instanceName);
    }

    // Every peer at once, for a signal that says nothing about any particular
    // one - the network under all of them changed.
    public void ResetAll()
    {
        lock (_gate)
            _attempts.Clear();
    }

    // Rounds to skip before the next dial: none while the misses are still
    // free, then doubling intervals - 2, 4, 8 rounds - held at MaxRounds.
    private static int SkipsAfter(int failures)
    {
        if (failures <= FreeAttempts)
            return 0;

        // Shifted rather than raised to a power because a peer that has been
        // missing all week overflows the exponent long before it overflows the
        // cap; clamping the shift keeps that arithmetic honest either way.
        var rounds = 1 << Math.Min(failures - FreeAttempts, 30);
        return Math.Min(rounds, MaxRounds) - 1;
    }
}
