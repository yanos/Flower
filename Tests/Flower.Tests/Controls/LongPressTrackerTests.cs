using Avalonia;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Controls;

// Whether a touch still counts as a long press when the wait is up.
public class LongPressTrackerTests
{
    private readonly object _finger = new();
    private readonly LongPressTracker _tracker = new();

    // Avalonia's own Holding gave up past 4px, which a resting finger drifts
    // often enough that a long press sometimes did nothing.
    [Fact]
    public void A_finger_that_drifts_a_little_is_still_a_long_press()
    {
        _tracker.Press(_finger, new Point(100, 100));

        _tracker.Move(_finger, new Point(106, 104));

        Assert.True(_tracker.Fire(_finger));
    }

    [Fact]
    public void A_finger_that_moves_away_is_not_a_long_press()
    {
        _tracker.Press(_finger, new Point(100, 100));

        _tracker.Move(_finger, new Point(100, 100 + LongPressTracker.Slop + 1));
        _tracker.Move(_finger, new Point(100, 100));

        Assert.False(_tracker.Fire(_finger));
    }

    [Fact]
    public void A_finger_lifted_before_the_wait_is_up_is_not_a_long_press()
    {
        _tracker.Press(_finger, new Point(100, 100));

        _tracker.End(_finger);

        Assert.False(_tracker.Fire(_finger));
    }

    [Fact]
    public void A_second_finger_down_ends_the_first_ones_long_press()
    {
        var second = new object();
        _tracker.Press(_finger, new Point(100, 100));

        _tracker.Press(second, new Point(200, 200));
        _tracker.End(second);

        Assert.False(_tracker.Fire(_finger));
    }

    [Fact]
    public void A_long_press_fires_once()
    {
        _tracker.Press(_finger, new Point(100, 100));

        Assert.True(_tracker.Fire(_finger));
        Assert.False(_tracker.Fire(_finger));
    }
}
