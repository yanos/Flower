using Flower.Server.Services;

using Xunit;

namespace Flower.Server.Tests;

// The throttle exists to keep a refused caller from burying the log in its own
// refusals - the callers most worth logging are the ones that repeat, and the
// in-memory buffer everything else is read from holds only 2000 entries. What
// these pin down is the balance that makes it safe: never swallow the *first*
// refusal (a silent rejection is the problem this whole area exists to fix),
// never let a repeat through early, and never let one kind of refusal silence
// a different one.
public class RefusalLogThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_refusal_from_a_source_is_always_logged()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldLog("1.2.3.4", T0, out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void Repeats_inside_the_window_are_suppressed()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);

        Assert.False(throttle.ShouldLog("1.2.3.4", T0.AddSeconds(1), out _));
        Assert.False(throttle.ShouldLog("1.2.3.4", T0.AddSeconds(59), out _));
    }

    // The count is the whole point of suppressing rather than dropping: the
    // next line that does get written has to say how much it stands for.
    [Fact]
    public void The_next_logged_line_carries_the_suppressed_count()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);
        for (var i = 1; i <= 5; i++)
            throttle.ShouldLog("1.2.3.4", T0.AddSeconds(i), out _);

        Assert.True(throttle.ShouldLog("1.2.3.4", T0.AddMinutes(2), out var suppressed));
        Assert.Equal(5, suppressed);
    }

    [Fact]
    public void The_count_resets_after_it_has_been_reported()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);
        throttle.ShouldLog("1.2.3.4", T0.AddSeconds(1), out _);
        throttle.ShouldLog("1.2.3.4", T0.AddMinutes(2), out _);

        Assert.True(throttle.ShouldLog("1.2.3.4", T0.AddMinutes(4), out var suppressed));
        Assert.Equal(0, suppressed);
    }

    // Two sources knocking at once must not hide each other: one device with a
    // wrong clock and one scanner are separate findings.
    [Fact]
    public void Sources_are_throttled_independently()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldLog("1.2.3.4", T0, out _));
        Assert.True(throttle.ShouldLog("5.6.7.8", T0, out _));
        Assert.False(throttle.ShouldLog("1.2.3.4", T0, out _));
    }

    // A burst is one line plus a count, not one line per attempt - the
    // behaviour that made this class necessary in the first place.
    [Fact]
    public void A_burst_of_forty_costs_two_lines()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        var logged = 0;
        for (var i = 0; i < 40; i++)
        {
            if (throttle.ShouldLog("1.2.3.4", T0.AddMilliseconds(i * 25), out _))
                logged++;
        }

        Assert.Equal(1, logged);
        Assert.True(throttle.ShouldLog("1.2.3.4", T0.AddMinutes(5), out var suppressed));
        Assert.Equal(39, suppressed);
    }

    [Fact]
    public void Prune_drops_sources_that_are_past_their_window_and_owe_nothing()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);
        Assert.Equal(1, throttle.TrackedSources);

        throttle.Prune(T0.AddMinutes(2));

        Assert.Equal(0, throttle.TrackedSources);
    }

    // Pruning must not lose a count that has not been reported yet, or a burst
    // followed by a quiet spell would silently under-report itself.
    [Fact]
    public void Prune_keeps_a_source_that_still_owes_a_suppressed_count()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);
        throttle.ShouldLog("1.2.3.4", T0.AddSeconds(1), out _);

        throttle.Prune(T0.AddMinutes(2));

        Assert.Equal(1, throttle.TrackedSources);
        Assert.True(throttle.ShouldLog("1.2.3.4", T0.AddMinutes(2), out var suppressed));
        Assert.Equal(1, suppressed);
    }

    [Fact]
    public void A_source_that_was_pruned_logs_immediately_when_it_returns()
    {
        var throttle = new RefusalLogThrottle(TimeSpan.FromMinutes(1));
        throttle.ShouldLog("1.2.3.4", T0, out _);
        throttle.Prune(T0.AddMinutes(2));

        Assert.True(throttle.ShouldLog("1.2.3.4", T0.AddMinutes(2), out var suppressed));
        Assert.Equal(0, suppressed);
    }
}
