using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The route a client's "Pair" button actually calls (PeerPairingService.
// RedeemPairingCodeAsync). Everything below the UI - the code, the signature,
// the resulting TrustedPeer - meets here, so this is where the flow is worth
// exercising end to end rather than a layer at a time.
public class PairRedeemEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private const string Path = "/api/flower/v1/pair-redeem";

    // A brand-new device: its own keypair, never seen by the server, exactly
    // as a freshly-installed client would be.
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // Each test redeems from its own private address. The route is rate
    // limited to 5 attempts per IP per minute (PairingEndpoints.
    // RedeemRateLimiter) and these all share one fixture, so a single source
    // address would make the sixth request in the class fail as a 429 no
    // matter what it was testing.
    private async Task<HttpStatusCode> RedeemAsync(
        DeviceSigningKey device, string code, string alias = "New Device", string remoteIp = "10.0.0.1")
    {
        var (signature, timestamp, nonce) = device.Sign("POST", Path, [], body: []);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Post;
            c.Request.Path = Path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-Alias"] = alias;
            c.Request.Headers["X-Flower-PublicKey"] = device.PublicKeyBase64;
            c.Request.Headers["X-Flower-PairingCode"] = code;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
        });

        return (HttpStatusCode)context.Response.StatusCode;
    }

    [Fact]
    public async Task A_valid_code_trusts_the_redeeming_device()
    {
        var pairingCodes = server.Services.GetRequiredService<PairingCodeService>();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var (code, _) = pairingCodes.GenerateCode();
        using var device = NewDevice();

        var status = await RedeemAsync(device, code, alias: "Kitchen iPad", remoteIp: "10.0.0.11");

        try
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(trustedPeers.IsTrusted(device.Fingerprint));
            // Not an admin: the code was issued without that grant, and a
            // device cannot claim it by asking.
            Assert.False(trustedPeers.IsAdmin(device.Fingerprint));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task An_admin_granting_code_produces_an_admin_peer()
    {
        var pairingCodes = server.Services.GetRequiredService<PairingCodeService>();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var (code, _) = pairingCodes.GenerateCode(grantsAdmin: true);
        using var device = NewDevice();

        await RedeemAsync(device, code, remoteIp: "10.0.0.12");

        try
        {
            Assert.True(trustedPeers.IsAdmin(device.Fingerprint));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_code_typed_in_lower_case_with_dashes_still_works()
    {
        // Codes get read out over the phone and copied from the dash-grouped
        // rendering on the admin screen, so the client sends whatever the user
        // typed and the server normalizes. If this stops holding, the pairing
        // box appears broken for anyone who did not type it exactly.
        var pairingCodes = server.Services.GetRequiredService<PairingCodeService>();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var (code, _) = pairingCodes.GenerateCode();
        using var device = NewDevice();

        var status = await RedeemAsync(
            device, $"{code[..4].ToLowerInvariant()}-{code[4..].ToLowerInvariant()}", remoteIp: "10.0.0.13");

        try
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(trustedPeers.IsTrusted(device.Fingerprint));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_trusts_nobody()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();

        var status = await RedeemAsync(device, "WRONGCOD", remoteIp: "10.0.0.14");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(trustedPeers.IsTrusted(device.Fingerprint));
    }

    [Fact]
    public async Task A_code_cannot_be_redeemed_twice()
    {
        // Single-use is the whole security property: a code overheard or left
        // on screen after a successful pair must not let a second device in.
        var pairingCodes = server.Services.GetRequiredService<PairingCodeService>();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var (code, _) = pairingCodes.GenerateCode();
        using var first = NewDevice();
        using var second = NewDevice();

        await RedeemAsync(first, code, remoteIp: "10.0.0.15");
        var secondStatus = await RedeemAsync(second, code, remoteIp: "10.0.0.16");

        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, secondStatus);
            Assert.False(trustedPeers.IsTrusted(second.Fingerprint));
        }
        finally
        {
            await trustedPeers.RevokeAsync(first.Fingerprint);
        }
    }

    [Fact]
    public async Task A_code_without_a_valid_signature_is_rejected()
    {
        // The code authorizes; the signature is what binds it to the key being
        // registered. Without that check, anyone who learned a code could
        // enrol a key they do not hold.
        var pairingCodes = server.Services.GetRequiredService<PairingCodeService>();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var (code, _) = pairingCodes.GenerateCode();
        using var device = NewDevice();
        var (_, timestamp, nonce) = device.Sign("POST", Path, [], body: []);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Post;
            c.Request.Path = Path;
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.17");
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-PublicKey"] = device.PublicKeyBase64;
            c.Request.Headers["X-Flower-PairingCode"] = code;
            c.Request.Headers["X-Flower-Signature"] = Convert.ToBase64String(new byte[64]);
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)context.Response.StatusCode);
        Assert.False(trustedPeers.IsTrusted(device.Fingerprint));
        // And the code survives, so the legitimate device can still use it.
        Assert.True(pairingCodes.TryConsume(code, out _));
    }
}
