using System;
using System.Net;

using Flower.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.Network;

// A reported network change is acted on only when the network reads
// differently afterwards: every platform's signal fires for changes that move
// nothing (iOS re-reported one cellular path 2,896 times in a day).
public class NetworkChangeFilterTests
{
    private static NetworkDescription.Interface Nic(string name, string[] addresses, params string[] gateways) =>
        new(name, addresses, gateways);

    [Fact]
    public void A_report_that_leaves_the_network_as_it_was_is_not_a_move()
    {
        var network = "Wifi:en0 via 192.168.1.1";
        var filter = new NetworkChangeFilter(() => network, NullLogger.Instance);

        Assert.False(filter.HasMoved());
        Assert.False(filter.HasMoved());
    }

    [Fact]
    public void A_different_network_is_a_move_once()
    {
        var network = "Wifi:en0 via 192.168.1.1";
        var filter = new NetworkChangeFilter(() => network, NullLogger.Instance);

        network = "Cellular:pdp_ip0";

        Assert.True(filter.HasMoved());
        Assert.False(filter.HasMoved());
    }

    // Better a recheck too many than a move missed.
    [Fact]
    public void A_network_that_cannot_be_described_counts_as_a_move()
    {
        string? network = null;
        var filter = new NetworkChangeFilter(() => network, NullLogger.Instance);

        Assert.True(filter.HasMoved());
        Assert.True(filter.HasMoved());
    }

    // A laptop moving its Wi-Fi from home to a phone's hotspot: same
    // interface, a new address and a new gateway.
    [Fact]
    public void Joining_a_phones_hotspot_on_the_same_interface_reads_differently()
    {
        var home = NetworkDescription.Of([Nic("en0", ["192.168.1.20"], "192.168.1.1")]);
        var hotspot = NetworkDescription.Of([Nic("en0", ["172.20.10.2"], "172.20.10.1")]);

        Assert.NotEqual(home, hotspot);
    }

    [Fact]
    public void The_order_interfaces_and_addresses_are_listed_in_does_not_matter()
    {
        var one = NetworkDescription.Of([
            Nic("en0", ["192.168.1.20", "fd00::20"], "192.168.1.1"),
            Nic("utun4", ["100.64.0.5"])]);
        var other = NetworkDescription.Of([
            Nic("utun4", ["100.64.0.5"]),
            Nic("en0", ["fd00::20", "192.168.1.20"], "192.168.1.1")]);

        Assert.Equal(one, other);
    }

    // A Mac's AirDrop interface comes and goes all day with nothing but a
    // link-local address, which is no way out to a server.
    [Fact]
    public void An_interface_with_only_link_local_addresses_is_not_part_of_the_network()
    {
        var addresses = new[] { "fe80::1", "169.254.3.4" };
        Assert.All(addresses, a => Assert.False(NetworkDescription.Counts(IPAddress.Parse(a))));

        var without = NetworkDescription.Of([Nic("en0", ["192.168.1.20"], "192.168.1.1")]);
        var with = NetworkDescription.Of([
            Nic("en0", ["192.168.1.20"], "192.168.1.1"),
            Nic("awdl0", [])]);

        Assert.Equal(without, with);
    }

    [Fact]
    public void A_vpn_coming_up_reads_differently()
    {
        var before = NetworkDescription.Of([Nic("en0", ["192.168.1.20"], "192.168.1.1")]);
        var after = NetworkDescription.Of([
            Nic("en0", ["192.168.1.20"], "192.168.1.1"),
            Nic("utun4", ["100.64.0.5"])]);

        Assert.NotEqual(before, after);
    }
}
