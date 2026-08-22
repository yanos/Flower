using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Endpoints;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The admin API behind the browser settings page: reading and writing this
// server's own configuration, triggering a rescan, reading its log, and the
// short-lived session tokens the browser runs on because it cannot sign
// anything itself (see AdminSessionService).
public class AdminEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    private async Task<DeviceSigningKey> NewAdminAsync(string alias = "Admin Device")
    {
        var device = NewDevice();
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, alias, device.PublicKeyBase64, isAdmin: true);
        return device;
    }

    // A signed admin request, exactly as ServerAdminClient builds one: identity in
    // headers, the signature over method + path + query + a hash of the body.
    private async Task<HttpContext> SignedAsync(
        DeviceSigningKey device, string method, string path, string? query = null, string? body = null)
    {
        var bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        var queryPairs = ParseQuery(query);
        var identity = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        };
        var (signature, timestamp, nonce) = device.Sign(method, path, queryPairs.Concat(identity), bodyBytes);

        return await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            if (query != null)
                c.Request.QueryString = new QueryString("?" + query);
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.50");
            foreach (var (key, value) in identity)
                c.Request.Headers[key] = value;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            if (body != null)
            {
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bodyBytes);
                c.Request.ContentLength = bodyBytes.Length;
            }
        });
    }

    private Task<HttpContext> WithSessionAsync(string token, string method, string path, string? body = null) =>
        server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.51");
            c.Request.Headers["X-Flower-Admin-Session"] = token;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bytes);
                c.Request.ContentLength = bytes.Length;
            }
        });

    private static List<(string Key, string Value)> ParseQuery(string? query) =>
        string.IsNullOrEmpty(query)
            ? []
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(parts => (Uri.UnescapeDataString(parts[0]),
                                  parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : ""))
                .ToList();

    // Read straight through, no seeking: TestServer swaps in its own
    // non-seekable ResponseBodyReaderStream, which throws on set_Position.
    private static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<T> ReadAsync<T>(HttpContext context) =>
        JsonSerializer.Deserialize<T>(await BodyAsync(context), Json)
        ?? throw new InvalidOperationException("empty response");

    [Fact]
    public async Task An_admin_device_can_read_the_servers_settings()
    {
        using var admin = await NewAdminAsync();
        try
        {
            var context = await SignedAsync(admin, "GET", "/api/admin/settings");

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            var settings = await ReadAsync<ServerSettingsDto>(context);
            // The fixture configures exactly one (empty) library folder, which is
            // what stops the startup scan from wandering into the real ~/Music.
            Assert.Single(settings.LibraryPaths);
            Assert.False(string.IsNullOrEmpty(settings.DataDirectory));
        }
        finally
        {
            await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(admin.Fingerprint);
        }
    }

    // The one thing that has to hold for the settings page to be worth anything:
    // what it writes survives, in the file an operator owns.
    [Fact]
    public async Task Writing_settings_persists_them_to_the_data_directorys_settings_file()
    {
        using var admin = await NewAdminAsync();
        try
        {
            var context = await SignedAsync(
                admin, "PUT", "/api/admin/settings",
                body: """{"alias":"Basement NAS","advertiseOnLan":false,"allowedCidrs":["10.8.0.0/24"]}""");

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            var settings = await ReadAsync<ServerSettingsDto>(context);
            Assert.Equal("Basement NAS", settings.Alias);
            Assert.False(settings.AdvertiseOnLan);
            Assert.Equal(["10.8.0.0/24"], settings.AllowedCidrs);

            // Both of these are read once by MdnsAdvertiser when the hosted
            // service starts, so the page has to be told they are not live yet.
            Assert.NotNull(settings.RestartRequired);
            Assert.Contains(nameof(FlowerServerOptions.Alias), settings.RestartRequired!);
            Assert.Contains(nameof(FlowerServerOptions.AdvertiseOnLan), settings.RestartRequired!);

            var written = await File.ReadAllTextAsync(
                Path.Combine(settings.DataDirectory, ServerDataDirectory.SettingsFileName));
            Assert.Contains("Basement NAS", written);
            Assert.Contains("10.8.0.0/24", written);

            // The seeded file is mostly underscore-prefixed documentation an
            // operator may have added to - a settings write must not flatten it.
            Assert.Contains("_LibraryPaths", written);
        }
        finally
        {
            await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(admin.Fingerprint);
        }
    }

    [Fact]
    public async Task A_paired_device_that_is_not_an_admin_is_refused()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64);

        try
        {
            var context = await SignedAsync(device, "GET", "/api/admin/settings");
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // The whole point of the session token: the browser holds one instead of a
    // key, and it works exactly as far as the admin device behind it does.
    [Fact]
    public async Task A_minted_session_authenticates_the_browser_and_dies_with_its_device()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var admin = await NewAdminAsync("Desktop");

        var minted = await SignedAsync(admin, "POST", "/api/admin/sessions");
        Assert.Equal(StatusCodes.Status200OK, minted.Response.StatusCode);
        var session = await ReadAsync<AdminSessionResponse>(minted);

        // Handed to the browser in the URL fragment, never the query string - a
        // fragment is not sent to the server, so the token cannot land in a log.
        Assert.Contains("#admin=", session.Url);

        var used = await WithSessionAsync(session.Token, "GET", "/api/admin/settings");
        Assert.Equal(StatusCodes.Status200OK, used.Response.StatusCode);

        // A bearer token that can mint its own successor is not short-lived.
        var chained = await WithSessionAsync(session.Token, "POST", "/api/admin/sessions");
        Assert.Equal(StatusCodes.Status400BadRequest, chained.Response.StatusCode);

        // Revoking the device the session came from has to take the session with
        // it, or "forget this device" is a promise the server does not keep.
        await trustedPeers.RevokeAsync(admin.Fingerprint);
        server.Services.GetRequiredService<AdminSessionService>().RevokeFor(admin.Fingerprint);

        var afterRevoke = await WithSessionAsync(session.Token, "GET", "/api/admin/settings");
        Assert.Equal(StatusCodes.Status401Unauthorized, afterRevoke.Response.StatusCode);
    }

    [Fact]
    public async Task A_rescan_can_be_started_and_its_status_read_back()
    {
        using var admin = await NewAdminAsync();
        try
        {
            var started = await SignedAsync(admin, "POST", "/api/admin/library/rescan");
            Assert.Equal(StatusCodes.Status200OK, started.Response.StatusCode);

            var status = await SignedAsync(admin, "GET", "/api/admin/library");
            Assert.Equal(StatusCodes.Status200OK, status.Response.StatusCode);
            // Answered as soon as the scan *starts*, so this deliberately asserts
            // nothing about whether it has finished - only that the route reports.
            Assert.Null((await ReadAsync<LibraryStatusResponse>(status)).LastError);
        }
        finally
        {
            await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(admin.Fingerprint);
        }
    }

    [Fact]
    public async Task The_log_is_readable_from_the_admin_api()
    {
        using var admin = await NewAdminAsync();
        try
        {
            var context = await SignedAsync(admin, "GET", "/api/admin/logs", query: "limit=50");

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            // The server logs its data directory and its startup scan before any
            // test can run, so there is always something here.
            Assert.NotEmpty(await ReadAsync<List<LogEntryResponse>>(context));
        }
        finally
        {
            await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(admin.Fingerprint);
        }
    }

    [Fact]
    public async Task An_unsigned_request_is_unauthorized()
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/admin/settings";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.52");
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // The fixture pins Flower:WebUiPath at an empty directory, so no bundle is
    // deployed here however the developer's own tree is set up - and the fallback
    // has to say so rather than 404 into nothing, since the address a client's
    // "Server Settings..." button just opened is exactly where that would be
    // most confusing.
    [Fact]
    public async Task The_root_page_explains_itself_when_no_web_ui_is_deployed()
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.53");
        });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("wasm-tools", await BodyAsync(context));
    }

    // The fallback must not swallow an unknown API path: a Subsonic client parsing
    // an HTML page as XML fails far less legibly than a 404 does.
    [Fact]
    public async Task An_unknown_api_path_still_fails_as_an_api_call()
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/rest/nothingHere";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.54");
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }
}

