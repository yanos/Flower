using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

using Flower.Server.Configuration;

namespace Flower.Server.Tests;

// A server told there is a proxy in front of it (FlowerServerOptions
// .TrustedProxies), which is the shape `tailscale serve` produces:
// tailscaled terminates TLS on the tailnet and proxies onward over loopback,
// so without this every tailnet device arrives as 127.0.0.1.
public sealed class ProxiedServerFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "flower-proxy-tests-" + Guid.NewGuid());

    private readonly string _emptyLibrary =
        Path.Combine(Path.GetTempPath(), "flower-proxy-tests-lib-" + Guid.NewGuid());

    private readonly string _noWebUi =
        Path.Combine(Path.GetTempPath(), "flower-proxy-tests-noweb-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_emptyLibrary);
        Directory.CreateDirectory(_noWebUi);

        // Same pinning as SubsonicServerFixture, and for the same reason -
        // an unpinned library path plus the default IntegrateWithITunes has
        // this server adopt the developer's real Music.app folder.
        builder.UseSetting("Flower:DataDirectory", _dataDirectory);
        builder.UseSetting("Flower:LibraryPaths:0", _emptyLibrary);
        builder.UseSetting("Flower:IntegrateWithITunes", "false");
        builder.UseSetting("Flower:WebUiPath", _noWebUi);

        // Two entries, and the second is the one with teeth. A loopback proxy
        // is what `tailscale serve` produces, but loopback is an address
        // LanGuard admits anyway - so a test proxying from it cannot tell
        // "the forwarded address was substituted" from "nothing happened".
        // 203.0.113.0/24 (TEST-NET-3) is a proxy the guard would reject on its
        // own, which makes the substitution observable in both directions.
        builder.UseSetting("Flower:TrustedProxies:0", "127.0.0.1/32");
        builder.UseSetting("Flower:TrustedProxies:1", "203.0.113.0/24");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_emptyLibrary, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_noWebUi, recursive: true); } catch { /* best effort */ }
    }
}

// Who the server thinks a request came from when something is proxying for it.
//
// Everything downstream reads one field - Connection.RemoteIpAddress: the
// LanGuard middleware in Program.cs decides admission from it, and all three
// per-IP rate limiters (pair-redeem, sync, /rest auth) key their buckets on
// it. So getting this wrong is not cosmetic in either direction. Believe a
// forwarded header from anyone and every caller picks their own source
// address; believe none of them behind `tailscale serve` and every tailnet
// device shares one bucket, where one busy client locks out the rest.
public class ForwardedHeaderTests(ProxiedServerFixture proxied, SubsonicServerFixture direct)
    : IClassFixture<ProxiedServerFixture>, IClassFixture<SubsonicServerFixture>
{
    // SyncProtocol.InfoPath - LocalSend-shaped, and ungated apart from
    // LanGuard, which is what makes it the cheapest probe for "was this caller
    // admitted at all".
    private const string InfoPath = "/api/localsend/v2/info";
    private const string RedeemPath = "/api/flower/v1/pair-redeem";

    private static async Task<HttpStatusCode> GetInfoAsync(
        TestServer server, string remoteIp, string? forwardedFor = null)
    {
        var context = await server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = InfoPath;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            if (forwardedFor != null)
                c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });

        return (HttpStatusCode)context.Response.StatusCode;
    }

    [Fact]
    public async Task A_forwarded_address_from_the_configured_proxy_is_the_one_admission_is_decided_on()
    {
        // Arrives from a proxy the guard would turn away, carrying a tailnet
        // address the guard allows. Admission follows the forwarded address,
        // not the hop that delivered it.
        Assert.Equal(
            HttpStatusCode.OK,
            await GetInfoAsync(proxied.Server, "203.0.113.1", forwardedFor: "100.101.102.103"));
    }

    [Fact]
    public async Task A_forwarded_public_address_is_still_rejected_by_the_guard()
    {
        // The half that makes the feature safe to have at all: substituting
        // the real client's address means the allow-list now applies to *it*,
        // so a proxy in front of the server is not a way around the gate. A
        // deployment that genuinely fronts public traffic has to say so, by
        // widening AllowedCidrs.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await GetInfoAsync(proxied.Server, "127.0.0.1", forwardedFor: "203.0.113.7"));
    }

    [Fact]
    public async Task A_forwarded_address_from_anyone_but_the_proxy_is_ignored()
    {
        // The same claim as the test above, from a caller that is not one of
        // the configured proxies. Believing it would let any public caller
        // wave itself through the guard - and let any caller shed an
        // exhausted rate-limit bucket by picking a new address.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await GetInfoAsync(proxied.Server, "198.51.100.9", forwardedFor: "100.101.102.103"));
    }

    [Fact]
    public async Task A_server_with_no_proxy_configured_believes_no_forwarded_header_at_all()
    {
        // The default deployment, where a forwarded header can only have been
        // written by the client itself. A public caller claiming a private
        // address gets nothing for it.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await GetInfoAsync(direct.Server, "203.0.113.8", forwardedFor: "10.0.0.9"));
    }

    private async Task<HttpStatusCode> RedeemAsync(string forwardedFor)
    {
        // Unsigned on purpose: the rate limiter runs before any signature is
        // verified (PairingEndpoints), so an unauthenticated attempt still
        // consumes a token and 401-vs-429 tells the two outcomes apart.
        var context = await proxied.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Post;
            c.Request.Path = RedeemPath;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });

        return (HttpStatusCode)context.Response.StatusCode;
    }

    [Fact]
    public async Task Each_device_behind_the_proxy_gets_its_own_rate_limit_bucket()
    {
        // The motivating bug, stated as a test. The redeem route allows five
        // attempts per IP per minute; six from one tailnet device must trip it
        // and six from six devices must not. Before the forwarded-header
        // handling both cases keyed on 127.0.0.1, so the second one locked out
        // every device on the tailnet after the fifth attempt anyone made.
        //
        // Addresses are unique to this test because RedeemRateLimiter's
        // buckets are process-wide and outlive any one fixture.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await RedeemAsync("100.64.10.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await RedeemAsync("100.64.10.1"));

        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await RedeemAsync($"100.64.11.{i + 1}"));
    }
}

