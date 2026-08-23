using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// What a client remembers about its paired server, and how it gets back to one
// it can no longer see.
//
// mDNS is link-local, so a client off its home network has nothing to discover.
// Reachability used to be defined as discovery, which meant a paired server
// vanished the moment its client left the house - with no address stored to
// fall back on. These cover the other half: the server reports its own
// addresses in the /info handshake, the client keeps them for the one server it
// paired with, and probes them later. See docs/REMOTE-ACCESS-PLAN.md.
[Collection("PlatformDataDirectory")]
public class RemoteServerReachabilityTests : PinnedDataDirectory
{
    // Routes /info by port, so one service can face several simulated
    // addresses for the same server - which is the situation being tested.
    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _responsesByPort = [];

        public void RespondWith(int port, string json) => _responsesByPort[port] = json;

        public void StopResponding(int port) => _responsesByPort.Remove(port);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_responsesByPort.TryGetValue(request.RequestUri!.Port, out var json))
                throw new HttpRequestException("simulated unreachable peer");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private readonly FakeInfoHandler _handler = new();
    private readonly NetworkDiscoveryService _discovery;
    private readonly AppSettings _appSettings = new() { PairedServerFingerprint = "server-fp" };
    private readonly PairedServerReachability _reachability;

    public RemoteServerReachabilityTests()
    {
        _discovery = Own(new NetworkDiscoveryService(
            new DeviceIdentity { Fingerprint = "my-fp", Alias = "Me" },
            NullLogger<NetworkDiscoveryService>.Instance,
            new FakeMdnsBackend(),
            new HttpClient(_handler)));

        _reachability = Own(new PairedServerReachability(
            _discovery,
            _appSettings,
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PairedServerReachability>.Instance));
    }

    private const string ServerInfo = """
        {"alias":"Basement","fingerprint":"server-fp","isServer":true,"deviceType":"server",
         "addresses":["192.168.1.40:4533","100.101.102.103:4534"]}
        """;

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), because);

    [Fact]
    public async Task The_paired_servers_own_addresses_are_remembered_from_its_handshake()
    {
        _handler.RespondWith(4533, ServerInfo);

        await _discovery.AddRememberedAsync("192.168.1.40:4533");

        // This is what "pair once at home and it follows you" rests on: the
        // tailnet address is now stored without the user ever typing it.
        WaitUntil(() => _appSettings.PairedServerAddresses.Contains("100.101.102.103:4534"),
            "the server's own reported addresses should have been remembered");
        Assert.Equal(["192.168.1.40:4533", "100.101.102.103:4534"], _appSettings.PairedServerAddresses);
    }

    [Fact]
    public async Task Addresses_from_a_peer_that_is_not_the_paired_server_are_ignored()
    {
        // The gate that keeps this from being a way to aim a client's probes
        // at hosts of someone else's choosing: /info is unauthenticated, so a
        // list of addresses is only worth anything from the fingerprint we
        // actually paired with.
        _handler.RespondWith(4533, """
            {"alias":"Someone Else","fingerprint":"other-fp","isServer":true,
             "addresses":["203.0.113.9:4533"]}
            """);

        await _discovery.AddRememberedAsync("192.168.1.40:4533");

        Assert.Empty(_appSettings.PairedServerAddresses);
        Assert.False(_reachability.IsReachable);
    }

    [Fact]
    public async Task A_remembered_address_makes_the_server_reachable_with_no_discovery_at_all()
    {
        _handler.RespondWith(4534, ServerInfo);
        _appSettings.PairedServerAddresses = ["100.101.102.103:4534"];

        await _reachability.RestoreRememberedAsync();

        Assert.True(_reachability.IsReachable);
        Assert.Equal(ServerRoute.Tailnet, _reachability.Route);
    }

    [Fact]
    public async Task The_route_follows_the_client_out_of_the_house_and_back()
    {
        _handler.RespondWith(4533, ServerInfo);
        _handler.RespondWith(4534, ServerInfo);
        _appSettings.PairedServerAddresses = ["192.168.1.40:4533", "100.101.102.103:4534"];

        await _reachability.RestoreRememberedAsync();
        Assert.True(_reachability.IsReachable);
        Assert.Equal(ServerRoute.LocalNetwork, _reachability.Route);

        // Out of the house: the LAN address stops answering, and the tailnet
        // one carries it without the server ever becoming unreachable.
        _handler.StopResponding(4533);
        await _discovery.AddRememberedAsync("192.168.1.40:4533");
        Assert.True(_reachability.IsReachable);
        Assert.Equal(ServerRoute.Tailnet, _reachability.Route);

        // And back in again.
        _handler.RespondWith(4533, ServerInfo);
        await _discovery.AddRememberedAsync("192.168.1.40:4533");
        Assert.True(_reachability.IsReachable);
        Assert.Equal(ServerRoute.LocalNetwork, _reachability.Route);
    }

    [Fact]
    public async Task A_server_that_answers_nowhere_is_unreachable_but_not_forgotten()
    {
        _appSettings.PairedServerAddresses = ["192.168.1.40:4533", "100.101.102.103:4534"];

        await _reachability.RestoreRememberedAsync();

        Assert.False(_reachability.IsReachable);
        Assert.Equal(ServerRoute.Unreachable, _reachability.Route);

        // The addresses survive, so the next attempt has something to try -
        // a server that is merely switched off must not cost the client the
        // only route back to it.
        Assert.Equal(2, _appSettings.PairedServerAddresses.Count);
    }

    [Fact]
    public async Task An_address_the_server_stops_reporting_stops_being_remembered()
    {
        _handler.RespondWith(4533, ServerInfo);
        await _discovery.AddRememberedAsync("192.168.1.40:4533");
        WaitUntil(() => _appSettings.PairedServerAddresses.Count == 2, "both addresses should be remembered first");

        // The server leaves the tailnet. Replaced rather than merged, so the
        // address it no longer has stops being probed instead of lingering.
        _handler.RespondWith(4533, """
            {"alias":"Basement","fingerprint":"server-fp","isServer":true,
             "addresses":["192.168.1.40:4533"]}
            """);
        await _discovery.AddRememberedAsync("192.168.1.40:4533");

        WaitUntil(() => _appSettings.PairedServerAddresses.Count == 1, "the dropped address should not be kept");
        Assert.Equal(["192.168.1.40:4533"], _appSettings.PairedServerAddresses);
    }
}

