using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// NetworkDiscoveryService drives MainViewModel's Devices sidebar and gates
// every peer-to-peer sync feature (LibraryDownloadService, PlaylistSyncService,
// AlbumArtLoader's remote-art fetch all resolve a peer through it - see
// FindByFingerprint). Its two external dependencies - mDNS discovery and the
// /info handshake's HttpClient - are both now constructor-injectable test
// seams (IMdnsBackend, HttpClient), so these tests exercise the real
// discovery/dedup/pruning logic against a FakeMdnsBackend and a fake
// HttpMessageHandler, without a real LAN or real sockets.
public class NetworkDiscoveryServiceTests : IDisposable
{
    // Mirrors NetworkDiscoveryService's own private ServiceType constant -
    // not exposed publicly, so tests build instance names against the same
    // literal directly.
    private const string ServiceType = "_flowersync._tcp";

    // Routes a fake /info response (or a thrown "unreachable" exception) by
    // the request's port, so a single NetworkDiscoveryService instance -
    // which shares one HttpClient across every peer - can be tested against
    // several simulated peers with different behavior in the same test.
    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _responsesByPort = [];
        public int RequestCount { get; private set; }
        public List<int> RequestedPorts { get; } = [];

        public void RespondWith(int port, string json) => _responsesByPort[port] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var port = request.RequestUri!.Port;
            RequestedPorts.Add(port);

            if (!_responsesByPort.TryGetValue(port, out var json))
                throw new HttpRequestException("simulated unreachable peer");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private readonly FakeMdnsBackend _backend = new();
    private readonly FakeInfoHandler _handler = new();
    private readonly NetworkDiscoveryService _service;

    public NetworkDiscoveryServiceTests()
    {
        var identity = new DeviceIdentity { Fingerprint = "my-fp", Alias = "Me" };
        _service = new NetworkDiscoveryService(
            identity,
            NullLogger<NetworkDiscoveryService>.Instance,
            _backend,
            new HttpClient(_handler));
    }

    public void Dispose() => _service.Dispose();

    private static string InstanceName(string name) => $"{name}.{ServiceType}.local";

    private static IPEndPoint Routable(byte lastOctet, int port = 4533) => new(IPAddress.Parse($"192.168.1.{lastOctet}"), port);

