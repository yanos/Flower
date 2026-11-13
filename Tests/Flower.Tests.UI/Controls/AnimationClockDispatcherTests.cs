using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Flower.Services;
using Xunit;

namespace Flower.Tests.Controls;

// Frame and timer integration for the shared animation clock.
public class AnimationClockDispatcherTests
{
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
