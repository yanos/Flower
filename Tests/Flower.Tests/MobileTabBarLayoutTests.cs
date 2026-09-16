using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Xunit;

namespace Flower.Tests;

// Search moved out of the pill at the top and into the oval at the bottom,
// which made six tabs where five fitted comfortably. Six is the number that
// stops being obvious by eye: the circles shrank, the oval widened, and what
// actually sets the floor is "Playlists" staying on one line. So the oval is
// measured on the narrowest phone the app is meant for rather than reasoned
// about - and on a large one too, since widening the oval by a percentage is
// the kind of change that only fails at one end.
[Collection("PlatformDataDirectory")]
public class MobileTabBarLayoutTests : PinnedDataDirectory
{
    // The narrowest phone in circulation (SE, mini) and a large one - the two
    // ends the percentage-based oval has to hold at.
    private const double NarrowPhone = 375;
    private const double LargePhone = 430;

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MobileMainView View { get; }
        public MobileMainViewModel Vm => _parts.Mobile;

        private readonly MainViewModelHarness.MobileParts _parts;

        public Harness(double width)
        {
            var tracks = Enumerable.Range(0, 20).Select(i => new Track
            {
                Title = $"Track {i:D2}", Path = $"/music/{i}.mp3",
                Album = $"Album {i / 5}", Artists = $"Artist {i / 5}",
            }).ToList();

            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
            View = new MobileMainView { DataContext = Vm };
            // TestAppBuilder runs a bare Application with no theme, so without
            // this nothing has a control template and the tree comes back
            // empty - see AlbumDetailLayoutTests.
            Window = new Window { Width = width, Height = 800 };
            Window.Styles.Add(new FluentTheme());
            Window.Content = View;
            Window.Show();
            Layout(width);
        }

