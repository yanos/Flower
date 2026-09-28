using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
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

    // The most the bar draws at full size (MobileTabs.RoomyShown): the
    // default without Home, which is what the full-size measurements need.
    private static readonly IReadOnlyList<MobileTab> SixTabs =
        MobileTabs.Default.Where(t => t != MobileTab.Home).ToList();

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public MobileMainView View { get; }
        public MobileMainViewModel Vm => _parts.Mobile;

        private readonly MainViewModelHarness.MobileParts _parts;

        public Harness(double width, System.Collections.Generic.IReadOnlyList<MobileTab>? tabs = null, bool labels = true)
        {
            var tracks = Enumerable.Range(0, 20).Select(i => new Track
            {
                Title = $"Track {i:D2}", Path = $"/music/{i}.mp3",
                Album = $"Album {i / 5}", Artists = $"Artist {i / 5}",
            }).ToList();

            // The bar the app ships with, since this is about how it looks.
            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks), tabs: tabs ?? MobileTabs.Default);
            _parts.Mobile.ShowTabLabels = labels;
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

        public Border Oval => Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TabOval");

        // The mini player's, which is 56 tall. Still in the tree while nothing
        // is playing, just not visible. So is the filter oval, drawn to the
        // same shape.
        public Border MiniPlayer => Window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Height == 56 && b.Name is not ("FilterOval" or "TabOval"));

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
    public void The_default_tabs_are_in_the_bar_in_reading_order(double width)
    {
        using var h = new Harness(width);

        Assert.Equal(
            new[] { "Home", "Albums", "Artists", "Songs", "Playlists", "Queue", "Search" },
            h.Tabs.Select(t => Harness.LabelOf(t).Text));
    }

    // Seven are drawn smaller than six, not squeezed into the same size: the
    // three tests above measure that they still fit, this that it is the
    // seventh that does it.
    [AvaloniaFact]
    public void Only_a_full_bar_draws_its_tabs_smaller()
    {
        using var six = new Harness(NarrowPhone, SixTabs);
        Assert.All(six.Tabs, t => Assert.DoesNotContain("compact", t.Classes));
        six.Vm.ToggleTabShownCommand.Execute(six.Vm.TabSettingRows.Single(r => r.Tab == MobileTab.Home));
        six.Layout(NarrowPhone);

        Assert.Equal(7, six.Tabs.Count);
        Assert.All(six.Tabs, t => Assert.Contains("compact", t.Classes));
        Assert.All(six.Tabs, t => Assert.Equal(10, Harness.LabelOf(t).FontSize));
    }

    // Names off: no label under any icon, the icons drawn bigger into the
    // room the names leave, and the oval exactly the size it was.
    [AvaloniaFact]
    public void With_names_off_the_icons_grow_and_the_oval_stays_the_same()
    {
        using var h = new Harness(NarrowPhone);
        var withNames = h.Oval.Bounds.Size;

        h.Vm.ShowTabLabels = false;
        h.Layout(NarrowPhone);

        Assert.All(h.Tabs, t => Assert.False(Harness.LabelOf(t).IsVisible));
        Assert.All(h.Tabs, t =>
        {
            var icon = t.GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains("tabIcon"));
            Assert.True(icon.RenderTransform is ScaleTransform { ScaleX: > 1 } ||
                        icon.RenderTransform?.Value.M11 > 1, $"{Harness.LabelOf(t).Text}'s icon was not enlarged");
        });
        Assert.Equal(withNames, h.Oval.Bounds.Size);
    }

    // The bar is the user's to arrange in Settings: what it shows follows the
    // choice at once, and a tab taken out is out of the tree, not just hidden.
    [AvaloniaFact]
    public void The_bar_follows_the_tabs_chosen_in_settings()
    {
        using var h = new Harness(NarrowPhone);
        var songs = h.Vm.TabSettingRows.Single(r => r.Tab == MobileTab.Songs);
        h.Vm.MoveTabUpCommand.Execute(songs);
        h.Vm.ToggleTabShownCommand.Execute(h.Vm.TabSettingRows.Single(r => r.Tab == MobileTab.Queue));
        h.Layout(NarrowPhone);

        Assert.Equal(
            new[] { "Home", "Albums", "Songs", "Artists", "Playlists", "Search" },
            h.Tabs.Select(t => Harness.LabelOf(t).Text));
    }

    // Six circles in a row that was drawn for five: each has to keep its own
    // column, or two of them light up as one smear on a press.
    [AvaloniaTheory]
    [InlineData(NarrowPhone, false)]
    [InlineData(LargePhone, false)]
    [InlineData(NarrowPhone, true)]
    [InlineData(LargePhone, true)]
    public void No_tab_overlaps_the_one_beside_it(double width, bool allSeven)
    {
        using var h = new Harness(width, allSeven ? MobileTabs.All : SixTabs);
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
    [InlineData(NarrowPhone, false)]
    [InlineData(LargePhone, false)]
    [InlineData(NarrowPhone, true)]
    [InlineData(LargePhone, true)]
    public void The_row_of_tabs_fits_inside_the_oval(double width, bool allSeven)
    {
        using var h = new Harness(width, allSeven ? MobileTabs.All : SixTabs);
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
    [InlineData(NarrowPhone, false)]
    [InlineData(LargePhone, false)]
    [InlineData(NarrowPhone, true)]
    [InlineData(LargePhone, true)]
    public void Every_label_fits_its_tab_without_being_clipped(double width, bool allSeven)
    {
        using var h = new Harness(width, allSeven ? MobileTabs.All : SixTabs);

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

    // Now Playing keeps the tab oval: it is drawn over the sheet, and the
    // sheet's own controls stop above it rather than running on underneath.
    // The mini player goes, being only a smaller copy of the sheet it opens:
    // down under the tab oval, where it ends up wholly behind it.
    [AvaloniaTheory]
    [InlineData(NarrowPhone)]
    [InlineData(LargePhone)]
    public void Now_playing_keeps_the_tab_oval_clear_of_its_controls(double width)
    {
        using var h = new Harness(width);
        h.Vm.PlaylistControl.Play(h.Vm.Main.Library.Tracks[0]);
        h.Vm.OpenNowPlayingCommand.Execute(null);
        Harness.Pump(500);
        h.Layout(width);

        var sheet = h.View.FindControl<Flower.Controls.SlidingSheet>("NowPlayingSheet")!;
        var root = h.View.FindControl<Grid>("Root")!;
        var chrome = h.View.FindControl<StackPanel>("BottomChrome")!;
        Assert.True(sheet.IsVisible);
        Assert.True(root.Children.IndexOf(chrome) > root.Children.IndexOf(sheet),
            "the tab oval is drawn under the Now Playing sheet");

        var body = h.Window.GetVisualDescendants().OfType<Flower.Controls.NowPlayingBodyPanel>().Single();
        Assert.True(h.InWindow(body).Bottom <= h.InWindow(h.Oval).Top,
            $"the sheet's controls ({h.InWindow(body)}) run under the tab oval ({h.InWindow(h.Oval)})");

        var mini = (Control)h.MiniPlayer.Parent!;
        Assert.Equal(0, mini.Opacity);
        Assert.False(mini.IsHitTestVisible);
        var tucked = h.MiniPlayer.TranslatePoint(default, h.Window)!.Value;
        var oval = h.InWindow(h.Oval);
        Assert.True(tucked.Y >= oval.Top && tucked.Y + h.MiniPlayer.Bounds.Height <= oval.Bottom,
            $"the mini player came to rest at y={tucked.Y}, not behind the tab oval ({oval})");

        // And back up out of it when the sheet closes.
        h.Vm.CloseSheetCommand.Execute(null);
        Harness.Pump(500);
        Assert.Equal(1, mini.Opacity);
        Assert.True(mini.IsHitTestVisible);
        Assert.True(h.InWindow(h.MiniPlayer).Bottom <= oval.Top,
            $"the mini player ({h.InWindow(h.MiniPlayer)}) never came back out from under the tab oval ({oval})");
    }

    // The search box's keyboard has no key to put it away, so a tap on the
    // empty part of the screen does it: the box loses focus, which is what
    // drops the keyboard on a phone.
    [AvaloniaFact]
    public void A_tap_on_empty_space_puts_the_search_box_down()
    {
        using var h = new Harness(NarrowPhone);
        var box = OpenSearch(h);

        var empty = new Point(NarrowPhone / 2, 560);
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
        using var h = new Harness(NarrowPhone);
        var box = OpenSearch(h);

        var tab = h.InWindow(h.Tabs.Single(t => Harness.LabelOf(t).Text == "Search")).Center;
        h.Window.MouseDown(tab, Avalonia.Input.MouseButton.Left);
        h.Window.MouseUp(tab, Avalonia.Input.MouseButton.Left);
        Harness.Pump(200);

        Assert.True(box.IsFocused, $"focus ended on {h.Window.FocusManager?.GetFocusedElement()}");
    }

    // The prompt is its title alone now - the box is right above it.
    [AvaloniaFact]
    public void The_search_prompt_has_no_line_under_its_title()
    {
        using var h = new Harness(NarrowPhone);
        OpenSearch(h);

        var message = ((Panel)h.Window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == h.Vm.EmptyStateTitle).Parent!).Children.OfType<TextBlock>().Last();
        Assert.False(message.IsVisible);
    }

    private static TextBox OpenSearch(Harness h)
    {
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Harness.Pump(400);
        h.Layout(NarrowPhone);
        var box = h.Window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);
        return box;
    }

    // The screen menu grows out of its button and folds back into it. What
    // matters beyond the look: it ends up whole once open, and the close the
    // animation holds back still goes through once it has played.
    [AvaloniaFact]
    public void The_screen_menu_opens_whole_and_still_closes_after_folding_away()
    {
        using var h = new Harness(NarrowPhone);
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Harness.Pump(400);
        h.Layout(NarrowPhone);
        var slot = h.Window.GetVisualDescendants().OfType<ScreenSlot>().Single(s => s.IsLive);
        var button = slot.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MenuButton");
        var flyout = (Flyout)button.Flyout!;

        flyout.ShowAt(button);
        Harness.Pump(400);
        var card = ((Control)flyout.Content!).FindAncestorOfType<FlyoutPresenter>()!;
        Assert.True(flyout.IsOpen);
        Assert.Equal(1, card.Opacity, 3);
        Assert.Equal(1, ((ScaleTransform)card.RenderTransform!).ScaleX, 3);

        flyout.Hide();
        Assert.True(flyout.IsOpen, "closed at once, with no fold to play");
        Harness.Pump(400);
        Assert.False(flyout.IsOpen);
    }

    // The two floating buttons face each other across the same 52px band, and
    // with Search gone the menu one is a single circle like the back one -
    // not the stadium the two of them used to share.
    [AvaloniaFact]
    public void The_menu_button_is_the_size_of_the_back_button()
    {
        using var h = new Harness(NarrowPhone);
        // The back button only appears on a screen with somewhere to go back
        // to - each screen carries its own (ScreenSlot).
        h.Vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Harness.Pump(400);
        h.Vm.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, "Album 0");
        Harness.Pump(600);
        h.Layout(NarrowPhone);

        var slot = h.Window.GetVisualDescendants().OfType<ScreenSlot>().Single(s => s.IsLive);
        var back = slot.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BackPill");
        var menu = slot.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MenuPill");

        Assert.True(back.IsVisible, "the back button never appeared");
        Assert.Equal(back.Bounds.Size, menu.Bounds.Size);
        Assert.Equal(40, Math.Round(menu.Bounds.Width));
    }
}
