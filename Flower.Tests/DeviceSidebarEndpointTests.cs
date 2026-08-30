using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;

using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// One place decides which address a peer is dialled at (see
// IPeerEndpointResolver), and the sidebar is not it.
//
// A server is routinely known at several addresses at once - seen on this link,
// remembered at its LAN address, at its tailnet address, at whatever it reports
// from outside - and each arrives as its own DiscoveredDevice under the same
// fingerprint. DeviceSidebarSection used to take whichever arrived last, and
// since MainViewModel's ListedPeers reads endpoints off those rows, that choice
// silently became the address sync and the log push went out on. A server two
// metres away on the same WiFi could end up dialled over the public internet.
public class DeviceSidebarEndpointTests
{
    private sealed class StubHost : IDeviceSidebarHost
    {
        public string? PairedServerFingerprint { get; set; }
        public bool IsSyncing => false;
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection => null;
        public void ForgetSyncedDevice(string fingerprint) { }
        public void DeviceRowsChanged() { }
    }

    // Stands in for NetworkDiscoveryService's ranked pick without standing up
    // an mDNS backend and an /info endpoint per case - the ranking itself is
    // NetworkDiscoveryServiceTests' subject, and what matters here is only that
    // the sidebar defers to it.
    private sealed class StubEndpoints : IPeerEndpointResolver
    {
        private readonly Dictionary<string, DiscoveredDevice> _best = new();

        public void Best(DiscoveredDevice device) => _best[device.Fingerprint] = device;

        public DiscoveredDevice? EndpointFor(string fingerprint) =>
            _best.TryGetValue(fingerprint, out var device) ? device : null;

        public IReadOnlyCollection<DiscoveredDevice> KnownDevices => _best.Values.ToList();
    }

    private static DiscoveredDevice At(string ip, string fingerprint) => new()
    {
        InstanceName = "server",
        BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Parse(ip), 4533)),
        Alias = "Server",
        Fingerprint = fingerprint,
    };

    private static (DeviceSidebarSection Section, ObservableCollection<SidebarItem> Items, StubEndpoints Endpoints) Build()
    {
        var items = new ObservableCollection<SidebarItem>();
        var endpoints = new StubEndpoints();
        return (new DeviceSidebarSection(items, new StubHost(), nicknames: null, reachability: null, endpoints), items, endpoints);
    }

    private static SidebarItem Row(ObservableCollection<SidebarItem> items) =>
        items.Single(i => i.Kind == SidebarItemKind.Device);

    // The defect, at its narrowest: a later sighting at a worse address must not
    // re-point the row away from the resolver's choice.
    [Fact]
    public void A_later_sighting_does_not_repoint_a_row_away_from_the_resolved_endpoint()
    {
        var (section, items, endpoints) = Build();
        var lan = At("192.168.1.40", "fp-server");
        endpoints.Best(lan);

        section.AddOrUpdate(lan);
        section.AddOrUpdate(At("38.133.38.247", "fp-server"));

        Assert.Equal(lan.BaseUri, Row(items).Device!.BaseUri);
    }

    // And the first sighting is no different - a row created from whichever
    // address happened to be seen first would be wrong for exactly as long as
    // nothing else touched it.
    [Fact]
    public void A_new_row_is_created_at_the_resolved_endpoint_not_the_sighting()
    {
        var (section, items, endpoints) = Build();
        var lan = At("192.168.1.40", "fp-server");
        endpoints.Best(lan);

        section.AddOrUpdate(At("38.133.38.247", "fp-server"));

        Assert.Equal(lan.BaseUri, Row(items).Device!.BaseUri);
    }

    // Discovery has not caught up with this peer yet - it is being announced to
    // the sidebar before it is resolvable. The sighting stands, rather than the
    // row losing its endpoint entirely.
    [Fact]
    public void A_peer_the_resolver_does_not_know_yet_keeps_its_own_sighting()
    {
        var (section, items, _) = Build();
        var sighting = At("192.168.1.41", "fp-unknown");

        section.AddOrUpdate(sighting);

        Assert.Equal(sighting.BaseUri, Row(items).Device!.BaseUri);
    }
}