    private static IPEndPoint LinkLocal(int port = 4533) => new(IPAddress.Parse("fe80::1"), port);

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), because);

    [Fact]
    public void Start_advertises_and_browses_for_our_own_service_type()
    {
        _service.Start(port: 4533);

        var advertised = Assert.Single(_backend.Advertised);
        Assert.Equal(_service.OwnInstanceName, advertised.InstanceName);
        Assert.Equal(ServiceType, advertised.ServiceType);
        Assert.Equal(4533, advertised.Port);
        Assert.Contains(ServiceType, _backend.Browsed);
    }

    [Fact]
    public void Restart_re_advertises_and_re_browses()
    {
        _service.Start(port: 4533);

        _service.Restart(port: 5000);

        Assert.Equal(2, _backend.Advertised.Count);
        Assert.Equal(5000, _backend.Advertised[^1].Port);
        Assert.Equal(2, _backend.Browsed.Count);
    }

    [Fact]
    public void OnInstanceFound_discovers_a_new_peer_and_resolves_its_info()
    {
        var endpoint = Routable(10);
        _handler.RespondWith(endpoint.Port, """{"alias":"Kitchen","fingerprint":"peer-fp","isServer":true,"trustsCaller":true}""");

        DiscoveredDevice? discovered = null;
        _service.DeviceDiscovered += (_, d) => discovered = d;

        _backend.RaiseInstanceFound(InstanceName("Kitchen-Speaker"), endpoint);

        WaitUntil(() => discovered?.Fingerprint == "peer-fp", "the peer's real alias/fingerprint should resolve via /info");
        Assert.Equal("Kitchen", discovered!.Alias);
        Assert.True(discovered.IsServer);
        Assert.True(discovered.TrustsUs);
        Assert.Same(discovered, Assert.Single(_service.KnownDevices));
    }

    [Fact]
    public void OnInstanceFound_ignores_announcements_of_a_different_service_type()
    {
        _backend.RaiseInstanceFound("SomeAirplayDevice._airplay._tcp.local", Routable(11));

        Assert.Empty(_service.KnownDevices);
    }

    [Fact]
    public void OnInstanceFound_ignores_our_own_advertisement_reflected_back()
    {
        _backend.RaiseInstanceFound($"{_service.OwnInstanceName}.{ServiceType}.local", Routable(12));

        Assert.Empty(_service.KnownDevices);
    }

    [Fact]
    public void OnInstanceFound_does_not_re_resolve_an_exact_repeat_announcement()
    {
        var endpoint = Routable(13);
        _handler.RespondWith(endpoint.Port, """{"alias":"Office","fingerprint":"office-fp"}""");
        var name = InstanceName("Office-Desktop");

        _backend.RaiseInstanceFound(name, endpoint);
        WaitUntil(() => _handler.RequestCount >= 1, "the first announcement should trigger a resolve");

        _backend.RaiseInstanceFound(name, endpoint);
        // Nothing distinguishes "no second call is coming" from "it just
        // hasn't happened yet" - give it a beat and confirm the count never
        // grows, rather than asserting immediately after the second raise.
        Thread.Sleep(200);

        Assert.Equal(1, _handler.RequestCount);
    }

    [Fact]
    public void OnInstanceFound_ignores_a_link_local_address_when_a_routable_one_is_already_known()
    {
        var routable = Routable(14);
        _handler.RespondWith(routable.Port, """{"alias":"Laptop","fingerprint":"laptop-fp"}""");
        var name = InstanceName("Laptop");
        _backend.RaiseInstanceFound(name, routable);
        WaitUntil(() => _service.KnownDevices.Count == 1, "the routable address should be discovered first");

        _backend.RaiseInstanceFound(name, LinkLocal());

        Assert.Equal(routable, Assert.Single(_service.KnownDevices).EndPoint);
    }

    [Fact]
    public void OnInstanceFound_replaces_a_link_local_address_once_a_routable_one_arrives()
    {
        var name = InstanceName("Phone");
        _backend.RaiseInstanceFound(name, LinkLocal());
        WaitUntil(() => _service.KnownDevices.Count == 1, "the link-local address should be recorded when it's all that's been seen");

        var routable = Routable(15);
        _handler.RespondWith(routable.Port, """{"alias":"Phone","fingerprint":"phone-fp"}""");
        _backend.RaiseInstanceFound(name, routable);

        Assert.Equal(routable, Assert.Single(_service.KnownDevices).EndPoint);
    }

    [Fact]
    public void InstanceLost_removes_the_peer_and_raises_DeviceLost()
    {
        var endpoint = Routable(16);
        var name = InstanceName("ToBeLost");
        _backend.RaiseInstanceFound(name, endpoint);
        WaitUntil(() => _service.KnownDevices.Count == 1, "the peer should be discovered first");

        string? lost = null;
        _service.DeviceLost += (_, n) => lost = n;
        _backend.RaiseInstanceLost(name);

        Assert.Equal(name, lost);
        Assert.Empty(_service.KnownDevices);
    }

    [Fact]
    public void A_peer_unreachable_on_every_info_attempt_is_pruned_after_three_consecutive_failures()
    {
        var name = InstanceName("FlakyPeer");
        string? lost = null;
        _service.DeviceLost += (_, n) => lost = n;

        // Three re-announcements at three different (still non-link-local,
        // all unconfigured-so-throwing) endpoints - each one falls past both
        // of OnInstanceFound's dedup guards (neither link-local nor an exact
        // repeat of the previous endpoint), so each triggers its own fresh
        // ResolveAliasAsync attempt against the shared failing HttpClient,
        // the same way three consecutive AliasPollInterval ticks against an
        // address that stopped answering would in production.
        _backend.RaiseInstanceFound(name, Routable(20));
        WaitUntil(() => _handler.RequestCount >= 1, "attempt 1 should have been made");
        _backend.RaiseInstanceFound(name, Routable(21));
        WaitUntil(() => _handler.RequestCount >= 2, "attempt 2 should have been made");
        Assert.Single(_service.KnownDevices); // two misses alone shouldn't be enough to prune yet
        _backend.RaiseInstanceFound(name, Routable(22));

        WaitUntil(() => lost == name, "the peer should be pruned after its third consecutive failed /info attempt");
        Assert.Empty(_service.KnownDevices);
    }

    [Fact]
    public void A_link_local_only_peer_is_never_pruned_no_matter_how_many_times_info_fails()
    {
        var name = InstanceName("LinkLocalOnly");
        var endpoint = LinkLocal();
        string? lost = null;
        _service.DeviceLost += (_, n) => lost = n;

        _backend.RaiseInstanceFound(name, endpoint);
        WaitUntil(() => _service.KnownDevices.Count == 1, "the link-local peer should still be recorded even though /info will fail");
        WaitUntil(() => _handler.RequestCount >= 1, "a resolve attempt should still have been made");

        // No repeat announcement can even retrigger a resolve for the exact
        // same link-local address (see the dedup test above) - the point
        // here is just that the single failure that did happen must not
        // count towards pruning at all, per HandleUnreachable's own
        // link-local carve-out.
        Thread.Sleep(200);
        Assert.Null(lost);
        Assert.Single(_service.KnownDevices);
    }

    [Fact]
    public void KnownDevices_dedupes_two_instance_names_that_resolve_to_the_same_fingerprint()
    {
        var endpointA = Routable(30);
        var endpointB = Routable(31);
        _handler.RespondWith(endpointA.Port, """{"alias":"Same Device (old name)","fingerprint":"shared-fp"}""");
        _handler.RespondWith(endpointB.Port, """{"alias":"Same Device","fingerprint":"shared-fp"}""");

        _backend.RaiseInstanceFound(InstanceName("OldName"), endpointA);
        _backend.RaiseInstanceFound(InstanceName("NewName"), endpointB);

        WaitUntil(() => _service.KnownDevices.Count == 1, "both instance names should collapse to one entry once their fingerprints match");
    }

    [Fact]
    public void FindByFingerprint_resolves_a_known_peer_by_its_stable_fingerprint()
    {
        var endpoint = Routable(40);
        _handler.RespondWith(endpoint.Port, """{"alias":"Server","fingerprint":"server-fp"}""");
        _backend.RaiseInstanceFound(InstanceName("Server"), endpoint);
        WaitUntil(() => _service.FindByFingerprint("server-fp") != null, "the peer should resolve once its /info completes");

        var found = _service.FindByFingerprint("server-fp");

        Assert.Equal(endpoint, found!.EndPoint);
        Assert.Null(_service.FindByFingerprint("no-such-fingerprint"));
    }
}
