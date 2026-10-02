using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Tests;

// docs/TRUST-BOUNDARY-PLAN.md step 2: budgets are charged per device once a
// request verifies, and only failing callers are counted by address. Every
// test here takes source addresses of its own, so one test spending a budget
// cannot starve another in the same fixture.
public class RequestGateTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    private async Task<DeviceSigningKey> PairedAsync()
    {
        var device = NewDevice();
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, "Gate test", device.PublicKeyBase64, isAdmin: false);
        return device;
    }

    // signer signs; claimed is the fingerprint the request says it is from.
    // The two differ only in the tests that need a signature that fails.
    private async Task<HttpContext> SendAsync(
        string method, string path, string remoteIp,
        DeviceSigningKey? signer = null, string? claimed = null, string query = "")
    {
        return await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Request.QueryString = query.Length == 0 ? QueryString.Empty : new QueryString("?" + query);
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

            if (signer == null)
                return;

            var pairs = query.Length == 0
                ? new List<(string, string)>()
                : query.Split('&').Select(p => p.Split('=')).Select(p => (p[0], Uri.UnescapeDataString(p[1]))).ToList();
            var (signature, timestamp, nonce) = signer.Sign(method, path, pairs, []);
            c.Request.Headers["X-Flower-Fingerprint"] = claimed ?? signer.Fingerprint;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
        });
    }

    [Fact]
    public async Task Two_devices_behind_one_address_do_not_share_a_budget()
    {
        using var busy = await PairedAsync();
        using var quiet = await PairedAsync();
        const string sharedAddress = "10.0.7.1";

        for (var i = 0; i < 60; i++)
            Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/api/flower/v1/playlists", sharedAddress, busy)).Response.StatusCode);

        var refused = await SendAsync("GET", "/api/flower/v1/playlists", sharedAddress, busy);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        Assert.Equal("60", refused.Response.Headers.RetryAfter.ToString());

        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/api/flower/v1/playlists", sharedAddress, quiet)).Response.StatusCode);
    }

    // Someone who knows a device's fingerprint - which is not a secret - and
    // fails signatures in its name runs out of attempts at their own address,
    // and the device itself, anywhere else, is unaffected.
    [Fact]
    public async Task Failing_signatures_for_a_device_do_not_lock_that_device_out_elsewhere()
    {
        using var victim = await PairedAsync();
        using var impostor = NewDevice();
        const string attacker = "10.0.7.2";

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(StatusCodes.Status401Unauthorized,
                (await SendAsync("GET", "/api/flower/v1/playlists", attacker, impostor, claimed: victim.Fingerprint)).Response.StatusCode);
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests,
            (await SendAsync("GET", "/api/flower/v1/playlists", attacker, impostor, claimed: victim.Fingerprint)).Response.StatusCode);

        Assert.Equal(StatusCodes.Status200OK,
            (await SendAsync("GET", "/api/flower/v1/playlists", "10.0.7.3", victim)).Response.StatusCode);
    }

    // A stranger flooding one address runs out, and a paired device at the
    // same address - a household behind one proxy - keeps going.
    [Fact]
    public async Task A_stranger_flood_from_an_address_does_not_refuse_a_paired_device_there()
    {
        using var device = await PairedAsync();
        const string sharedAddress = "10.0.7.4";

        for (var i = 0; i < 30; i++)
            Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync("GET", "/api/flower/v1/library", sharedAddress)).Response.StatusCode);

        Assert.Equal(StatusCodes.Status429TooManyRequests, (await SendAsync("GET", "/api/flower/v1/library", sharedAddress)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", "/api/flower/v1/playlists", sharedAddress, device)).Response.StatusCode);
    }

    // The two routes that had no budget at all.
    [Fact]
    public async Task The_handshake_has_a_budget_for_strangers_that_a_paired_device_does_not_share()
    {
        using var device = await PairedAsync();
        const string sharedAddress = "10.0.7.5";

        for (var i = 0; i < 120; i++)
            Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", SyncProtocol.InfoPath, sharedAddress)).Response.StatusCode);

        Assert.Equal(StatusCodes.Status429TooManyRequests, (await SendAsync("GET", SyncProtocol.InfoPath, sharedAddress)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync("GET", SyncProtocol.InfoPath, sharedAddress, device)).Response.StatusCode);
    }

    [Fact]
    public async Task Minting_stream_tickets_is_charged_to_the_devices_media_budget()
    {
        using var device = await PairedAsync();

        for (var i = 0; i < 240; i++)
        {
            Assert.Equal(StatusCodes.Status200OK,
                (await SendAsync("POST", "/api/flower/v1/stream-tickets", "10.0.7.6", device, query: $"id=track-{i}")).Response.StatusCode);
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests,
            (await SendAsync("POST", "/api/flower/v1/stream-tickets", "10.0.7.6", device, query: "id=one-more")).Response.StatusCode);
    }

    // The filters sign a body only when it states its length; one that does
    // not used to be verified as empty and then read, unsigned, by its handler.
    [Fact]
    public async Task A_body_that_does_not_state_its_length_is_refused()
    {
        using var device = await PairedAsync();
        var body = Encoding.UTF8.GetBytes("""{"Plays":[]}""");
        var (signature, timestamp, nonce) = device.Sign("POST", "/api/flower/v1/plays", [], []);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/flower/v1/plays";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.7.7");
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            c.Request.Headers.TransferEncoding = "chunked";
            c.Request.Body = new MemoryStream(body);
            c.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        });

        Assert.Equal(StatusCodes.Status411LengthRequired, context.Response.StatusCode);
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}

