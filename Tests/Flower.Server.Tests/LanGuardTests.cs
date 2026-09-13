using System.Net;

using Flower.Services;

namespace Flower.Server.Tests;

// The first middleware in the pipeline, and the only thing standing between the
// whole API surface and the open internet if the server is ever exposed beyond a
// LAN. It had no coverage at all.
//
// Driven against SyncProtocol.InfoPath because the guard runs before anything
// else, so the route only has to be one that answers an anonymous caller 200 -
// /info does (see DiscoveryEndpoints: a probe that claims no identity reads as
// "unknown", never as a rejection). These cases used to ride on /rest/ping with
// a Subsonic credential, which is exactly the sort of incidental dependency on
// the adapter that made retiring it look bigger than it was.
public class LanGuardTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    [Theory]
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("192.168.1.50")]    // RFC1918
    [InlineData("10.0.0.7")]        // RFC1918
    [InlineData("172.16.4.2")]      // RFC1918
    [InlineData("100.101.102.103")] // Tailscale CGNAT
    public async Task A_private_or_loopback_client_is_allowed_through(string ip)
    {
        var (status, _) = await server.SendAsync(SyncProtocol.InfoPath, ip);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("172.32.0.1")]  // just outside the 172.16/12 block
    public async Task A_public_client_is_refused_before_reaching_any_endpoint(string ip)
    {
        // Dropped by the middleware, not answered with an error body - the
        // request never reaches the route table or the auth filter.
        await GuardedRequest.AssertDropped(async () =>
            (await server.SendAsync(SyncProtocol.InfoPath, ip)).Status);
    }

    [Fact]
    public async Task The_guard_applies_to_unauthenticated_requests_too()
    {
        // Order matters: a public client must be cut off before it can even
        // probe which credentials the server accepts.
        await GuardedRequest.AssertDropped(async () =>
            (await server.SendAsync("/api/flower/v1/library", "8.8.8.8")).Status);
    }
}