// The other half of the not-deployed page: a bundle that *is* there gets served,
// and the SPA fallback hands unknown non-API paths back to index.html so the
// settings deep-link the "Server Settings..." button opens survives a reload.
public class WebUiHostingTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public string WebUi { get; } =
            Path.Combine(Path.GetTempPath(), "flower-server-webui-" + Guid.NewGuid());

        private readonly string _dataDirectory =
            Path.Combine(Path.GetTempPath(), "flower-server-webui-data-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var emptyLibrary = Path.Combine(_dataDirectory, "lib");
            Directory.CreateDirectory(emptyLibrary);
            Directory.CreateDirectory(WebUi);

            // Standing in for a published Flower.Web bundle - Resolve only ever
            // looks for index.html, so one file is a faithful deployment here.
            File.WriteAllText(Path.Combine(WebUi, "index.html"), "<html>flower</html>");

            builder.UseSetting("Flower:DataDirectory", _dataDirectory);
            builder.UseSetting("Flower:LibraryPaths:0", emptyLibrary);
            // See SubsonicServerFixture - keeps the adopted Music.app folder
            // out of the pinned library path list.
            builder.UseSetting("Flower:IntegrateWithITunes", "false");
            builder.UseSetting("Flower:WebUiPath", WebUi);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(WebUi, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<(int Status, string Body)> GetAsync(Factory factory, string path)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.60");
        });

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_deployed_bundle_is_served_at_the_root_and_backs_the_spa_fallback()
    {
        using var factory = new Factory();

        var (rootStatus, rootBody) = await GetAsync(factory, "/");
        Assert.Equal(StatusCodes.Status200OK, rootStatus);
        Assert.Contains("flower", rootBody);

        // Not a file on disk, not an API route: index.html, so the browser app
        // boots and routes it itself.
        var (deepStatus, deepBody) = await GetAsync(factory, "/settings");
        Assert.Equal(StatusCodes.Status200OK, deepStatus);
        Assert.Contains("flower", deepBody);

        // Still not allowed to shadow the APIs.
        var (apiStatus, _) = await GetAsync(factory, "/rest/nothingHere");
        Assert.Equal(StatusCodes.Status404NotFound, apiStatus);
    }
}