// docs/TRUST-BOUNDARY-PLAN.md step 3: every refusal under /api is a problem
// document with a code, on every route this server maps - walked from the
// routing table rather than listed, so a route added later is covered without
// anyone remembering to add it here.
public class RefusalContractTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    [Fact]
    public async Task Every_api_route_refuses_a_stranger_with_a_coded_problem()
    {
        var routes = server.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (Method: method, Path: System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, "{[^}]+}", "x"))))
            .Distinct()
            .ToList();

        // The routing table is the thing under test; an empty one proves nothing.
        Assert.True(routes.Count > 30, $"only {routes.Count} /api routes were found");

        var address = 0;
        foreach (var (method, path) in routes)
        {
            var context = await server.Server.SendAsync(c =>
            {
                c.Request.Method = method;
                c.Request.Path = path;
                c.Request.QueryString = new QueryString("?id=x");
                c.Connection.RemoteIpAddress = IPAddress.Parse($"10.0.9.{100 + address}");
            });
            address++;

            var status = context.Response.StatusCode;
            if (path == SyncProtocol.InfoPath)
            {
                Assert.Equal(StatusCodes.Status200OK, status);
                continue;
            }

            Assert.True(status >= 400, $"{method} {path} answered {status} to a stranger");
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.True(FlowerProblem.ContentType == context.Response.ContentType?.Split(';')[0],
                $"{method} {path} answered {status} as {context.Response.ContentType}: {body}");
            var problem = FlowerProblem.TryRead(body);
            Assert.True(problem is { Code.Length: > 0 }, $"{method} {path} refused without a code");

            // A stranger is device-unknown everywhere a device signs, and the
            // pairing route - which takes a key it has not seen - says the
            // signature did not verify.
            Assert.Equal(
                path.EndsWith("/pair-redeem", StringComparison.Ordinal) ? ProblemCodes.SignatureInvalid : ProblemCodes.DeviceUnknown,
                problem!.Code);
        }
    }

    // The fallback under every route: a path nothing maps is still a coded
    // refusal, not a bare status a client has to guess from.
    [Fact]
    public async Task An_unmapped_api_path_is_a_coded_not_found()
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/flower/v1/no-such-route";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.9.99");
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal(ProblemCodes.NotFound, FlowerProblem.TryRead(await reader.ReadToEndAsync())?.Code);
    }
}
