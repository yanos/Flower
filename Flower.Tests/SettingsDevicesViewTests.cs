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

    // Both views reach their services through the one MainViewModel they are
    // handed. A per-test TrustedPeerStore is the whole point: this row exists
    // because *this* test put it there, which was impossible while the store
    // came out of the process-wide container.
    [AvaloniaFact]
    public async Task A_trusted_devices_view_lists_the_peers_its_view_model_was_given()
    {
        using var parts = BuildClient();
        await parts.Main.TrustedPeers.ApproveAsync(ServerFingerprint, "Living Room", "test-public-key");

        var view = new TrustedDevicesView(parts.Main);
        var window = Show(view);

        var rows = (view.FindControl<ListBox>("DevicesList")?.ItemsSource as IEnumerable<object>)?
            .Cast<TrustedPeerRow>().ToList();

        Assert.NotNull(rows);
        Assert.Equal("Living Room", Assert.Single(rows).Alias);

        window.Close();
    }
}