// The detector behind docs/OPEN-INTERNET-REVIEW.md finding #2's fix: a proxy or
// tunnel in front of this server that TrustedProxies does not name. It cannot
// be caught at startup - cloudflared dials out and delivers over loopback, so
// nothing about the process or the config says a tunnel exists - which leaves
// one runtime signal: an X-Forwarded-For from a hop not trusted to write one.
public class ProxyHeaderAuditTests
{
    private static readonly IPNetwork Loopback = IPNetwork.Parse("127.0.0.1/32");

    [Fact]
    public void A_direct_client_with_no_forwarded_header_is_not_a_finding()
    {
        // The ordinary LAN deployment. Warning here would be noise on every
        // server anyone runs.
        var audit = new ProxyHeaderAudit([]);

        Assert.False(audit.IsUndeclaredHop(IPAddress.Parse("192.168.1.20"), carriesForwardedFor: false));
    }

    [Fact]
    public void A_forwarded_header_with_no_trusted_proxies_configured_is_the_finding()
    {
        // cloudflared running, TrustedProxies forgotten: every client is now
        // 127.0.0.1, sharing one rate-limit bucket and one pass through
        // LanGuard.
        var audit = new ProxyHeaderAudit([]);

        Assert.True(audit.IsUndeclaredHop(IPAddress.Loopback, carriesForwardedFor: true));
    }

    [Fact]
    public void A_forwarded_header_from_a_declared_proxy_is_not_a_finding()
    {
        var audit = new ProxyHeaderAudit([Loopback]);

        Assert.False(audit.IsUndeclaredHop(IPAddress.Loopback, carriesForwardedFor: true));
    }

    [Fact]
    public void A_forwarded_header_from_an_address_no_configured_CIDR_covers_is_the_finding()
    {
        // The Docker case: TrustedProxies names 127.0.0.1/32 but the container
        // network delivers from 172.20.0.3, so nothing is believed and the
        // operator has no way to tell without being told.
        var audit = new ProxyHeaderAudit([Loopback]);

        Assert.True(audit.IsUndeclaredHop(IPAddress.Parse("172.20.0.3"), carriesForwardedFor: true));
    }

    [Fact]
    public void Warnings_are_throttled_so_a_caller_cannot_flood_the_log()
    {
        // The header is written by whoever sent the request, so a caller can
        // trigger this on demand. That is worth surfacing once, not every time.
        var audit = new ProxyHeaderAudit([]);
        var now = DateTimeOffset.UtcNow;

        Assert.True(audit.ShouldWarn(IPAddress.Loopback, true, now));
        Assert.False(audit.ShouldWarn(IPAddress.Loopback, true, now));
        Assert.False(audit.ShouldWarn(IPAddress.Loopback, true, now + TimeSpan.FromMinutes(1)));
        Assert.True(audit.ShouldWarn(IPAddress.Loopback, true, now + ProxyHeaderAudit.RepeatInterval));
    }

    [Fact]
    public void A_request_that_is_not_a_finding_never_spends_the_throttle()
    {
        // Otherwise ordinary traffic would silently consume the one warning the
        // interval allows, and the real misconfiguration would go unlogged.
        var audit = new ProxyHeaderAudit([]);
        var now = DateTimeOffset.UtcNow;

        Assert.False(audit.ShouldWarn(IPAddress.Parse("192.168.1.20"), carriesForwardedFor: false, now));
        Assert.True(audit.ShouldWarn(IPAddress.Loopback, carriesForwardedFor: true, now));
    }
}
