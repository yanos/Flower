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
            Assert.Equal(StatusCodes.Status403Forbidden, (await SendAsync("GET", "/api/flower/v1/library", sharedAddress)).Response.StatusCode);

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
