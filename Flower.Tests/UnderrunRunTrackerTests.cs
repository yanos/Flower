using Flower.Audio;

using Xunit;

namespace Flower.Tests;

// The render watchdog's underrun reporting, which is a diagnostic that got
// itself into the evidence.
//
// The counter only climbs, so "it moved again" is true on every tick of a
// problem nobody has fixed, and the watchdog wrote a warning for each. A phone
// left idle overnight with a feeder polling a stopped device (see AudioFeeder)
// therefore logged 86,400 identical lines a day into an archive that is pushed
// to the paired server and kept for a week - so the diagnostic became both the
// bulk of the log and the reason draining it was expensive.
//
// What these pin is that a run is one event: reported when it starts, again
// while it lasts but rarely, and once when it ends.
public class UnderrunRunTrackerTests
{
    [Fact]
    public void A_quiet_render_loop_says_nothing()
    {
        var tracker = new UnderrunRunTracker();

        for (var tick = 0; tick < 10; tick++)
        {
            var reading = tracker.Observe(0);

            Assert.False(reading.Log);
            Assert.Null(reading.Cleared);
        }
    }

    [Fact]
    public void The_first_underrunning_tick_is_reported()
    {
        var tracker = new UnderrunRunTracker();
        tracker.Observe(0);

        var reading = tracker.Observe(12);

        Assert.True(reading.Log);
        Assert.Equal(12, reading.New);
        Assert.Equal(1, reading.RunTicks);
    }

    // The whole point: a run that carries on is not news every second.
    [Fact]
    public void A_run_that_carries_on_is_reported_once_a_minute_rather_than_every_tick()
    {
        var tracker = new UnderrunRunTracker();
        var count = 0L;
        var reported = 0;

        // An hour of ticks in the state the idle phone was actually in.
        for (var tick = 0; tick < 3600; tick++)
        {
            count += 435;
            if (tracker.Observe(count).Log)
                reported++;
        }

        // The tick that opened the run, then one a minute for the rest of it -
        // 61 lines where the old watchdog wrote 3,600.
        Assert.Equal(1 + 3600 / UnderrunRunTracker.SummaryTicks, reported);
    }

    // A run ending is worth a line of its own: silence afterwards is equally
    // what a watchdog that has stopped running looks like.
    [Fact]
    public void A_run_that_ends_is_reported_once_with_what_it_came_to()
    {
        var tracker = new UnderrunRunTracker();
        tracker.Observe(100);   // the counter as this watchdog found it
        tracker.Observe(100);   // ... and a quiet tick, so no run is open

        tracker.Observe(150);
        tracker.Observe(190);

        var recovered = tracker.Observe(190);

        Assert.False(recovered.Log);
        var run = Assert.NotNull(recovered.Cleared);
        Assert.Equal(2, run.Ticks);
        // What the run added, not what the counter has ever reached.
        Assert.Equal(90, run.Underruns);

        // And then nothing, until something underruns again.
        Assert.Null(tracker.Observe(190).Cleared);
    }

    // Two separate glitches are two events, not one long one - the second must
    // not be swallowed by the first having already been reported.
    [Fact]
    public void A_second_run_is_reported_as_its_own_event()
    {
        var tracker = new UnderrunRunTracker();
        tracker.Observe(0);

        Assert.True(tracker.Observe(5).Log);
        Assert.False(tracker.Observe(5).Log);
        Assert.True(tracker.Observe(9).Log);
    }
}
