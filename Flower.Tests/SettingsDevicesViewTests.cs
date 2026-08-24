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

    // Answers per host rather than for everything, so a test can also stand up
    // an address that is *not* a server - which is what an address a server
    // has stopped reporting, or one typed with a typo in it, looks like from
    // here.
    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Alias, string Fingerprint)> _serversByHost =
            new() { [ServerEndPoint.Address.ToString()] = ("Living Room", ServerFingerprint) };

        public void AddServer(string host, string alias, string fingerprint) =>
            _serversByHost[host] = (alias, fingerprint);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_serversByHost.TryGetValue(request.RequestUri!.Host, out var server))
                throw new HttpRequestException("simulated unreachable peer");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"alias":"{{server.Alias}}","fingerprint":"{{server.Fingerprint}}","isServer":true,"trustsCaller":true}"""),
            });
        }
    }

    private static MainViewModelHarness.Parts BuildClient(FakeInfoHandler? handler = null) =>
        MainViewModelHarness.BuildParts(
            new Library(new List<Track>()),
            new MainPlaylist(new List<Track>()),
            new AppSettings(),
            discoveryHttpClient: new HttpClient(handler ?? new FakeInfoHandler()));

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

    private static IReadOnlyList<ServerRow> Rows(ServerPickerView view) =>
        (view.FindControl<ListBox>("ServersList")?.ItemsSource as IEnumerable<ServerRow>)?.ToList() ?? [];

    private static int RowCount(ServerPickerView view) => Rows(view).Count;

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

    // One server is one row, however many ways this device knows to reach it.
    // A server reports every address it holds, over both schemes, and each one
    // is registered as its own remembered peer - so a perfectly ordinary
    // server with an IPv4 and an IPv6 address produced four rows, three of
    // them labelled with a raw URL and none of them pairable.
    [AvaloniaFact]
    public async Task Every_way_of_reaching_one_server_is_still_one_row()
    {
        using var parts = BuildClient();
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        DiscoverTheServer(parts);
        await parts.NetworkDiscovery.AddRememberedAsync($"http://{ServerEndPoint.Address}:{ServerEndPoint.Port}");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, RowCount(view));
        Assert.Equal("Living Room", Rows(view)[0].Alias);

        window.Close();
    }

    // An address nothing answers on is not a server anyone can pair with -
    // pairing pins a fingerprint, and this has none. It used to get a row
    // anyway, labelled with the URL and permanently disabled: the visible
    // symptom of dead addresses accumulating in discovery (see
    // PairedServerReachability.RememberAddresses).
    [AvaloniaFact]
    public async Task An_address_that_never_answers_is_not_offered_as_a_server()
    {
        using var parts = BuildClient();
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        DiscoverTheServer(parts);
        await parts.NetworkDiscovery.AddRememberedAsync("http://[fd93:887c:f753::2]:4533");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, RowCount(view));
        Assert.DoesNotContain(Rows(view), r => r.Alias.Contains("fd93"));

        window.Close();
    }

    // Two different servers may well call themselves the same thing - an alias
    // defaults to the machine name. Only then is an address worth showing, and
    // only as a subtitle: the name is the identity, the address is the tie
    // break.
    [AvaloniaFact]
    public async Task Two_servers_sharing_a_name_are_told_apart_by_their_address()
    {
        var handler = new FakeInfoHandler();
        handler.AddServer("192.168.1.11", "Living Room", "fp-other-server");

        using var parts = BuildClient(handler);
        var view = new ServerPickerView(parts.Main);
        var window = Show(view);

        DiscoverTheServer(parts);
        await parts.NetworkDiscovery.AddRememberedAsync("http://192.168.1.11:4533");
        Dispatcher.UIThread.RunJobs();

        var rows = Rows(view);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Living Room", r.Alias));
        Assert.Equal(2, rows.Select(r => r.Detail).Distinct().Count());
        Assert.All(rows, r => Assert.NotNull(r.Detail));

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
    // The local backend has no trusted-device roster at all - this device
    // accepts no incoming connections, so the list of devices allowed to sync
    // lives on the server and is edited there. Capabilities says so, and the
    // call itself refuses rather than quietly returning an empty list (see
    // ISettingsBackend's note on unsupported actions).
    [AvaloniaFact]
    public async Task This_device_has_no_trusted_device_roster_of_its_own()
    {
        using var parts = BuildClient();

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        await settings.LoadAsync();

        Assert.False(settings.Capabilities.TrustedDevices);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new LocalSettingsBackend(parts.Main).LoadDevicesAsync());
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
        Assert.True(settings.Capabilities.PairedServerPicker);
        Assert.True(settings.Capabilities.RebuildDatabase);

        Assert.False(settings.Capabilities.ServerNetwork);
        Assert.False(settings.Capabilities.TrustedDevices);
        Assert.False(settings.Capabilities.PairingCodes);
        Assert.False(settings.Capabilities.SubsonicCredentials);
        Assert.False(settings.Capabilities.Log);
    }

    // An unpaired device still curates its own library - only pairing with a
    // server takes that over, because only then is there another library for
    // this one to be a view of.
    [AvaloniaFact]
    public async Task An_unpaired_device_can_still_manage_its_own_library()
    {
        using var parts = BuildClient();

        var settings = new SettingsViewModel(new LocalSettingsBackend(parts.Main));
        await settings.LoadAsync();

        Assert.True(settings.CanManageLibrary);
    }
}
