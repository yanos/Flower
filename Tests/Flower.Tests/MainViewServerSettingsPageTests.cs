using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests;

// The browser's way into Settings, which is a sidebar row and a page rather
// than a menu item and a dialog.
//
// Worth holding down because it is a whole platform's only entry point: a
// single-view lifetime has no app menu and no second Window, so nothing else
// on that host reaches SettingsPanel at all. A row that stopped being built,
// or a view that stopped making room for it, would take Settings away in the
// browser while every desktop head stayed green.
public class MainViewServerSettingsPageTests
{
    // MusicListView service-locates its ColumnManager out of Ioc.Default at
    // construction - the one container read left in that layer, deliberately
    // (see its own constructor comment) - so MainView cannot be built at all
    // without one configured.
    public MainViewServerSettingsPageTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public void The_content_area_has_a_page_to_put_the_panel_in()
    {
        var view = new MainView();

        // By name, the way UpdateServerSettingsPage reaches it. A FindControl
        // that comes back null is the failure this catches - the page is filled
        // from code-behind, so a renamed host fails at the moment the row is
        // clicked and nowhere earlier.
        Assert.NotNull(view.FindControl<ContentControl>("ServerSettingsPageHost"));
    }

    // Off the browser there is no row, so nothing can select one - asserted as
    // the absence a desktop user would see rather than by re-testing the
    // predicate. Settings there is the app menu's, and a second way in through
    // the sidebar would be a third door to the same dialog.
    [AvaloniaFact]
    public void And_no_row_offers_it_anywhere_but_the_browser()
    {
        Assert.False(OperatingSystem.IsBrowser()); // the premise, said out loud

        using var parts = MainViewModelHarness.BuildParts(
            new Library([]), new MainPlaylist([]));

        Assert.DoesNotContain(parts.Main.SidebarItems, i => i.Kind == SidebarItemKind.ServerSettings);
        Assert.False(parts.Main.IsShowingServerSettings);
    }

    // The page and the track list are alternatives, not layers: selecting one
    // has to hide the other, because MusicListView draws its own column-header
    // row internally and a panel laid over it leaves that header peeking out
    // (see MainView.axaml's note on the device-detail pane).
    [AvaloniaFact]
    public void Showing_the_page_takes_the_track_list_off_screen()
    {
        using var parts = MainViewModelHarness.BuildParts(
            new Library([]), new MainPlaylist([]));

        // Built by hand because this host builds no such row - the assertion is
        // about what selecting one does, which is the browser's whole journey.
        var row = new SidebarItem(SidebarItemKind.ServerSettings, "Server Settings");
        parts.Main.SidebarItems.Add(row);
        parts.Main.SelectedSidebarItem = row;

        Assert.True(parts.Main.IsShowingServerSettings);
        Assert.False(parts.Main.IsShowingTrackList);
    }
}
