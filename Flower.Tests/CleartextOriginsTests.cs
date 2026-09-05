using System;
using System.Net;
using Flower.Services;
using Xunit;

namespace Flower.Tests;

// The rule Android's network security config cannot state - it matches
// hostnames, not CIDRs - written where it can be. See CleartextOrigins.
public class CleartextOriginsTests
{
    [Theory]
    [InlineData("http://192.168.1.20:4533")]
    [InlineData("http://10.0.0.5:4533")]
    [InlineData("http://172.16.4.1:4533")]
    [InlineData("http://127.0.0.1:4533")]
    [InlineData("http://169.254.7.7:4533")]
    [InlineData("http://[fe80::1]:4533")]
    [InlineData("http://100.101.102.103:4533")] // a tailnet, already encrypted underneath
    public void Cleartext_to_a_network_we_are_already_on_is_allowed(string origin) =>
        Assert.True(CleartextOrigins.IsAllowed(new Uri(origin)));

    [Theory]
    [InlineData("http://93.184.216.34:4533")]
    [InlineData("http://[2606:2800:220:1::1]:4533")]
    public void Cleartext_to_a_routable_address_is_refused(string origin) =>
        Assert.False(CleartextOrigins.IsAllowed(new Uri(origin)));

    [Fact]
    public void A_name_is_judged_by_what_it_resolved_to()
    {
        var origin = new Uri("http://music.example.com:4533");
        Assert.True(CleartextOrigins.IsAllowed(origin, IPAddress.Parse("192.168.1.20")));
        Assert.False(CleartextOrigins.IsAllowed(origin, IPAddress.Parse("93.184.216.34")));
    }

    // Refused rather than allowed: a lookup that failed is not evidence the
    // host is on this link.
    [Fact]
    public void A_name_nobody_resolved_is_refused() =>
        Assert.False(CleartextOrigins.IsAllowed(new Uri("http://music.example.com:4533")));

    // https is somebody else's question entirely - PeerHttpClient decides
    // whether the certificate is acceptable, and a routable host over TLS is
    // the ordinary remote-access case.
    [Theory]
    [InlineData("https://music.example.com")]
    [InlineData("https://93.184.216.34:4533")]
    public void Encrypted_origins_are_not_this_check_s_business(string origin) =>
        Assert.True(CleartextOrigins.IsAllowed(new Uri(origin)));
}
