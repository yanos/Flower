using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Flower.Server.Tests;

// A server deliberately exposed to the internet: Flower:AllowPublicAccess on,
// with a loopback proxy declared, which is the shape a Cloudflare Tunnel
// produces (cloudflared runs on this machine and connects onward over
// loopback). See docs/SELF-HOSTING.md.
public sealed class PublicServerFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "flower-public-tests-" + Guid.NewGuid());

    private readonly string _emptyLibrary =
        Path.Combine(Path.GetTempPath(), "flower-public-tests-lib-" + Guid.NewGuid());

    private readonly string _noWebUi =
        Path.Combine(Path.GetTempPath(), "flower-public-tests-noweb-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_emptyLibrary);
        Directory.CreateDirectory(_noWebUi);

        // Same pinning as the other fixtures, and for the same reason - an
        // unpinned library path has this server adopt the developer's real
        // Music folder.
        builder.UseSetting("Flower:DataDirectory", _dataDirectory);
        builder.UseSetting("Flower:LibraryPaths:0", _emptyLibrary);
        builder.UseSetting("Flower:IntegrateWithITunes", "false");
        builder.UseSetting("Flower:WebUiPath", _noWebUi);

        builder.UseSetting("Flower:AllowPublicAccess", "true");
        builder.UseSetting("Flower:TrustedProxies:0", "127.0.0.1/32");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_emptyLibrary, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_noWebUi, recursive: true); } catch { /* best effort */ }
    }
}

// What changes, and what deliberately does not, when a server is told it is
// reachable from the open internet.
//
// LanGuard was the containing control named in five separate places
// (docs/OPEN-INTERNET-REVIEW.md), so switching it off is the single most
// consequential setting this server has. These tests pin both halves: that the
// switch does what it says, and that nothing behind it was relying on the
// guard to be the thing keeping strangers out.
public class PublicAccessTests(PublicServerFixture publicServer, SubsonicServerFixture lanOnly)
    : IClassFixture<PublicServerFixture>, IClassFixture<SubsonicServerFixture>
{
    // Ungated apart from LanGuard, which makes it the cheapest probe for "was
    // this caller admitted at all".
    private const string InfoPath = "/api/localsend/v2/info";
    private const string LibraryPath = "/api/flower/v1/library";

    private static async Task<HttpStatusCode> GetAsync(
        TestServer server, string path, string remoteIp, string? forwardedFor = null)
    {
        var context = await server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            if (forwardedFor != null)
                c.Request.Headers["X-Forwarded-For"] = forwardedFor;
        });

        return (HttpStatusCode)context.Response.StatusCode;
    }

    [Fact]
    public async Task A_public_caller_is_turned_away_unless_the_server_was_told_to_expect_one()
    {
        // The default, and the one every deployment that never touches this
        // setting keeps.
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(lanOnly.Server, InfoPath, "203.0.113.7"));
    }

    [Fact]
    public async Task A_public_caller_reaches_a_server_that_was()
    {
        Assert.Equal(HttpStatusCode.OK, await GetAsync(publicServer.Server, InfoPath, "203.0.113.7"));
    }

    [Fact]
    public async Task A_public_address_forwarded_by_the_tunnel_is_admitted_as_itself()
    {
        // The deployment this exists for: cloudflared delivers over loopback
        // and names the real client in X-Forwarded-For. Admission follows the
        // forwarded address, which is also the address every rate limiter
        // behind this then keys on - the whole reason TrustedProxies has to be
        // set rather than left empty.
        Assert.Equal(
            HttpStatusCode.OK,
            await GetAsync(publicServer.Server, InfoPath, "127.0.0.1", forwardedFor: "203.0.113.7"));
    }

    [Fact]
    public async Task Opening_the_door_does_not_open_the_library()
    {
        // The half that makes the switch survivable. Public access moves the
        // whole burden onto the signature check, so an unsigned stranger must
        // get exactly as far as they did before: to the handshake, and no
        // further. A regression here would be invisible on a LAN and total on
        // the internet.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await GetAsync(publicServer.Server, LibraryPath, "127.0.0.1", forwardedFor: "203.0.113.7"));
    }
}
