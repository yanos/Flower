using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.ViewModels;

// The browser's way into Settings, which is a sidebar row and a page rather
// than a menu item and a dialog.
//
// Worth holding down because it is a whole platform's only entry point: a
// single-view lifetime has no app menu and no second Window, so nothing else
// on that host reaches SettingsPanel at all. A row that stopped being built,
// or a view that stopped making room for it, would take Settings away in the
// browser while every desktop head stayed green.
//
// Pinned like every other MainViewModelHarness caller. It was not, so it opened
// flower.db in the developer's real data directory on a desktop run, and on
// the iOS simulator - in whatever directory a concurrently running pinned test
// had just set - it failed with SQLite's 'disk I/O error'.
[Collection("PlatformDataDirectory")]
public class MainViewServerSettingsPageTests : PinnedDataDirectory
{
    // MusicListView service-locates its ColumnManager out of Ioc.Default at
    // construction - the one container read left in that layer, deliberately
    // (see its own constructor comment) - so MainView cannot be built at all
    // without one configured.
    public MainViewServerSettingsPageTests() => TestIoc.EnsureConfigured();

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
}
