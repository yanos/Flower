using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

using Material.Icons;
using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests;

// Context menus drawn by the operating system (an NSMenu on macOS): what an
// Avalonia ContextMenu becomes when handed over, and which openings are handed
// over - every menu the app opens from code, and those declared in XAML, which
// Avalonia opens on its own.
[Collection("PlatformContextMenu")]
public class PlatformContextMenuTests : IDisposable
{
    private readonly IPlatformContextMenu? _previous = PlatformContextMenu.Current;

    public void Dispose() => PlatformContextMenu.Current = _previous;

    private sealed class Recorder(bool shows = true) : IPlatformContextMenu
    {
        public List<IReadOnlyList<ContextMenuEntry>> Shown { get; } = new();

        public bool TryShow(IReadOnlyList<ContextMenuEntry> entries, Control target)
        {
            Shown.Add(entries);
            return shows;
        }
    }

    private static string[] Titles(IEnumerable<ContextMenuEntry> entries) =>
        entries.Select(e => e.IsSeparator ? "---" : e.Title).ToArray();

    [AvaloniaFact]
    public void Hidden_items_are_left_out_and_the_separators_they_strand_go_with_them()
    {
        var entries = ContextMenuEntry.From(new object[]
        {
            new Separator(),
            new MenuItem { Header = "Play Next" },
            new MenuItem { Header = "Remove from Queue", IsVisible = false },
            new Separator(),
            new Separator(),
            new MenuItem { Header = "Get Info" },
            new Separator(),
            new MenuItem { Header = "Download", IsVisible = false },
        });

        Assert.Equal(new[] { "Play Next", "---", "Get Info" }, Titles(entries));
    }

    [AvaloniaFact]
    public void An_item_reads_as_the_user_would_see_it()
    {
        ICommand never = new RelayCommand(() => { }, () => false);
        var entries = ContextMenuEntry.From(new object[]
        {
            new MenuItem { Header = "Title", Icon = new MaterialIcon { Kind = MaterialIconKind.Check } },
            new MenuItem { Header = "Rename", IsEnabled = false },
            new MenuItem { Header = "Delete", Command = never },
            new MenuItem { Header = "Add To Playlist", Items = { new MenuItem { Header = "New Playlist" }, new MenuItem { Header = "Road Trip" } } },
        });

        Assert.True(entries[0].IsChecked);
        Assert.False(entries[1].IsEnabled);
        Assert.False(entries[2].IsEnabled);
        Assert.True(entries[3].IsEnabled);
        Assert.Null(entries[3].Activate);
        Assert.Equal(new[] { "New Playlist", "Road Trip" }, Titles(entries[3].Children));
    }

    // Picking an entry clicks the MenuItem it came from, so whatever that
    // item does - a Click handler, a Command - runs as it would from
    // Avalonia's menu.
    [AvaloniaFact]
    public void Picking_an_entry_clicks_its_menu_item()
    {
        var clicked = 0;
        var commanded = 0;
        var byHandler = new MenuItem { Header = "Play Next" };
        byHandler.Click += (_, _) => clicked++;
        var byCommand = new MenuItem { Header = "Shuffle", Command = new RelayCommand(() => commanded++) };

        var entries = ContextMenuEntry.From(new object[] { byHandler, byCommand });
        entries[0].Activate!();
        entries[1].Activate!();

        Assert.Equal(1, clicked);
        Assert.Equal(1, commanded);
    }

    [AvaloniaFact]
    public void A_menu_opened_from_code_is_handed_over_and_avalonias_stays_shut()
    {
        var native = new Recorder();
        PlatformContextMenu.Current = native;
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Get Info" } } };
        var window = Show(new Border(), out var target);

        PlatformContextMenu.Open(menu, target);

        Assert.Equal(new[] { "Get Info" }, Titles(Assert.Single(native.Shown)));
        Assert.False(menu.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void When_the_platform_cannot_show_it_avalonias_opens_instead()
    {
        PlatformContextMenu.Current = new Recorder(shows: false);
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Get Info" } } };
        var window = Show(new Border(), out var target);

        PlatformContextMenu.Open(menu, target);

        Assert.True(menu.IsOpen);
        menu.Close();
        window.Close();
    }

    // A XAML-declared menu (Home's Continue Playing tiles) is opened by
    // Avalonia on a right-click, never through Open - and its bindings have
    // not resolved yet, since it has never been in the visual tree.
    [AvaloniaFact]
    public void A_declared_menu_is_handed_over_on_a_right_click_with_its_bindings_live()
    {
        var native = new Recorder();
        PlatformContextMenu.Current = native;
        var resumed = 0;
        var item = new MenuItem { Header = "Continue Playing" };
        item.Bind(MenuItem.CommandProperty, new Avalonia.Data.Binding(nameof(TileData.Resume)));
        var button = new Button { DataContext = new TileData(() => resumed++), ContextMenu = new ContextMenu { Items = { item } } };
        var window = Show(button, out _);

        button.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(Assert.Single(native.Shown));
        Assert.False(button.ContextMenu.IsOpen);
        entry.Activate!();
        Assert.Equal(1, resumed);
        window.Close();
    }

    // Home's tiles are recycled as its rows scroll: the same Button, and so
    // the same declared menu, shows another album. Picking from the menu has
    // to act on the album the tile shows now.
    [AvaloniaFact]
    public void A_recycled_tile_s_menu_acts_on_the_album_it_shows_now()
    {
        var native = new Recorder();
        PlatformContextMenu.Current = native;
        var resumed = new List<string>();
        var item = new MenuItem { Header = "Continue Playing" };
        item.Bind(MenuItem.CommandProperty, new Avalonia.Data.Binding(nameof(TileData.Resume)));
        var button = new Button { DataContext = new TileData(() => resumed.Add("Blue")), ContextMenu = new ContextMenu { Items = { item } } };
        var window = Show(button, out _);
        button.RaiseEvent(new ContextRequestedEventArgs());

        button.DataContext = new TileData(() => resumed.Add("Red"));
        button.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        native.Shown[^1][0].Activate!();

        Assert.Equal(new[] { "Red" }, resumed);
        window.Close();
    }

    private sealed class TileData(Action resume)
    {
        public ICommand Resume { get; } = new RelayCommand(resume);
    }

    private static Window Show(Control content, out Control target)
    {
        var window = new Window { Content = content };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        target = content;
        return window;
    }
}
