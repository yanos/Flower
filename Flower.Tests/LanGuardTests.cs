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
    public void IsPrivateOrLoopback_is_true_for_lan_local_addresses(string address)
    {
        Assert.True(LanGuard.IsPrivateOrLoopback(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // just outside the 172.16/12 range
    [InlineData("2001:4860:4860::8888")] // public IPv6
    public void IsPrivateOrLoopback_is_false_for_public_addresses(string address)
    {
        Assert.False(LanGuard.IsPrivateOrLoopback(IPAddress.Parse(address)));
    }
}
