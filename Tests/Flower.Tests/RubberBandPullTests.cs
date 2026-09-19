using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// A finger landing on a list already at its top and dragging down. Avalonia
// 12's ScrollGestureRecognizer starts no gesture for that drag at all, so
// RubberBandScroll follows the pointer itself: the list stretches down under
// the finger, springs back on release, and a long enough pull raises
// PulledDownEvent (which is what opens the screen's filter box). Pointer
// events are raised by hand because the headless platform has no touch.
public class RubberBandPullTests
{
    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required ScrollViewer Scroller { get; init; }
        public required Border Content { get; init; }
        public int PulledDownCount;

        public double StretchY => ((TranslateTransform)Scroller.RenderTransform!).Y;
    }

    private static Rig Show()
    {
        var content = new Border { Height = 2000, Background = Brushes.Gray, VerticalAlignment = VerticalAlignment.Top };
        var scroller = new ScrollViewer { Content = content };
        RubberBandScroll.SetIsEnabled(scroller, true);
        // Theme first: content added before it never gets its templates.
        var window = new Window { Width = 390, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = scroller;
        window.Show();
        window.Measure(new Size(390, 700));
        window.Arrange(new Rect(0, 0, 390, 700));
        Dispatcher.UIThread.RunJobs();

        var rig = new Rig { Window = window, Scroller = scroller, Content = content };
        scroller.AddHandler(RubberBandScroll.PulledDownEvent, (_, _) => rig.PulledDownCount++);
        return rig;
    }

    private static PointerPointProperties Pressed() => new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);

    private static void Press(Rig rig, IPointer pointer, Point at) =>
        rig.Content.RaiseEvent(new PointerPressedEventArgs(rig.Content, pointer, rig.Window, at, 0, Pressed(), KeyModifiers.None));

    private static void Move(Rig rig, IPointer pointer, Point to)
    {
        var target = pointer.Captured as Interactive ?? rig.Content;
        target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, pointer, rig.Window, to, 0, Pressed(), KeyModifiers.None));
    }

    private static void Release(Rig rig, IPointer pointer, Point at)
    {
        var target = pointer.Captured as Interactive ?? rig.Content;
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, rig.Window, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    }

    [AvaloniaFact]
    public void A_drag_down_from_the_top_stretches_the_list_under_the_finger()
    {
        var rig = Show();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

        Press(rig, finger, new Point(200, 100));
        Move(rig, finger, new Point(200, 115));
        Move(rig, finger, new Point(200, 200));

        Assert.True(rig.StretchY > 0, $"stretched by {rig.StretchY}");
        Assert.Same(rig.Scroller, finger.Captured);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void A_long_pull_raises_pulled_down_on_release()
    {
        var rig = Show();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

        Press(rig, finger, new Point(200, 100));
        Move(rig, finger, new Point(200, 115));
        Move(rig, finger, new Point(200, 215));
        Assert.Equal(0, rig.PulledDownCount);
        Release(rig, finger, new Point(200, 215));

        Assert.Equal(1, rig.PulledDownCount);
        Assert.Null(finger.Captured);
        rig.Window.Close();
    }

    // The pull reports how far it has gone towards opening, rising with the
    // stretch to 1 at the point letting go would open, and falling back to 0
    // as the stretch springs back.
    [AvaloniaFact]
    public void A_pull_reports_its_progress_up_and_back_down()
    {
        // Stepped by hand: the spring back runs on AnimationClock, whose own
        // timer the headless dispatcher does not drive.
        var previous = AnimationClock.Current;
        var now = TimeSpan.Zero;
        var clock = new AnimationClock(() => now);
        AnimationClock.Current = clock;
        try
        {
            var rig = Show();
            var reported = new List<double>();
            rig.Scroller.AddHandler(RubberBandScroll.PullingDownEvent, (_, e) => reported.Add(e.Progress));
            var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

            Press(rig, finger, new Point(200, 100));
            Move(rig, finger, new Point(200, 115));
            Move(rig, finger, new Point(200, 130));
            Assert.InRange(reported[^1], 0.01, 0.99);
            Move(rig, finger, new Point(200, 215));
            Assert.Equal(1, reported[^1]);
            Release(rig, finger, new Point(200, 215));

            Assert.InRange(reported[^1], 0.99, 1);

            now += TimeSpan.FromMilliseconds(100);
            clock.TickForTest();
            Assert.InRange(reported[^1], 0.01, 0.99);
            now += TimeSpan.FromMilliseconds(400);
            clock.TickForTest();
            Assert.Equal(0, reported[^1]);
            rig.Window.Close();
        }
        finally
        {
            AnimationClock.Current = previous;
        }
    }

    [AvaloniaFact]
    public void A_short_pull_only_bounces()
    {
        var rig = Show();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

        Press(rig, finger, new Point(200, 100));
        Move(rig, finger, new Point(200, 115));
        Move(rig, finger, new Point(200, 145));
        Release(rig, finger, new Point(200, 145));

        Assert.Equal(0, rig.PulledDownCount);
        rig.Window.Close();
    }

    // A sideways drag is ScreenStackPanel's swipe between screens.
    [AvaloniaFact]
    public void A_sideways_drag_is_left_alone()
    {
        var rig = Show();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

        Press(rig, finger, new Point(100, 100));
        Move(rig, finger, new Point(130, 112));
        Move(rig, finger, new Point(250, 150));

        Assert.Equal(0, rig.StretchY);
        Assert.Null(finger.Captured);
        rig.Window.Close();
    }

    // Down from anywhere but the top is an ordinary scroll back up the list,
    // which the recognizer handles.
    [AvaloniaFact]
    public void A_list_scrolled_down_is_left_to_scroll()
    {
        var rig = Show();
        rig.Scroller.Offset = new Vector(0, 300);
        Dispatcher.UIThread.RunJobs();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);

        Press(rig, finger, new Point(200, 100));
        Move(rig, finger, new Point(200, 115));
        Move(rig, finger, new Point(200, 200));

        Assert.Equal(0, rig.StretchY);
        Assert.Null(finger.Captured);
        rig.Window.Close();
    }
}
