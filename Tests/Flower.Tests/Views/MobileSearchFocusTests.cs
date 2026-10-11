using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Xunit;

namespace Flower.Tests.Views;

// The phone's search box and the keyboard it raises. A phone keyboard has no
// key that puts it away, so what does is the box losing focus - which only a
// laid-out view, with real taps landing on real controls, can show.
[Collection("PlatformDataDirectory")]
public class MobileSearchFocusTests : PinnedDataDirectory
{
    private const double Width = 375;

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MobileMainView View { get; }
        public MobileMainViewModel Vm => _parts.Mobile;

        private readonly MainViewModelHarness.MobileParts _parts;

        public Harness()
        {
            var tracks = Enumerable.Range(0, 20).Select(i => new Track
            {
                Title = $"Track {i:D2}", Path = $"/music/{i}.mp3",
                Album = $"Album {i / 5}", Artists = $"Artist {i / 5}",
            }).ToList();

            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks), tabs: MobileTabs.Default);
            View = new MobileMainView { DataContext = Vm };
            // TestAppBuilder runs a bare Application with no theme, so without
            // this nothing has a control template and the tree comes back
            // empty.
            Window = new Window { Width = Width, Height = 800 };
            Window.Styles.Add(new FluentTheme());
            // The app's colours too: the tab showing is drawn on a brush of
            // Theme.axaml's, and without it that tab has no background at all
            // - which leaves it nothing to be hit on, and a tap on it falls
            // through to the list behind.
            Window.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new System.Uri("avares://Flower/"))
            {
                Source = new System.Uri("avares://Flower/Theme.axaml"),
            });
            Window.Content = View;
            Window.Show();
            Layout();
        }

        public void Layout()
        {
            // Twice: the first pass is what tells the view how wide it is, and
            // the layout that choice implies only lands on the next one.
            for (var i = 0; i < 2; i++)
            {
                Window.Measure(new Size(Width, 800));
                Window.Arrange(new Rect(0, 0, Width, 800));
                Window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }

        public static void Pump(int milliseconds = 150)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        // Left to right, which is the order they are read in and the order
        // swipe-paging walks (MobileTab's own declaration).
        public List<Button> Tabs => Window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("tab"))
            .OrderBy(b => InWindow(b).X)
            .ToList();

        public Rect InWindow(Visual control) =>
            new(control.TranslatePoint(default, Window) ?? default, control.Bounds.Size);

        public static TextBlock LabelOf(Button tab) =>
            tab.GetVisualDescendants().OfType<TextBlock>().First();

        public void Dispose()
        {
            Pump(300);
            Window.Close();
            _parts.Dispose();
        }
    }

    // The search box's keyboard has no key to put it away, so a tap on the
    // empty part of the screen does it: the box loses focus, which is what
    // drops the keyboard on a phone.
    [AvaloniaFact]
    public void A_tap_on_empty_space_puts_the_search_box_down()
    {
        using var h = new Harness();
        var box = OpenSearch(h);

        var empty = new Point(Width / 2, 560);
        h.Window.MouseDown(empty, Avalonia.Input.MouseButton.Left);
        h.Window.MouseUp(empty, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(box.IsFocused);
    }

    // A tap on a control is that control's, not empty space: the Search tab
    // hands the focus back to the box (SearchTab_Click), and ends with it
    // there rather than with the keyboard put away.
    [AvaloniaFact]
    public void A_tap_on_the_search_tab_leaves_the_search_box_up()
    {
        using var h = new Harness();
        var box = OpenSearch(h);

        var tab = h.InWindow(h.Tabs.Single(t => Harness.LabelOf(t).Text == "Search")).Center;
        h.Window.MouseDown(tab, Avalonia.Input.MouseButton.Left);
        h.Window.MouseUp(tab, Avalonia.Input.MouseButton.Left);
        Harness.Pump(200);

        Assert.True(box.IsFocused, $"focus ended on {h.Window.FocusManager?.GetFocusedElement()}");
    }

    private static TextBox OpenSearch(Harness h)
    {
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Harness.Pump(400);
        h.Layout();
        var box = h.Window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
        return box;
    }
}
