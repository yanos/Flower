using System.Collections.Generic;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The backoff behind the discovery poll - see PeerRetrySchedule for why a
// remembered peer needs one and a discovered peer does not.
//
// What these pin is the shape of the compromise: a peer that misses once is
// treated as a hiccup and costs nothing, a peer that has been missing all
// afternoon is dialled once a minute rather than twelve times, and no peer is
// ever given up on - the interval stops growing, it never becomes never.
public class PeerRetryScheduleTests
{
    // One round of the poll, as the loop runs it: ask, and dial if the answer
    // is yes. Returns whether this peer was dialled.
    private static bool Round(PeerRetrySchedule schedule, bool answers)
    {
        if (!schedule.DueThisRound("peer"))
            return false;

        if (answers)
            schedule.RecordSuccess("peer");
        else
            schedule.RecordFailure("peer");

        return true;
    }

    // Which rounds, out of `rounds` of a peer that never answers, actually
    // dialled it.
    private static List<int> DialledRounds(int rounds)
    {
        var schedule = new PeerRetrySchedule();
        var dialled = new List<int>();
        for (var round = 1; round <= rounds; round++)
        {
            if (Round(schedule, answers: false))
                dialled.Add(round);
        }

        return dialled;
    }

    [Fact]
    public void A_peer_nothing_is_known_about_is_dialled_now()
    {
        var schedule = new PeerRetrySchedule();

        Assert.True(schedule.DueThisRound("peer"));
    }

    [Fact]
    public void A_peer_that_keeps_answering_is_dialled_every_round()
    {
        var schedule = new PeerRetrySchedule();

        for (var round = 0; round < 100; round++)
            Assert.True(Round(schedule, answers: true));
    }

    // A transient Wi-Fi stall, or one answer that arrived a moment after the
    // timeout, must not cost a peer that is actually there anything at all.
    [Fact]
    public void The_first_few_misses_stay_on_the_base_cadence()
    {
        var dialled = DialledRounds(PeerRetrySchedule.FreeAttempts);

        Assert.Equal([1, 2, 3], dialled);
    }

    [Fact]
    public void Past_those_the_interval_doubles_and_then_holds()
    {
        var dialled = DialledRounds(60);

        // Every round while the misses are free, then every 2nd, 4th and 8th,
        // and from there one round in MaxRounds for as long as it takes.
        Assert.Equal([1, 2, 3, 4, 6, 10, 18, 30, 42, 54], dialled);
    }

    // The half that matters as much as the backing off: an address that is dead
    // from where the user is standing is not a dead address, and the client has
    // to still be trying when they walk somewhere it works.
    [Fact]
    public void A_peer_that_has_failed_all_week_is_still_dialled()
    {
        var schedule = new PeerRetrySchedule();
        for (var miss = 0; miss < 2000; miss++)
            schedule.RecordFailure("peer");

        var dialled = 0;
        for (var round = 0; round < PeerRetrySchedule.MaxRounds; round++)
        {
            if (schedule.DueThisRound("peer"))
                dialled++;
        }

        Assert.Equal(1, dialled);
    }

    [Fact]
    public void An_answer_puts_a_peer_back_on_the_base_cadence()
    {
        var schedule = new PeerRetrySchedule();
        for (var miss = 0; miss < 50; miss++)
            schedule.RecordFailure("peer");

        schedule.RecordSuccess("peer");

        Assert.True(schedule.DueThisRound("peer"));
        Assert.True(schedule.DueThisRound("peer"));
    }

    // ResetAll is for the signals that say nothing about any one peer - the
    // network under all of them changed. Forget is for the one that does.
    [Fact]
    public void A_reset_makes_every_backed_off_peer_due_at_once()
    {
        var schedule = BackedOff("one", "two");

        Assert.False(schedule.DueThisRound("one"));

        schedule.ResetAll();

        Assert.True(schedule.DueThisRound("one"));
        Assert.True(schedule.DueThisRound("two"));
    }

    [Fact]
    public void Forgetting_one_peer_leaves_the_others_backed_off()
    {
        var schedule = BackedOff("one", "two");

        schedule.Forget("one");

        Assert.True(schedule.DueThisRound("one"));
        Assert.False(schedule.DueThisRound("two"));
    }

    private static PeerRetrySchedule BackedOff(params string[] peers)
    {
        var schedule = new PeerRetrySchedule();
        foreach (var peer in peers)
        {
            for (var miss = 0; miss < 50; miss++)
                schedule.RecordFailure(peer);
        }

        return schedule;
    }
}
