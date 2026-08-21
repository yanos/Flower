using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests;

// Settings' Devices tab - TrustedDevicesView on a Server, ServerPickerView on
// a Client. Both used to resolve every dependency they had out of Ioc.Default
// in their own field initializers, which is precisely why neither had a test:
// the process-wide container can only be configured once, so there was no way
// to build one of these against anything but whatever the whole test run
// shared. They take a MainViewModel now and read the rest off it - see
// docs/ARCHITECTURE-REVIEW.md Tier 2.3.
//
// ServerPickerView additionally carries the leak half of the same finding
// (Tier 4.2): it is transient - a fresh instance every time Settings opens or
// the Server checkbox is toggled - while the two things it listens to (mDNS
// discovery, the ViewModel) live as long as the process.
[Collection("PlatformDataDirectory")]
public class SettingsDevicesViewTests : PinnedDataDirectory
{
    private const string ServerFingerprint = "fp-server";
    private static readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("192.168.1.10"), 4533);

    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"alias":"Living Room","fingerprint":"{{ServerFingerprint}}","isServer":true,"trustsCaller":true}"""),
            });
    }

    private static MainViewModelHarness.Parts BuildClient() =>
        MainViewModelHarness.BuildParts(
            new Library(new List<Track>()),
            new MainPlaylist(new List<Track>()),
            new AppSettings { IsServer = false },
            discoveryHttpClient: new HttpClient(new FakeInfoHandler()));

    // Puts the control in a real (headless) window, because everything under
    // test here hangs off attach/detach rather than off the constructor.
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void DiscoverTheServer(MainViewModelHarness.Parts parts)
    {
        parts.MdnsBackend.RaiseInstanceFound("living-room._flowersync._tcp.local", ServerEndPoint);
        Assert.True(
            SpinWait.SpinUntil(
                () => parts.NetworkDiscovery.KnownDevices.Any(d => d.Fingerprint == ServerFingerprint),
                TimeSpan.FromSeconds(5)),
            "the /info handshake never resolved the peer's fingerprint");
        Dispatcher.UIThread.RunJobs();
    }

    private static int RowCount(ServerPickerView view) =>
        (view.FindControl<ListBox>("ServersList")?.ItemsSource as IEnumerable<object>)?.Count() ?? 0;

    // The live case, which is what makes the detached one below an assertion
    // about detaching rather than about the wiring never having worked.
    [AvaloniaFact]
    public void An_attached_server_picker_picks_up_a_newly_discovered_server()
    {
        using var parts = BuildClient();
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        Assert.Equal(0, RowCount(view));
        DiscoverTheServer(parts);

        Assert.Equal(1, RowCount(view));

        window.Close();
    }

    // The leak this fixes: every Settings window ever opened left another live
    // ServerPickerView rebuilding its own detached row list on every mDNS
    // packet for the rest of the process.
    [AvaloniaFact]
    public void A_detached_server_picker_stops_listening_to_discovery()
    {
        using var parts = BuildClient();
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        DiscoverTheServer(parts);

        Assert.Equal(0, RowCount(view));

        window.Close();
    }

    // TabControl detaches the content of a tab the user switches away from and
    // re-attaches the same instance on the way back, so the teardown above has
    // to be a detach rather than a dispose - and re-attaching has to re-read
    // whatever changed while it was gone, not just re-subscribe.
    [AvaloniaFact]
    public void A_re_attached_server_picker_listens_again_and_catches_up()
    {
        using var parts = BuildClient();
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        DiscoverTheServer(parts);
        Assert.Equal(0, RowCount(view));

        window.Content = view;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, RowCount(view));

        window.Close();
    }

    // The trusted-device list is now part of SettingsPanel and is fed by
    // SettingsViewModel over an ISettingsBackend, so what used to be asserted
    // against TrustedDevicesView's ItemsSource is asserted against the model that
    // fills it. A per-test TrustedPeerStore is still the whole point: this row
    // exists because *this* test put it there, which was impossible while the
    // store came out of the process-wide container.
    [AvaloniaFact]
    public async Task Settings_lists_the_trusted_peers_its_view_model_was_given()
    {
        using var parts = BuildClient();
        await parts.Main.TrustedPeers.ApproveAsync(ServerFingerprint, "Living Room", "test-public-key");

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        await settings.LoadAsync();

        Assert.Equal("Living Room", Assert.Single(settings.Devices).Alias);
    }

    // Loads the real XAML and walks every tab, which is the only thing that
    // catches a broken binding path or a DataTemplate whose x:DataType no longer
    // matches - compiled bindings fail at load, not at first paint.
    [AvaloniaFact]
    public async Task The_settings_panel_renders_every_tab()
    {
        using var parts = BuildClient();

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        var panel = new SettingsPanel(settings, parts.Main);
        var window = Show(panel);
        Dispatcher.UIThread.RunJobs();

        var tabs = panel.GetVisualDescendants().OfType<TabControl>().Single();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
        }

        // Five declared - General, Library, Devices, Network, Logs - of which the
        // last two are collapsed for a local backend (see SettingsCapabilities).
        Assert.Equal(5, tabs.ItemCount);
        await settings.LoadAsync();

        window.Close();
    }

    // The capability flags are what let one SettingsPanel serve both this device
    // and a remote server, so a regression in them silently shows the wrong
    // controls rather than failing anything.
    [AvaloniaFact]
    public async Task Local_settings_offer_the_app_only_controls_and_not_the_server_ones()
    {
        using var parts = BuildClient();

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        await settings.LoadAsync();

        Assert.True(settings.Capabilities.ThemePicker);
        Assert.True(settings.Capabilities.ITunesIntegration);
        Assert.True(settings.Capabilities.SyncRole);
        Assert.True(settings.Capabilities.RebuildDatabase);

        Assert.False(settings.Capabilities.ServerNetwork);
        Assert.False(settings.Capabilities.PairingCodes);
        Assert.False(settings.Capabilities.SubsonicCredentials);
        Assert.False(settings.Capabilities.Log);
    }

    // An unpaired Client still curates its own library; only "Client, paired"
    // disables it, and ticking "Act as Server" re-enables it straight away rather
    // than after a save-and-reopen.
    [AvaloniaFact]
    public async Task An_unpaired_client_can_still_manage_its_own_library()
    {
        using var parts = BuildClient();

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        await settings.LoadAsync();

        Assert.True(settings.CanManageLibrary);

        settings.IsServer = true;
        Assert.True(settings.CanManageLibrary);
    }
}
