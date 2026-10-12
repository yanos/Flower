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

namespace Flower.Tests.Controls;

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
        // Somewhere else on the screen - the tab bar, under the list.
        public Button Tab { get; }
        public int TabClicks { get; private set; }
        // What the glide scrolls: the row's list.
        public Border List { get; private set; }
        private readonly Grid _screen;
        private static readonly Point Centre = new(200, 50);
        private static readonly Point OnTab = new(200, 150);

        public Harness()
        {
            Row = NewButton("A song");
            Row.Click += (_, _) => Clicks++;
            List = new Border { Child = Row };
            Tab = NewButton("Albums");
            Tab.Click += (_, _) => TabClicks++;
            Grid.SetRow(Tab, 1);

            _screen = new Grid { RowDefinitions = new RowDefinitions("100,100"), Children = { List, Tab } };
            var root = new Border { Child = _screen };
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

        private static Button NewButton(string text) => new()
        {
            Content = text,
            // As the rows in the app have it. Without a brush of its own
            // this one is not hit-testable here, and no tap reaches it.
            Background = Brushes.Transparent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        // A screen switched to while its list was gliding: the list goes,
        // and a new one with a row of its own takes its place.
        public void ReplaceList()
        {
            List.Child = null;
            _screen.Children.Remove(List);
            List = new Border { Child = Row };
            _screen.Children.Insert(0, List);
            Window.UpdateLayout();
            Window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
        }

        // Its constructor is internal - only the recognizer is meant to raise it.
        public void StartGlide(int id) => List.RaiseEvent((ScrollGestureInertiaStartingEventArgs)Activator.CreateInstance(
            typeof(ScrollGestureInertiaStartingEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [id, new Vector(0, 2000)], culture: null)!);
        public void EndGesture(int id) => List.RaiseEvent(new ScrollGestureEndedEventArgs(id));

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

        public void Tap() => TapAt(Centre);

        public void TapTab() => TapAt(OnTab);

        private void TapAt(Point point)
        {
            Window.MouseMove(point);
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
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

    [AvaloniaFact]
    public void A_tap_off_the_gliding_list_still_clicks()
    {
        // The list glides on under the tab bar; the tab is tapped.
        var h = new Harness();
        h.StartGlide(7);

        h.TapTab();

        Assert.Equal(1, h.TabClicks);
    }

    [AvaloniaFact]
    public void A_glide_whose_list_left_the_screen_swallows_nothing()
    {
        // Its end is raised on the list it left with, where the guard cannot
        // hear it.
        var h = new Harness();
        h.StartGlide(7);
        h.ReplaceList();

        h.Tap();
        h.TapTab();

        Assert.Equal(1, h.Clicks);
        Assert.Equal(1, h.TabClicks);
    }
}
