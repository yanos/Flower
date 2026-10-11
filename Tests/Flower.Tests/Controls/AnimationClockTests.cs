using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Flower.Services;
using Xunit;

namespace Flower.Tests.Controls;

// The shared 60Hz clock four animations used to each run their own
// DispatcherTimer for - see docs/ARCHITECTURE-REVIEW.md Tier 1.5. Driven here
// through the internal time-injecting constructor rather than a real dispatcher
// timer, so frames advance deterministically instead of by sleeping.
public class AnimationClockTests
{
    private TimeSpan _now = TimeSpan.Zero;
    private readonly AnimationClock _clock;

    public AnimationClockTests() => _clock = new AnimationClock(() => _now);

    private void Frame(double milliseconds = 16)
    {
        _now += TimeSpan.FromMilliseconds(milliseconds);
        _clock.TickForTest();
    }

    [Fact]
    public void A_subscriber_is_ticked_with_the_time_since_its_own_subscription()
    {
        _now = TimeSpan.FromSeconds(5); // the clock has been alive a while
        var seen = new List<TimeSpan>();
        _clock.Subscribe(seen.Add);

        Frame();
        Frame();

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(32) }, seen);
    }

    // Two animations that start at different moments each measure from their
    // own start - the spinner's angle and an easing's progress are both
    // elapsed-relative, so a shared absolute tick would be wrong for one of them.
    [Fact]
    public void Each_subscriber_measures_from_when_it_subscribed()
    {
        TimeSpan first = default, second = default;
        _clock.Subscribe(e => first = e);
        Frame();
        _clock.Subscribe(e => second = e);
        Frame();

        Assert.Equal(TimeSpan.FromMilliseconds(32), first);
        Assert.Equal(TimeSpan.FromMilliseconds(16), second);
    }

    [Fact]
    public void Disposing_a_subscription_stops_its_ticks()
    {
        var ticks = 0;
        var handle = _clock.Subscribe(_ => ticks++);

        Frame();
        handle.Dispose();
        Frame();

        Assert.Equal(1, ticks);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var handle = _clock.Subscribe(_ => { });
        handle.Dispose();
        handle.Dispose();

        Assert.Equal(0, _clock.SubscriberCount);
    }

    // An easing unsubscribes from inside its own final frame, which would
    // otherwise mutate the list mid-walk.
    [Fact]
    public void A_subscriber_may_dispose_itself_from_inside_its_callback()
    {
        var ticksA = 0;
        var ticksB = 0;
        IDisposable? a = null;
        a = _clock.Subscribe(_ => { ticksA++; a!.Dispose(); });
        _clock.Subscribe(_ => ticksB++);

        Frame();
        Frame();

        Assert.Equal(1, ticksA);
        Assert.Equal(2, ticksB); // the second subscriber still got its frame
    }

    // The easings finish by invoking a navigation callback, which can cancel
    // another animation that is already in this frame's buffer - it must not
    // be ticked after being disposed.
    [Fact]
    public void A_subscriber_disposed_by_an_earlier_callback_is_not_ticked_this_frame()
    {
        var laterTicks = 0;
        IDisposable? later = null;
        _clock.Subscribe(_ => later!.Dispose());
        later = _clock.Subscribe(_ => laterTicks++);

        Frame();

        Assert.Equal(0, laterTicks);
    }

    [Fact]
    public void Subscribing_from_inside_a_callback_does_not_disturb_the_frame()
    {
        var added = 0;
        var subscribedOnce = false;
        _clock.Subscribe(_ =>
        {
            if (subscribedOnce)
                return;
            subscribedOnce = true;
            _clock.Subscribe(_ => added++);
        });

        Frame();
        Assert.Equal(0, added); // joined mid-frame, so it starts on the next one
        Frame();
        Assert.Equal(1, added);
    }

    // The whole point: an idle app has no 60Hz wakeup at all, not exactly one.
    [Fact]
    public void The_clock_runs_only_while_something_is_subscribed()
    {
        Assert.False(_clock.IsRunning);

        var handle = _clock.Subscribe(_ => { });
        Assert.True(_clock.IsRunning);

        handle.Dispose();
        Assert.False(_clock.IsRunning);
        Assert.Equal(0, _clock.SubscriberCount);
    }

    // On a 120Hz phone a 16ms timer landed out of step with the frames it was
    // meant to drive - an easing moved one frame's distance, then two - so with
    // a TopLevel to ask, the clock ticks on the frames themselves and keeps no
    // timer at all.
    [AvaloniaFact]
    public void With_a_TopLevel_it_ticks_on_frames_and_starts_no_timer()
    {
        var window = new Window { Width = 100, Height = 100 };
        window.Show();
        var clock = new AnimationClock { TopLevelProvider = () => window };
        var ticks = 0;

        // Moving something, as every real subscriber does: a frame that
        // changes nothing is paced by Avalonia's own fallback timer, which the
        // headless dispatcher does not run, where one that changes something
        // is paced by the frames being drawn.
        var subscription = clock.Subscribe(_ => window.Opacity = ++ticks % 2 == 0 ? 1 : 0.9);
        Assert.False(clock.HasTimer);
        PumpUntil(() => ticks >= 3);
        Assert.True(ticks >= 3, $"only {ticks} frames ticked");

        // The request for the next frame lapses on the first frame that finds
        // nothing subscribed, so an idle clock asks for no frames at all.
        subscription.Dispose();
        Assert.False(clock.IsRunning);
        var settled = ticks;
        PumpUntil(() => false, milliseconds: 200);
        Assert.Equal(settled, ticks);
        window.Close();
    }

    // The XAML previewer, or a view built before the platform has attached
    // one: nothing to ask for frames, so it falls back to its timer.
    [AvaloniaFact]
    public void Without_a_TopLevel_it_falls_back_to_its_timer()
    {
        var clock = new AnimationClock { TopLevelProvider = () => null };

        var subscription = clock.Subscribe(_ => { });

        Assert.True(clock.HasTimer);
        subscription.Dispose();
        Assert.False(clock.HasTimer);
    }

    // Frames, as the display would deliver them: headless renders only when
    // told to, and a frame callback waits on the previous frame being drawn.
    private static void PumpUntil(Func<bool> done, int milliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < deadline)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }
}
