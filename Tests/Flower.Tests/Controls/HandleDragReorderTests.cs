using System.Collections.ObjectModel;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Controls;

// A row dragged by its handle on a page tall enough to scroll - Home's shelves
// in the phone's Settings. The ScrollViewer's own gesture recognizer is handed
// the same touch, and it used to take the pointer as soon as the finger moved:
// the page scrolled and the row stayed where it was. Pointer events are raised
// by hand because the headless platform has no touch, and the recognizer
// ignores a mouse.
public class HandleDragReorderTests
{
    private const double RowHeight = 44;
    private const double ScrolledTo = 300;

    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required ScrollViewer Scroller { get; init; }
        public required ObservableCollection<string> Items { get; init; }
        public required ItemsControl List { get; init; }

        public Border HandleOf(string item) =>
            List.GetRealizedContainers().OfType<ContentPresenter>()
                .Select(c => (Border)((Border)c.Child!).Child!)
                .Single(h => (string)h.DataContext! == item);
    }

    private static Rig Show()
    {
        var items = new ObservableCollection<string> { "Continue Playing", "Recently Played", "Recently Added" };
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<string>((_, _) => new Border
            {
                Child = new Border
                {
                    Classes = { "dragHandle" },
                    Width = 36,
                    Height = RowHeight,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                },
            }),
        };
        // The rest of the page, which is what makes it scroll: cards above
        // the list and below it.
        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Children = { new Border { Height = 600 }, list, new Border { Height = 2000 } } },
        };
        _ = new HandleDragReorder(list, item => items.IndexOf((string)item!), (item, to) => items.Move(items.IndexOf((string)item), to));

        // Theme first: content added before it never gets its templates - and
        // the recognizer lives in the ScrollViewer's template.
        var window = new Window { Width = 390, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = scroller;
        window.Show();
        window.Measure(new Size(390, 700));
        window.Arrange(new Rect(0, 0, 390, 700));
        Dispatcher.UIThread.RunJobs();

        // Scrolled partway down, as the page is by the time Home's card is in
        // reach - the recognizer will not start a downward drag on a page
        // already at its top.
        scroller.Offset = new Vector(0, ScrolledTo);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return new Rig { Window = window, Scroller = scroller, Items = items, List = list };
    }

    private static PointerPointProperties Pressed() => new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);

    private static Interactive Target(Rig rig, IPointer finger, Interactive pressed) =>
        finger.Captured as Interactive ?? pressed;

    [AvaloniaFact]
    public void A_row_dragged_by_its_handle_moves_and_the_page_stays_put()
    {
        var rig = Show();
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        var handle = rig.HandleOf("Continue Playing");
        var start = handle.TranslatePoint(new Point(18, RowHeight / 2), rig.Window)!.Value;

        handle.RaiseEvent(new PointerPressedEventArgs(handle, finger, rig.Window, start, 0, Pressed(), KeyModifiers.None));
        foreach (var dy in new[] { 4.0, 16, 40, 70, 2 * RowHeight })
        {
            var target = Target(rig, finger, handle);
            target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, finger, rig.Window,
                start + new Point(0, dy), 0, Pressed(), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
        }
        var last = Target(rig, finger, handle);
        last.RaiseEvent(new PointerReleasedEventArgs(last, finger, rig.Window, start + new Point(0, 2 * RowHeight), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Recently Played", "Recently Added", "Continue Playing" }, rig.Items);
        Assert.Equal(ScrolledTo, rig.Scroller.Offset.Y);
    }
}
