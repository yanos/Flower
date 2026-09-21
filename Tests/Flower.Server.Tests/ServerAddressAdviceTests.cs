using System.Net;

using Flower.Server.Services;

namespace Flower.Server.Tests;

// The advice only pays for itself if it fires on the shapes that actually
// caused the failure and stays quiet on the ones that work - a warning that
// cries wolf on a healthy LAN deployment is worse than none, because the next
// real one is read as noise. Both halves are covered here.
public class ServerAddressAdviceTests
{
    [Theory]
    // The value that cost an evening: reported, discarded by the client's
    // resolver, and invisible in the server's own log.
    [InlineData("MacBookPro.local")]
    [InlineData("macbookpro.LOCAL")]
    [InlineData("nas.local:8533")]
    [InlineData("http://nas.local:4533")]
    [InlineData("https://nas.local")]
    // A trailing dot is a fully-qualified name and still mDNS.
    [InlineData("nas.local.")]
    public void Flags_multicast_dns_names(string advertisedHost) =>
        Assert.True(ServerAddressAdvice.LooksLikeMulticastDnsName(advertisedHost));

    [Theory]
    [InlineData("192.168.1.40")]
    [InlineData("192.168.1.40:4533")]
    [InlineData("https://38.133.38.247:4534")]
    [InlineData("music.example.com")]
    [InlineData("https://music.example.com")]
    // "localhost" is not a .local name, however much it reads like one.
    [InlineData("localhost")]
    [InlineData("localhost:4533")]
    // A host whose name merely contains "local" is not an mDNS name either.
    [InlineData("local.example.com")]
    [InlineData("mylocal")]
    [InlineData("")]
    public void Leaves_ordinary_addresses_alone(string advertisedHost) =>
        Assert.False(ServerAddressAdvice.LooksLikeMulticastDnsName(advertisedHost));

    // An IPv6 literal is full of colons, so the port split has to be
    // bracket-aware or the host comes back truncated - the same trap
    // LocalAddresses.AdvertisedOrigin documents.
    [Theory]
    [InlineData("[fd42::1]:4533")]
    [InlineData("[::1]:4533")]
    public void Does_not_mistake_ipv6_literals_for_names(string advertisedHost) =>
        Assert.False(ServerAddressAdvice.LooksLikeMulticastDnsName(advertisedHost));

    [Theory]
    [InlineData("172.17.0.2")]   // Docker's default bridge
    [InlineData("172.16.0.1")]   // bottom of the range
    [InlineData("172.31.255.254")] // top of it
    public void Recognises_docker_bridge_addresses(string address) =>
        Assert.True(ServerAddressAdvice.IsDockerBridgeAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("192.168.50.172")]
    [InlineData("10.0.0.5")]
    [InlineData("100.101.102.103")] // tailnet
    [InlineData("172.15.0.1")]      // just below the range
    [InlineData("172.32.0.1")]      // just above it
    [InlineData("38.133.38.247")]   // public
    [InlineData("fd42:801e::1")]    // ULA, not IPv4 at all
    public void Leaves_other_addresses_alone(string address) =>
        Assert.False(ServerAddressAdvice.IsDockerBridgeAddress(IPAddress.Parse(address)));

    // The warning's own guard: a host-networked container sees the real LAN
    // address alongside docker0's, so "every address is a bridge address" is
    // what keeps it quiet there. Asserted on the predicate the caller composes
    // rather than on the log, since that composition is the actual rule.
    [Fact]
    public void Host_networked_container_is_not_all_bridge_addresses()
    {
        List<IPAddress> own = [IPAddress.Parse("192.168.50.172"), IPAddress.Parse("172.17.0.1")];
        Assert.False(own.TrueForAll(ServerAddressAdvice.IsDockerBridgeAddress));
    }

    [Fact]
    public void Bridged_container_is_all_bridge_addresses()
    {
        List<IPAddress> own = [IPAddress.Parse("172.17.0.2")];
        Assert.True(own.TrueForAll(ServerAddressAdvice.IsDockerBridgeAddress));
    }
}
