using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

        // Makes a port start throwing again, so a test can take one route away
        // from a peer while leaving another working - which is the whole point
        // of remembering more than one address for a server.
        public void StopResponding(int port) => _responsesByPort.Remove(port);

        // The headers of the most recent /info poll, so a test can check what
        // the client proved about itself rather than only what it was told.
        public HttpRequestHeaders? LastHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastHeaders = request.Headers;
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

    // ── Identifying ourselves on the poll ────────────────────────────────
    //
    // /info stays open to anyone, but the half of its answer that matters to a
    // paired client - trustsCaller, and the addresses that keep a server
    // reachable off the LAN - is only given to a caller whose signature checks
    // out. So the poll has to carry one. See docs/OPEN-INTERNET-REVIEW.md.

    [Fact]
    public async Task The_info_poll_is_signed_when_this_device_can_sign()
    {
        using var signingKey = TestSigningKey.Create();
        var identity = new DeviceIdentity { Fingerprint = signingKey.Fingerprint, Alias = "Me" };
        var handler = new FakeInfoHandler();
        handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true,"deviceType":"server"}""");
        using var service = new NetworkDiscoveryService(
            identity, NullLogger<NetworkDiscoveryService>.Instance, new FakeMdnsBackend(),
            new HttpClient(handler),
            new SignedDeviceCredentials(identity, signingKey));

        await service.AddRememberedAsync("192.168.1.40:4533");

        Assert.NotNull(handler.LastHeaders);
        Assert.True(handler.LastHeaders!.Contains("X-Flower-Signature"));
        Assert.True(handler.LastHeaders.Contains("X-Flower-Timestamp"));
        Assert.True(handler.LastHeaders.Contains("X-Flower-Nonce"));
        Assert.Equal(signingKey.Fingerprint, handler.LastHeaders.GetValues("X-Flower-Fingerprint").Single());
    }

    // The browser head registers no signing key at all, and the tests above
    // construct without one - a peer still has to resolve, just without the
    // parts of the answer a signature buys.
    [Fact]
    public async Task A_device_with_no_signing_credentials_still_resolves_a_peer()
    {
        _handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true,"deviceType":"server"}""");

        var device = await _service.AddRememberedAsync("192.168.1.40:4533");

        Assert.Equal("server-fp", device!.Fingerprint);
        Assert.False(_handler.LastHeaders!.Contains("X-Flower-Signature"));
        Assert.Equal("my-fp", _handler.LastHeaders.GetValues("X-Flower-Fingerprint").Single());
    }

    // ── Remembered peers: reaching a server discovery cannot see ─────────
    //
    // mDNS is link-local, so a client off its home network has nothing to
    // find. Before these, reachability *was* discovery and a paired server
    // simply vanished when its client left the house. See
    // docs/REMOTE-ACCESS-PLAN.md.

    [Fact]
    public async Task A_remembered_address_becomes_a_peer_without_any_mDNS_sighting()
    {
        _handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true,"deviceType":"server"}""");

        var device = await _service.AddRememberedAsync("192.168.1.40:4533");

        Assert.NotNull(device);
        Assert.Equal("server-fp", device!.Fingerprint);
        Assert.True(device.IsRemembered);
        Assert.True(device.IsResponding);
        Assert.Same(device, Assert.Single(_service.KnownDevices));
        Assert.Empty(_backend.Browsed);
    }

    [Fact]
    public async Task A_remembered_address_that_does_not_answer_is_still_kept()
    {
        // Nothing is registered for this port, so /info throws. The entry has
        // to survive anyway - "my server, currently unreachable" is the state
        // a phone away from home is in whenever the server is asleep, and
        // forgetting the address would make it unrecoverable.
        var device = await _service.AddRememberedAsync("192.168.1.41:4533");

        Assert.NotNull(device);
        Assert.False(device!.IsResponding);
        Assert.Single(_service.KnownDevices);
    }

    [Fact]
    public async Task An_unresolvable_address_is_rejected_rather_than_stored()
    {
        Assert.Null(await _service.AddRememberedAsync("not a host name at all"));
        Assert.Null(await _service.AddRememberedAsync("192.168.1.40:not-a-port"));
        Assert.Empty(_service.KnownDevices);
    }

    [Fact]
    public async Task A_remembered_peer_survives_more_failures_than_would_prune_a_discovered_one()
    {
        // The regression this design is most likely to grow. A discovered peer
        // is pruned after MaxConsecutiveResolveFailures because a fresh mDNS
        // announcement brings it back; a remembered one has no announcement to
        // come back on, so pruning it destroys the only route to it.
        _handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true}""");
        var device = await _service.AddRememberedAsync("192.168.1.40:4533");
        Assert.True(device!.IsResponding);

        _handler.StopResponding(4533);
        for (var attempt = 0; attempt < 6; attempt++)
            await _service.AddRememberedAsync("192.168.1.40:4533");

        Assert.Single(_service.KnownDevices);
        Assert.False(device.IsResponding);

        // And it comes back on its own once the address works again, without
        // anything having to re-add it.
        _handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true}""");
        await _service.AddRememberedAsync("192.168.1.40:4533");
        Assert.True(device.IsResponding);
    }

    [Fact]
    public async Task A_peer_reports_the_addresses_it_can_be_reached_on()
    {
        _handler.RespondWith(4533, """
            {"alias":"Basement","fingerprint":"server-fp","isServer":true,
             "addresses":["192.168.1.40:4533","100.101.102.103:4533"]}
            """);

        var device = await _service.AddRememberedAsync("192.168.1.40:4533");

        Assert.Equal(["192.168.1.40:4533", "100.101.102.103:4533"], device!.Addresses);
    }

    [Fact]
    public async Task A_live_sighting_outranks_a_remembered_address_for_the_same_server()
    {
        // The same server, known two ways at once: seen on this link, and
        // remembered at its tailnet address. Both work, and the sighting is
        // the better route by definition - which is what makes walking back
        // through the front door quietly restore the direct path.
        const string info = """{"alias":"Basement","fingerprint":"server-fp","isServer":true}""";
        _handler.RespondWith(4533, info);
        _handler.RespondWith(4534, info);

        _backend.RaiseInstanceFound(InstanceName("Basement"), Routable(40));
        WaitUntil(() => _service.KnownDevices.Count == 1, "the discovered peer should resolve");

        await _service.AddRememberedAsync("100.101.102.103:4534");

        var device = Assert.Single(_service.KnownDevices);
        Assert.False(device.IsRemembered);
        Assert.Equal(0, NetworkDiscoveryService.ReachRank(device));
    }

    [Fact]
    public async Task A_lan_address_outranks_a_tailnet_one_and_the_tailnet_takes_over_when_it_stops_answering()
    {
        // Walking out of the house, in one test. Both candidates are
        // remembered, so the choice is made purely on rank - and then purely
        // on which one still answers.
        const string info = """{"alias":"Basement","fingerprint":"server-fp","isServer":true}""";
        _handler.RespondWith(4533, info);
        _handler.RespondWith(4534, info);

        await _service.AddRememberedAsync("192.168.1.40:4533");
        await _service.AddRememberedAsync("100.101.102.103:4534");

        var atHome = Assert.Single(_service.KnownDevices);
        Assert.Equal(1, NetworkDiscoveryService.ReachRank(atHome));
        Assert.Equal(4533, atHome.BaseUri.Port);

        // The LAN address stops answering - the phone has left the building.
        _handler.StopResponding(4533);
        await _service.AddRememberedAsync("192.168.1.40:4533");

        var away = Assert.Single(_service.KnownDevices);
        Assert.Equal(2, NetworkDiscoveryService.ReachRank(away));
        Assert.Equal(4534, away.BaseUri.Port);
        Assert.True(away.IsResponding);
    }

    [Fact]
    public async Task A_remembered_peer_can_be_removed()
    {
        _handler.RespondWith(4533, """{"alias":"Basement","fingerprint":"server-fp","isServer":true}""");
        await _service.AddRememberedAsync("192.168.1.40:4533");

        _service.RemoveRemembered("192.168.1.40:4533");

        Assert.Empty(_service.KnownDevices);
    }

    // Browse-only, and the "only" is the point: a client is not a server, so
    // it has nothing to publish and no port to publish it on. This used to
    // advertise as well, back when every app hosted its own listener.
    [Fact]
    public void Start_browses_for_our_own_service_type_and_advertises_nothing()
    {
        _service.Start();

        Assert.Contains(ServiceType, _backend.Browsed);
        Assert.Empty(_backend.Advertised);
    }

    [Fact]
    public void Restart_re_browses_and_still_advertises_nothing()
    {
        _service.Start();

        _service.Restart();

        Assert.Equal(2, _backend.Browsed.Count);
        Assert.Empty(_backend.Advertised);
    }

    [Fact]
    public void OnInstanceFound_discovers_a_new_peer_and_resolves_its_info()
    {
        var endpoint = Routable(10);
        _handler.RespondWith(endpoint.Port, """{"alias":"Kitchen","fingerprint":"peer-fp","publicKey":"peer-key","trustsCaller":true}""");

        DiscoveredDevice? discovered = null;
        _service.DeviceDiscovered += (_, d) => discovered = d;

        _backend.RaiseInstanceFound(InstanceName("Kitchen-Speaker"), endpoint);

        WaitUntil(() => discovered?.Fingerprint == "peer-fp", "the peer's real alias/fingerprint should resolve via /info");
        Assert.Equal("Kitchen", discovered!.Alias);
        Assert.Equal("peer-key", discovered.PublicKey);
        Assert.True(discovered.TrustsUs);
        Assert.Same(discovered, Assert.Single(_service.KnownDevices));
    }

    // deviceType is carried through as the peer reported it. Only "server"
    // answers now that a client does not advertise itself, so this is less a
    // decision than a record of what the handshake said - but a peer that
    // reports something else is a Flower this one does not understand, and
    // that is worth being able to see.
    [Fact]
    public void Resolves_the_peers_reported_device_type()
    {
        var endpoint = Routable(30);
        _handler.RespondWith(endpoint.Port,
            """{"alias":"Basement NAS","fingerprint":"server-fp","deviceType":"server"}""");

        DiscoveredDevice? discovered = null;
        _service.DeviceDiscovered += (_, d) => discovered = d;

        _backend.RaiseInstanceFound(InstanceName("Basement-NAS"), endpoint);

        WaitUntil(() => discovered?.Fingerprint == "server-fp", "the server's info should resolve");
        Assert.Equal("server", discovered!.DeviceType);
    }

    // Whether this server counts us as one of its administrators, which is what
    // decides if the admin-only controls are offered at all - see
    // DiscoveredDevice.WeAreAdmin and MainViewModel.CanInviteDeviceToPairedServer.
    [Fact]
    public void Resolves_whether_the_peer_makes_us_an_administrator()
    {
        var endpoint = Routable(31);
        _handler.RespondWith(endpoint.Port,
            """{"alias":"Basement NAS","fingerprint":"admin-fp","callerIsAdmin":true}""");

        DiscoveredDevice? discovered = null;
        _service.DeviceDiscovered += (_, d) => discovered = d;

        _backend.RaiseInstanceFound(InstanceName("Basement-NAS"), endpoint);

        WaitUntil(() => discovered?.Fingerprint == "admin-fp", "the server's info should resolve");
        Assert.True(discovered!.WeAreAdmin);
    }

    // The resting state is the cautious one, the opposite way round from
    // TrustsUs: a peer that says nothing about it leaves the admin-only
    // controls hidden rather than showing ones it would refuse.
    [Fact]
    public void A_peer_that_says_nothing_about_admin_rights_grants_none()
    {
        var endpoint = Routable(32);
        _handler.RespondWith(endpoint.Port,
            """{"alias":"Basement NAS","fingerprint":"quiet-fp","callerIsAdmin":null}""");

        DiscoveredDevice? discovered = null;
        _service.DeviceDiscovered += (_, d) => discovered = d;

        _backend.RaiseInstanceFound(InstanceName("Basement-NAS"), endpoint);

        WaitUntil(() => discovered?.Fingerprint == "quiet-fp", "the server's info should resolve");
        Assert.False(discovered!.WeAreAdmin);
    }

    [Fact]
    public void OnInstanceFound_ignores_announcements_of_a_different_service_type()
    {
        _backend.RaiseInstanceFound("SomeAirplayDevice._airplay._tcp.local", Routable(11));

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

        Assert.Equal(NetworkDiscoveryService.HttpOrigin(routable), Assert.Single(_service.KnownDevices).BaseUri);
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

        Assert.Equal(NetworkDiscoveryService.HttpOrigin(routable), Assert.Single(_service.KnownDevices).BaseUri);
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

        Assert.Equal(NetworkDiscoveryService.HttpOrigin(endpoint), found!.BaseUri);
        Assert.Null(_service.FindByFingerprint("no-such-fingerprint"));
    }
}
