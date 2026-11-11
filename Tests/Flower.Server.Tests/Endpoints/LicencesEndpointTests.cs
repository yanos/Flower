using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using Flower.Services;

using Flower.Server.Tests.TestSupport;

namespace Flower.Server.Tests.Endpoints;

// The server ships FFmpeg and TagLib#, so its image has to carry their LGPL as
// every client build does - and /licences is where a browser tab's
// "Open-Source Licences" link reads it, before the tab has paired or signed
// anything.
public class LicencesEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    [Fact]
    public async Task An_unsigned_caller_reads_the_notice_and_the_lgpl()
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = OpenSourceNotices.ServerPath;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        });

        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("text/plain", context.Response.ContentType);
        Assert.Contains("LGPL-2.1-or-later: FFmpeg", body);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", body);
    }
}
