using System;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

// Touching a list to stop a flick must not tap the row under the finger (see
// FlingStopGuard). The glide itself is raised by hand - headless input has no
// touch, so no real recognizer ever starts one - but the press and release go
// through the real input pipeline to a real Button, since whether a click
// happens is decided there and nowhere else.
public class FlingStopGuardTests
{
    private sealed class Harness
    {
        public Window Window { get; }
        public Button Row { get; }
        public int Clicks { get; private set; }
        private static readonly Point Centre = new(200, 100);

        public Harness()
        {
            Row = new Button
            {
                Content = "A song",
                // As the rows in the app have it. Without a brush of its own
                // this one is not hit-testable here, and no tap reaches it.
                Background = Brushes.Transparent,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            };
            Row.Click += (_, _) => Clicks++;

            var root = new Border { Child = Row };
            FlingStopGuard.SetIsEnabled(root, true);

            Window = new Window { Width = 400, Height = 200, Content = root };
            Window.Styles.Add(new FluentTheme());
            Window.Show();
            Window.UpdateLayout();
            // Hit testing reads bounds the render pass computes - see
            // TrackDownloadButtonTests.
            Window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
        }

        // Its constructor is internal - only the recognizer is meant to raise it.
        public void StartGlide(int id) => Row.RaiseEvent((ScrollGestureInertiaStartingEventArgs)Activator.CreateInstance(
            typeof(ScrollGestureInertiaStartingEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [id, new Vector(0, 2000)], culture: null)!);
        public void EndGesture(int id) => Row.RaiseEvent(new ScrollGestureEndedEventArgs(id));

        public int LongPresses { get; private set; }

        public void EnableLongPress() =>
            LongPress.SetCommand(Row, new CommunityToolkit.Mvvm.Input.RelayCommand(() => LongPresses++));

        // Headless input has no touch, so the hold timer never runs - the
        // Holding event is raised by hand, mid-press, where the real one fires.
        public void PressAndHold()
        {
            Window.MouseMove(Centre);
            Window.MouseDown(Centre, MouseButton.Left);
            Row.RaiseEvent((HoldingRoutedEventArgs)Activator.CreateInstance(
                typeof(HoldingRoutedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [HoldingState.Started, Centre, PointerType.Touch, null], culture: null)!);
            Window.MouseUp(Centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Tap()
        {
            Window.MouseMove(Centre);
            Window.MouseDown(Centre, MouseButton.Left);
            Window.MouseUp(Centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void A_tap_with_nothing_gliding_still_clicks()
    {
        var h = new Harness();

        h.Tap();

        Assert.Equal(1, h.Clicks);
    }

    [AvaloniaFact]
    public void A_touch_that_stops_a_glide_does_not_click()
    {
        var h = new Harness();
        h.StartGlide(7);

        h.Tap();

        Assert.Equal(0, h.Clicks);
    }

    [AvaloniaFact]
    public void Only_the_touch_that_stopped_the_glide_is_swallowed()
    {
        var h = new Harness();
        h.StartGlide(7);
        h.Tap();
        // What the recognizer does when that press lands.
        h.EndGesture(7);

        h.Tap();

        Assert.Equal(1, h.Clicks);
    }

    [AvaloniaFact]
    public void A_glide_that_finished_on_its_own_does_not_swallow_the_next_tap()
    {
        var h = new Harness();
        h.StartGlide(7);
        h.EndGesture(7);

        h.Tap();

        Assert.Equal(1, h.Clicks);
    }

    [AvaloniaFact]
    public void A_hold_with_nothing_gliding_is_still_a_long_press()
    {
        var h = new Harness();
        h.EnableLongPress();

        h.PressAndHold();

        Assert.Equal(1, h.LongPresses);
    }

    [AvaloniaFact]
    public void Resting_the_finger_that_stopped_a_glide_is_not_a_long_press()
    {
        var h = new Harness();
        h.EnableLongPress();
        h.StartGlide(7);

        h.PressAndHold();

        Assert.Equal(0, h.LongPresses);
        Assert.Equal(0, h.Clicks);
    }

    [AvaloniaFact]
    public void The_end_of_some_other_gesture_does_not_end_the_glide()
    {
        var h = new Harness();
        h.StartGlide(7);
        h.EndGesture(8);

        h.Tap();

        Assert.Equal(0, h.Clicks);
    }
}
