using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Flower.Server.Tests.Security;

// A server with a web UI deployed - a stand-in bundle of two files, since what
// is under test is how the server serves it, not what is in it.
public sealed class WebUiServerFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "flower-webui-tests-" + Guid.NewGuid());

    private readonly string _emptyLibrary =
        Path.Combine(Path.GetTempPath(), "flower-webui-tests-lib-" + Guid.NewGuid());

    private readonly string _webUi =
        Path.Combine(Path.GetTempPath(), "flower-webui-tests-bundle-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_emptyLibrary);
        Directory.CreateDirectory(Path.Combine(_webUi, "_framework"));
        File.WriteAllText(Path.Combine(_webUi, "index.html"), "<!doctype html><title>Flower</title>");
        File.WriteAllBytes(Path.Combine(_webUi, "_framework", "dotnet.wasm"), [0x00, 0x61, 0x73, 0x6d]);

        // Same pinning as the other fixtures - see FlowerServerFixture.
        builder.UseSetting("Flower:DataDirectory", _dataDirectory);
        builder.UseSetting("Flower:LibraryPaths:0", _emptyLibrary);
        builder.UseSetting("Flower:IntegrateWithITunes", "false");
        builder.UseSetting("Flower:WebUiPath", _webUi);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_emptyLibrary, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_webUi, recursive: true); } catch { /* best effort */ }
    }
}

// An administrator's tab signs whatever it is clicked into doing, so the page
// must refuse to sit inside anybody else's frame - docs/CODE-REVIEW-2026-10.md
// A4. Both ways the UI leaves this server: the page itself, through the
// single-page fallback, and the bundle's files, through static files.
public class WebUiFramingTests(WebUiServerFixture server) : IClassFixture<WebUiServerFixture>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/_framework/dotnet.wasm")]
    public async Task Refuses_to_be_framed_and_to_be_sniffed(string path)
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("DENY", context.Response.Headers.XFrameOptions.ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
    }
}
