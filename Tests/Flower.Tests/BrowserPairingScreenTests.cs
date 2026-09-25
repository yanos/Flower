using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// The browser head's pairing screen and what pairing decides afterwards. A tab
// the server does not know shows nothing but a box for a code; a tab that
// cannot hold a key is told why instead; and only a tab the server made an
// administrator is offered Server Settings. The browser-only wiring
// (BrowserPeerCredentials, the page reload) is handed in as delegates, which is
// what these stand in for.
[Collection("PlatformDataDirectory")]
public class BrowserPairingScreenTests : PinnedDataDirectory
{
    private MainViewModel Build() =>
        Own(MainViewModelHarness.Build(new Library(new List<Track>()), new MainPlaylist(new List<Track>()))).Main;

    [AvaloniaFact]
    public void An_unpaired_tab_is_shown_the_code_box()
    {
        var vm = Build();

        vm.IsBrowserUnpaired = true;

        Assert.True(vm.ShowsBrowserPairingScreen);
        Assert.True(vm.CanEnterBrowserPairingCode);
    }

    // No key, no pairing: a code box would send its user round a loop.
    [AvaloniaFact]
    public void A_tab_that_cannot_hold_a_key_is_told_why_and_offered_no_box()
    {
        var vm = Build();

        vm.IsBrowserUnpaired = true;
        vm.BrowserCannotPairReason = "reach this server over https";

        Assert.True(vm.ShowsBrowserPairingScreen);
        Assert.False(vm.CanEnterBrowserPairingCode);
    }

    // Until the server answers, the tab doesn't know which page it is, so it
    // shows a blank one rather than a flash of the library.
    [AvaloniaFact]
    public void A_tab_waiting_for_the_server_is_blank_and_then_shows_what_it_was_told()
    {
        var vm = Build();

        vm.IsCheckingBrowserPairing = true;
        Assert.True(vm.CoversBrowserPage);
        Assert.False(vm.ShowsBrowserPairingScreen);

        vm.IsBrowserUnpaired = true;
        vm.IsCheckingBrowserPairing = false;
        Assert.True(vm.CoversBrowserPage);
        Assert.True(vm.ShowsBrowserPairingScreen);

        vm.IsBrowserUnpaired = false;
        Assert.False(vm.CoversBrowserPage);
    }

    [AvaloniaFact]
    public void A_paired_tab_sees_the_app_and_no_pairing_screen()
    {
        var vm = Build();

        Assert.False(vm.ShowsBrowserPairingScreen);
    }

    [AvaloniaFact]
    public async Task A_code_the_server_takes_starts_the_page_over()
    {
        var vm = Build();
        vm.IsBrowserUnpaired = true;
        string? spent = null;
        var reloaded = false;
        vm.BrowserPairer = code => { spent = code; return Task.FromResult<string?>(null); };
        vm.AfterBrowserPaired = () => reloaded = true;
        vm.BrowserPairingCode = "G6RJR";

        await vm.PairBrowserCommand.ExecuteAsync(null);

        Assert.Equal("G6RJR", spent);
        Assert.True(reloaded);
        Assert.Null(vm.BrowserPairingError);
    }

    [AvaloniaFact]
    public async Task A_refused_code_is_said_under_the_box_and_the_page_stays()
    {
        var vm = Build();
        vm.IsBrowserUnpaired = true;
        var reloaded = false;
        vm.BrowserPairer = _ => Task.FromResult<string?>("That code wasn't accepted.");
        vm.AfterBrowserPaired = () => reloaded = true;

        await vm.PairBrowserCommand.ExecuteAsync(null);

        Assert.Equal("That code wasn't accepted.", vm.BrowserPairingError);
        Assert.False(reloaded);
        Assert.False(vm.IsPairingBrowser);
    }

    // A listener's tab plays music; only an administrator's gets the settings.
    [AvaloniaFact]
    public void Server_Settings_is_in_the_sidebar_only_for_an_administrators_tab()
    {
        var vm = Build();
        vm.HostsServerSettingsPage = true;
        bool HasSettingsRow() => vm.SidebarItems.Any(i => i.Kind == SidebarItemKind.ServerSettings);

        Assert.False(HasSettingsRow());

        vm.IsBrowserAdmin = true;
        Assert.True(HasSettingsRow());
        Assert.Contains(vm.SidebarItems, i => i is { Kind: SidebarItemKind.Header, Name: "Server" });

        vm.IsBrowserAdmin = false;
        Assert.False(HasSettingsRow());
        Assert.DoesNotContain(vm.SidebarItems, i => i is { Kind: SidebarItemKind.Header, Name: "Server" });
    }

    // A page opened at #page=settings asks before the server has said whether
    // this tab may see the page. It lands there once the answer is yes - and
    // not at all for a listener.
    [AvaloniaFact]
    public void A_settings_link_opened_before_the_server_answered_lands_on_the_page_once_it_does()
    {
        var vm = Build();
        vm.HostsServerSettingsPage = true;

        vm.RequestServerSettingsPage();
        Assert.NotEqual(SidebarItemKind.ServerSettings, vm.SelectedSidebarItem?.Kind);

        vm.IsBrowserAdmin = true;

        Assert.Equal(SidebarItemKind.ServerSettings, vm.SelectedSidebarItem?.Kind);
    }

    [AvaloniaFact]
    public void Losing_administrator_while_on_the_settings_page_goes_back_to_the_library()
    {
        var vm = Build();
        vm.HostsServerSettingsPage = true;
        vm.IsBrowserAdmin = true;
        vm.RequestServerSettingsPage();

        vm.IsBrowserAdmin = false;

        Assert.Equal(SidebarItemKind.Songs, vm.SelectedSidebarItem?.Kind);
    }
}