// What a server says about where it can be reached. The client half of this is
// covered above; this is the half that has to be true for any of it to work.
public class LocalAddressesTests
{
    [Fact]
    public void Reports_no_loopback_or_link_local_address()
    {
        // A loopback address is reachable only from the machine itself, and a
        // link-local one only on the link it was minted for - a client that
        // remembered either would be probing something that cannot work from
        // anywhere else.
        var addresses = LocalAddresses.Reachable(4533);

        Assert.DoesNotContain(addresses, a => a.StartsWith("127.") || a.StartsWith("[::1]") || a.StartsWith("169.254."));
        Assert.DoesNotContain(addresses, a => a.StartsWith("[fe80"));
    }

    [Fact]
    public void Every_reported_address_carries_the_port()
    {
        Assert.All(LocalAddresses.Reachable(4533), a => Assert.EndsWith(":4533", a));
    }

    [Fact]
    public void An_advertised_host_is_reported_first_and_keeps_a_port_it_already_has()
    {
        Assert.Equal("my-server.tail1234.ts.net:4533", LocalAddresses.Reachable(4533, "my-server.tail1234.ts.net")[0]);

        // Appending unconditionally would produce host:8080:4533.
        Assert.Equal("my-server.tail1234.ts.net:8080", LocalAddresses.Reachable(4533, "my-server.tail1234.ts.net:8080")[0]);
    }

    [Fact]
    public void Reported_addresses_are_not_duplicated()
    {
        var addresses = LocalAddresses.Reachable(4533);

        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
