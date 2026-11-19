using System;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Controls;

// A finger resting on a row opens its menu; a finger scrolling the list does
// not (see LongPress). Headless input has no touch, so the touch's press is
// raised on the row by hand, as is the scroll - the recognizer that would
// raise it never runs here. The wait is the real one.
public class LongPressTests
{
    private sealed class Harness
    {
        private readonly Pointer _finger = new(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
        private readonly Window _window;

        public Border List { get; }
        public Button Row { get; }
        // Another list on the same screen: a shelf beside the one touched.
        public Border OtherList { get; }
        public int LongPresses { get; private set; }

        public Harness()
        {
            Row = new Button { Content = "A song" };
            LongPress.SetCommand(Row, new RelayCommand(() => LongPresses++));
            List = new Border { Child = Row };
            OtherList = new Border();
            _window = new Window { Content = new StackPanel { Children = { List, OtherList } } };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Press() => Row.RaiseEvent(new PointerPressedEventArgs(
            Row, _finger, _window, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));

        public static void Scroll(Control list) => list.RaiseEvent(new ScrollGestureEventArgs(1, new Vector(0, 6)));

        // Past the wait, with the finger still down: the dispatcher run until
        // a timer of its own stops it, so LongPress's timer gets its turn -
        // RunJobs alone does not run timers.
        public static void Hold()
        {
            using var held = new CancellationTokenSource();
            DispatcherTimer.RunOnce(held.Cancel, TimeSpan.FromMilliseconds(700));
            Dispatcher.UIThread.MainLoop(held.Token);
        }
    }

    [AvaloniaFact]
    public void A_finger_resting_on_a_row_is_a_long_press()
    {
        var h = new Harness();

        h.Press();
        Harness.Hold();

        Assert.Equal(1, h.LongPresses);
    }

    // Avalonia's scroll recognizer takes the touch 5px in and from then on
    // the row hears nothing of it, so a slow scroll kept down past the wait
    // opened the menu of the row it started on.
    [AvaloniaFact]
    public void A_finger_scrolling_the_list_is_not_a_long_press()
    {
        var h = new Harness();

        h.Press();
        Harness.Scroll(h.List);
        Harness.Hold();

        Assert.Equal(0, h.LongPresses);
    }

    [AvaloniaFact]
    public void Another_list_scrolling_does_not_end_the_long_press()
    {
        var h = new Harness();

        h.Press();
        Harness.Scroll(h.OtherList);
        Harness.Hold();

        Assert.Equal(1, h.LongPresses);
    }
}
