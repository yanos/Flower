using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Controls;

// A name copied from CopyText's menu, by right click or long press. The
// finger's press is raised by hand, as in LongPressTests.
public class CopyTextTests
{
    private sealed class Harness
    {
        public Window Window { get; }
        public Button Link { get; }
        public int Clicks { get; private set; }

        public Harness(string? name)
        {
            Link = new Button { Content = name, Command = new RelayCommand(() => Clicks++) };
            CopyText.SetText(Link, name);
            // A theme for the overlay layer the menu opens in.
            Window = new Window { Content = Link };
            Window.Styles.Add(new FluentTheme());
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void RightClick() => Link.RaiseEvent(new ContextRequestedEventArgs());

        public void Hold()
        {
            var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
            Link.RaiseEvent(new PointerPressedEventArgs(
                Link, finger, Window, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None));
            using var held = new CancellationTokenSource();
            DispatcherTimer.RunOnce(held.Cancel, TimeSpan.FromMilliseconds(700));
            Dispatcher.UIThread.MainLoop(held.Token);
        }

        public async Task<string?> CopyAsync()
        {
            var menu = CopyText.OpenMenu(Link);
            Assert.NotNull(menu);
            var copy = Assert.Single(menu!.Items.OfType<MenuItem>());
            Assert.Equal("Copy", copy.Header);
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            return await Window.Clipboard!.TryGetTextAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_right_click_copies_the_name()
    {
        var h = new Harness("Add N to (X)");

        h.RightClick();

        Assert.Equal("Add N to (X)", await h.CopyAsync());
    }

    // Lifting the finger after the menu opens does not follow the link.
    [AvaloniaFact]
    public async Task A_long_press_copies_the_name()
    {
        var h = new Harness("Broadcast");

        h.Hold();

        Assert.Equal("Broadcast", await h.CopyAsync());
        Assert.Equal(0, h.Clicks);
    }

    [AvaloniaFact]
    public void Nothing_opens_without_a_name()
    {
        var h = new Harness("Stereolab");
        CopyText.SetText(h.Link, null);

        h.RightClick();

        Assert.Null(CopyText.OpenMenu(h.Link));
    }
}
