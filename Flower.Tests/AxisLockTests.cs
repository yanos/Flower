using Avalonia;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

// The axis-lock state machine behind PredominantAxisScroll - the thing standing
// in for NSScrollView.usesPredominantAxisScrolling, which Avalonia cannot reach
// (see PredominantAxisScroll's own comment). Driven here as a plain stream of
// pixel deltas and timestamps, with no window or scroller involved.
//
// The ticks are supplied rather than read from the clock, so "the gesture ended"
// is expressed directly instead of by sleeping.
public class AxisLockTests
{
    // A real trackpad delivers many small deltas rather than one big one, and
    // the decision threshold is crossed by accumulating them - so the tests feed
    // gestures the same way, at a plausible ~8ms apart.
    private const long FrameMs = 8;

    private static Vector Feed(AxisLock axisLock, double dx, double dy, ref long ticks)
    {
        var allowed = axisLock.Allow(new Vector(dx, dy), ticks);
        ticks += FrameMs;
        return allowed;
    }

    [Fact]
    public void A_mostly_vertical_gesture_locks_vertical_and_drops_sideways_drift()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // Enough travel to get past DecisionDistance, drifting slightly sideways
        // throughout - the exact shape of the complaint this fixes.
        for (var i = 0; i < 5; i++)
            Feed(axisLock, 1, 6, ref ticks);

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);

        var allowed = Feed(axisLock, 3, 10, ref ticks);
        Assert.Equal(0, allowed.X);
        Assert.Equal(10, allowed.Y);
    }

    [Fact]
    public void A_mostly_horizontal_gesture_locks_horizontal()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 6, 1, ref ticks);

        Assert.Equal(AxisLock.Axis.Horizontal, axisLock.Locked);

        var allowed = Feed(axisLock, 10, 3, ref ticks);
        Assert.Equal(10, allowed.X);
        Assert.Equal(0, allowed.Y);
    }

    [Fact]
    public void The_very_first_event_of_a_gesture_is_already_locked()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // There is no opening window in which both axes move. This is the whole
        // fix for short scrolls: they are made up entirely of first events, so
        // anything let through early is let through for the entire gesture.
        var allowed = Feed(axisLock, 2, 3, ref ticks);

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
        Assert.Equal(0, allowed.X);
        Assert.Equal(3, allowed.Y);
    }

    [Fact]
    public void A_short_scroll_that_never_commits_still_moves_and_still_never_drifts()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // A flick shorter than DecisionDistance: the axis stays provisional the
        // whole way, which must not mean either "nothing scrolls" or "sideways
        // gets through".
        var total = 0.0;
        for (var i = 0; i < 3; i++)
        {
            var allowed = Feed(axisLock, 0.6, 2, ref ticks);
            Assert.Equal(0, allowed.X);
            total += allowed.Y;
        }

        Assert.False(axisLock.Committed);
        Assert.Equal(6, total);
    }

    [Fact]
    public void A_wrong_early_guess_corrects_itself_before_the_gesture_commits()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // Opens sideways, then turns out to be vertical. The provisional lock
        // exists for exactly this: the correction costs a couple of pixels of
        // horizontal movement, not a whole gesture on the wrong axis.
        Feed(axisLock, 3, 0, ref ticks);
        Assert.Equal(AxisLock.Axis.Horizontal, axisLock.Locked);

        for (var i = 0; i < 3; i++)
            Feed(axisLock, 0, 4, ref ticks);

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
        Assert.True(axisLock.Committed);
    }

    [Fact]
    public void A_near_tie_goes_to_vertical_rather_than_sideways()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // Diagonal, leaning very slightly horizontal. Below HorizontalBias, so
        // it reads as a vertical scroll held at an angle - which is what it
        // almost always is - rather than as an attempt to scroll sideways.
        for (var i = 0; i < 5; i++)
            Feed(axisLock, 5, 4, ref ticks);

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
    }

    [Fact]
    public void Sideways_drift_never_unlocks_a_gesture_that_keeps_scrolling_vertically()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 1, 6, ref ticks);
        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);

        // A long gesture that drifts sideways the whole way but is always moving
        // down harder than across. Cross-axis pressure is measured net of the
        // locked axis, so this accumulates nothing no matter how long it runs.
        for (var i = 0; i < 200; i++)
        {
            var allowed = Feed(axisLock, 4, 9, ref ticks);
            Assert.Equal(0, allowed.X);
        }

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
    }

    [Fact]
    public void Deliberately_turning_sideways_mid_gesture_switches_the_lock()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 0, 6, ref ticks);
        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);

        // Now genuinely horizontal, without pausing. It takes real travel to get
        // there - that is the point - but it does get there.
        for (var i = 0; i < 20 && axisLock.Locked == AxisLock.Axis.Vertical; i++)
            Feed(axisLock, 10, 0, ref ticks);

        Assert.Equal(AxisLock.Axis.Horizontal, axisLock.Locked);

        var allowed = Feed(axisLock, 10, 2, ref ticks);
        Assert.Equal(10, allowed.X);
        Assert.Equal(0, allowed.Y);
    }

    [Fact]
    public void A_pause_ends_the_gesture_so_the_next_one_picks_its_own_axis()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 0, 6, ref ticks);
        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);

        // Fingers lifted. Avalonia never tells us that (the macOS backend drops
        // NSEvent.phase), so a gap in the event stream is the only signal there
        // is - past GestureGapMs the next event starts a fresh decision.
        ticks += (long)AxisLock.GestureGapMs + 1;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 6, 0, ref ticks);

        Assert.Equal(AxisLock.Axis.Horizontal, axisLock.Locked);
    }

    [Fact]
    public void Momentum_keeps_the_axis_the_flick_was_thrown_along()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        for (var i = 0; i < 5; i++)
            Feed(axisLock, 1, 20, ref ticks);
        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);

        // Coasting: the deltas taper off but keep arriving inside the gap, so
        // this is still the same gesture and stays locked. A momentum tail that
        // slid sideways as it decayed would be exactly the artefact being fixed.
        for (var delta = 18.0; delta > 0.5; delta *= 0.85)
        {
            var allowed = Feed(axisLock, 1, delta, ref ticks);
            Assert.Equal(0, allowed.X);
        }

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
    }

    [Fact]
    public void A_plain_mouse_wheel_reporting_only_one_axis_locks_vertical_immediately()
    {
        var axisLock = new AxisLock();
        long ticks = 1000;

        // One notch is 50px, well past DecisionDistance, so a discrete wheel
        // commits on its very first event and never leaves the vertical axis.
        var allowed = Feed(axisLock, 0, 50, ref ticks);

        Assert.Equal(AxisLock.Axis.Vertical, axisLock.Locked);
        Assert.Equal(50, allowed.Y);
    }
}