        public void Layout(double width)
        {
            Window.Width = width;
            // Twice: the first pass is what tells the view how wide it is, and
            // the layout that choice implies only lands on the next one.
            for (var i = 0; i < 2; i++)
            {
                Window.Measure(new Size(width, 800));
                Window.Arrange(new Rect(0, 0, width, 800));
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

        // The one Border given a height of its own - Border.floating is 40,
        // and the tab oval overrides it to fit a label under each icon.
        public Border Oval => Window.GetVisualDescendants().OfType<Border>().Single(b => b.Height == 64);

        // The mini player's, which is 56 tall. Still in the tree while nothing
        // is playing, just not visible.
        public Border MiniPlayer => Window.GetVisualDescendants().OfType<Border>().Single(b => b.Height == 56);

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

    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void All_six_tabs_are_in_the_bar_in_reading_order(double width)
    {
        using var h = new Harness(width);

        Assert.Equal(
            new[] { "Recent", "Songs", "Albums", "Artists", "Playlists", "Search" },
            h.Tabs.Select(t => Harness.LabelOf(t).Text));
    }

    // Six circles in a row that was drawn for five: each has to keep its own
    // column, or two of them light up as one smear on a press.
    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void No_tab_overlaps_the_one_beside_it(double width)
    {
        using var h = new Harness(width);
        var tabs = h.Tabs;

        for (var i = 1; i < tabs.Count; i++)
        {
            var left = h.InWindow(tabs[i - 1]);
            var right = h.InWindow(tabs[i]);
            Assert.True(left.Right <= right.X + 0.5,
                $"{Harness.LabelOf(tabs[i - 1]).Text} ({left}) runs into {Harness.LabelOf(tabs[i]).Text} ({right})");
        }
    }

    // And the row as a whole has to stay inside the oval it is drawn in,
    // rather than hanging off its rounded ends.
    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void The_row_of_tabs_fits_inside_the_oval(double width)
    {
        using var h = new Harness(width);
        var tabs = h.Tabs;
        var oval = h.InWindow(h.Oval);

        Assert.True(h.InWindow(tabs[0]).X >= oval.X - 0.5,
            $"the first tab ({h.InWindow(tabs[0])}) starts before the oval ({oval})");
        Assert.True(h.InWindow(tabs[^1]).Right <= oval.Right + 0.5,
            $"the last tab ({h.InWindow(tabs[^1])}) runs past the oval ({oval})");
    }

    // The real constraint, and the one that fails first: a label is laid out
    // to whatever width its column gives it, so a column too narrow for
    // "Playlists" does not throw - it just draws a clipped word. Measuring
    // what the text wants is the only way to see it.
    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void Every_label_fits_its_tab_without_being_clipped(double width)
    {
        using var h = new Harness(width);

        foreach (var tab in h.Tabs)
        {
            var label = Harness.LabelOf(tab);
            Assert.True(label.DesiredSize.Width <= tab.Bounds.Width,
                $"\"{label.Text}\" wants {label.DesiredSize.Width:F1}px in a {tab.Bounds.Width:F1}px tab");
        }
    }

    // Search is a tab now, so it is marked the way the others are - .current,
    // which colours the icon and the label together. It kept .selected while
    // it lived in the pill, which only ever tinted the icon.
    [AvaloniaFact]
    public void The_search_tab_is_marked_current_when_it_is_showing()
    {
        using var h = new Harness(NarrowPhone);
        var search = h.Tabs.Single(t => Harness.LabelOf(t).Text == "Search");
        Assert.DoesNotContain("current", search.Classes);

        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Harness.Pump(400);

        Assert.Contains("current", search.Classes);
    }

    // Every tab icon is drawn bigger than the room it takes, and a negative
    // margin gives the difference back - which is what let the glyphs grow
    // without the oval moving. Easy to break from either end: a size changed
    // without its margin, or a new icon added with none at all, and the labels
    // start drifting down while the tabs stay put. So the footprint is pinned
    // rather than the drawn size, which is different for every one of them.
    //
    // The size each icon is given, not the size it measures to. The Songs note
    // is a Path whose geometry is a resource in App.axaml, and the bare
    // Application this suite runs does not have it, so that one arranges to
    // nothing here while drawing perfectly well on a phone. The arithmetic
    // between a size and its margin is the part an edit gets wrong anyway;
    // that the icons push nothing around is what the tests above measure.
    [AvaloniaFact]
    public void Every_tab_icon_takes_the_same_room_however_big_it_is_drawn()
    {
        using var h = new Harness(NarrowPhone);

        foreach (var tab in h.Tabs)
        {
            var icon = (Control)tab.GetVisualDescendants().OfType<StackPanel>().First().Children[0];
            var footprint = icon.Width + icon.Margin.Left + icon.Margin.Right;
            Assert.True(Math.Round(footprint, 2) == 24,
                $"{Harness.LabelOf(tab).Text}: {icon.GetType().Name} takes {footprint} " +
                $"(drawn at {icon.Width}, margin {icon.Margin})");
        }
    }

    // The two ovals stacked at the bottom are one shape repeated, so they have
    // to come out the same length - which takes the same margin and the same
    // star columns, in two places that do not look at each other. The mini
    // player is shown by hand: this is about how wide it is, not about when it
    // appears.
    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void The_tab_oval_is_as_long_as_the_mini_player(double width)
    {
        using var h = new Harness(width);
        var mini = h.MiniPlayer;
        ((Control)mini.Parent!).IsVisible = true;
        h.Layout(width);

        Assert.True(mini.Bounds.Width > 0, "the mini player never got a width");
        Assert.Equal(Math.Round(h.Oval.Bounds.Width), Math.Round(mini.Bounds.Width));
    }

    // The two floating buttons face each other across the same 52px band, and
    // with Search gone the settings one is a single circle like the back one -
    // not the stadium the two of them used to share.
    [AvaloniaFact]
    public void The_settings_button_is_the_size_of_the_back_button()
    {
        using var h = new Harness(NarrowPhone);
        var back = h.View.FindControl<Border>("BackPill")!;
        var settings = h.View.FindControl<Border>("SettingsPill")!;

        // The back button only appears once there is somewhere to go back to,
        // and only once the screen has settled - see UpdateBackPill.
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Harness.Pump(400);
        h.Vm.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, "Album 0");
        Harness.Pump(600);
        h.Layout(NarrowPhone);

        Assert.True(back.IsVisible, "the back button never appeared");
        Assert.Equal(back.Bounds.Size, settings.Bounds.Size);
        Assert.Equal(40, Math.Round(settings.Bounds.Width));
    }
}
