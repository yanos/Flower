using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests;

// The Navigation Bar page reorders the bar by dragging a row's handle. The
// arithmetic is MobileMainViewModel.MoveTab's (MobileTabsTests); what is here
// is that a finger on a handle actually reaches it, from where it lets go.
[Collection("PlatformDataDirectory")]
public class NavigationBarSettingsViewTests : PinnedDataDirectory
{
    private static readonly MobileTab[] Bar =
        [MobileTab.Home, MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Playlists, MobileTab.Search];

    private sealed class Harness : IDisposable
    {
        private readonly MainViewModelHarness.MobileParts _parts;

        public Window Window { get; }
        public NavigationBarSettingsView View { get; }
        public MobileMainViewModel Vm => _parts.Mobile;

        public Harness()
        {
            var tracks = Enumerable.Range(0, 3).Select(i => new Track { Title = $"T{i}", Path = $"/m/{i}.mp3" }).ToList();
            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks), tabs: Bar);
            View = new NavigationBarSettingsView { DataContext = Vm };
            Window = new Window { Width = 390, Height = 844 };
            Window.Styles.Add(new FluentTheme());
            Window.Styles.Add(new MaterialIconStyles(null));
            Window.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Flower/"))
            {
                Source = new Uri("avares://Flower/Theme.axaml"),
            });
            Window.Content = View;
            Window.Show();
            Settle();
        }

        public void Settle()
        {
            for (var i = 0; i < 2; i++)
            {
                Window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }

        public Point HandleOf(MobileTab tab)
        {
            var handle = View.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("dragHandle") && b.DataContext is MobileTabSettingRow { IsShown: true } row && row.Tab == tab);
            return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), Window)!.Value;
        }

        public void Drag(MobileTab tab, double by)
        {
            var from = HandleOf(tab);
            Window.MouseDown(from, MouseButton.Left);
            Window.MouseMove(from + new Point(0, by / 2));
            Window.MouseMove(from + new Point(0, by));
            Window.MouseUp(from + new Point(0, by), MouseButton.Left);
            Settle();
        }

        public void Dispose()
        {
            Window.Close();
            _parts.Dispose();
        }
    }

    // Rows are 50 high: a row and a half up is past the midpoint of the one
    // above that, so it lands two places up.
    [AvaloniaFact]
    public void Dragging_a_handle_up_moves_the_tab_up_the_bar()
    {
        using var h = new Harness();

        h.Drag(MobileTab.Playlists, -75);

        Assert.Equal(
            new[] { MobileTab.Home, MobileTab.Albums, MobileTab.Playlists, MobileTab.Artists, MobileTab.Songs, MobileTab.Search },
            h.Vm.VisibleTabs);
    }

    // Far past the end is the end, not nothing.
    [AvaloniaFact]
    public void Dragging_past_the_end_of_the_bar_puts_the_tab_last()
    {
        using var h = new Harness();

        h.Drag(MobileTab.Home, 2000);

        Assert.Equal(
            new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Playlists, MobileTab.Search, MobileTab.Home },
            h.Vm.VisibleTabs);
    }

    // Short of halfway to the next row is where it started.
    [AvaloniaFact]
    public void A_short_drag_leaves_the_bar_as_it_was()
    {
        using var h = new Harness();

        h.Drag(MobileTab.Songs, 20);

        Assert.Equal(Bar, h.Vm.VisibleTabs);
    }

    // What the finger let go of is put back: nothing stays lifted or offset.
    [AvaloniaFact]
    public void After_a_drop_no_row_is_left_lifted()
    {
        using var h = new Harness();

        h.Drag(MobileTab.Artists, 60);

        Assert.DoesNotContain(h.View.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("lifted"));
        Assert.All(h.View.VisibleList.GetRealizedContainers(), c => Assert.Null(c.RenderTransform));
    }
}
