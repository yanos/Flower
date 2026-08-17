using System.Net;

using Flower.Services;

namespace Flower.Tests;

public class LanGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")] // IPv4 link-local
    [InlineData("::1")] // IPv6 loopback
    [InlineData("fd00::1")] // IPv6 ULA
    [InlineData("fe80::1")] // IPv6 link-local
    [InlineData("::ffff:192.168.1.1")] // IPv4-mapped IPv6
    [InlineData("100.64.0.1")] // Tailscale CGNAT range, low end
    [InlineData("100.100.100.100")] // Tailscale CGNAT range, mid
    [InlineData("100.127.255.255")] // Tailscale CGNAT range, high end
    public void IsPrivateOrLoopback_is_true_for_lan_local_addresses(string address)
    {
        Assert.True(LanGuard.IsPrivateOrLoopback(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // just outside the 172.16/12 range
    [InlineData("100.63.255.255")] // just below the CGNAT range
    [InlineData("100.128.0.0")] // just above the CGNAT range
    [InlineData("2001:4860:4860::8888")] // public IPv6
    public void IsPrivateOrLoopback_is_false_for_public_addresses(string address)
    {
        Assert.False(LanGuard.IsPrivateOrLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsPrivateOrLoopback_honors_extra_allowed_cidrs()
    {
        var address = IPAddress.Parse("203.0.113.42");

        Assert.False(LanGuard.IsPrivateOrLoopback(address));
        Assert.False(LanGuard.IsPrivateOrLoopback(address, ["203.0.113.0/28"])); // just outside this /28
        Assert.True(LanGuard.IsPrivateOrLoopback(address, ["203.0.113.0/24"]));
        Assert.True(LanGuard.IsPrivateOrLoopback(address, ["10.0.0.0/8", "203.0.113.42/32"]));
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    public void IsPrivateOrLoopback_can_be_told_not_to_trust_the_cgnat_range(string address)
    {
        // 100.64.0.0/10 is Tailscale's range but also generic carrier-grade
        // NAT, where "another subscriber on the same carrier" lives - a
        // deployment that doesn't use a tailnet can drop it (Flower.Server's
        // TrustTailscaleRange).
        Assert.False(LanGuard.IsPrivateOrLoopback(
            IPAddress.Parse(address), allowCarrierGradeNat: false));

        // Everything else keeps working with it off.
        Assert.True(LanGuard.IsPrivateOrLoopback(
            IPAddress.Parse("192.168.1.1"), allowCarrierGradeNat: false));
        Assert.True(LanGuard.IsPrivateOrLoopback(
            IPAddress.Parse("127.0.0.1"), allowCarrierGradeNat: false));
    }

    [Fact]
    public void IsPrivateOrLoopback_ignores_malformed_extra_cidrs()
    {
        var address = IPAddress.Parse("203.0.113.42");

        Assert.False(LanGuard.IsPrivateOrLoopback(address, ["not-a-cidr", "203.0.113.0"]));
    }
}
