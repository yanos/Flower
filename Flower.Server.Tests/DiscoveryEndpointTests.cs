using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The handshake that turns an mDNS hit into a sidebar row. A client discards
// any peer whose /info it cannot resolve, so the field names here are the
// contract - they are read out of the JSON by raw lowercase name on the client
// (NetworkDiscoveryService.ResolveAliasAsync), not deserialized into a shared
// type, which means a casing change is a silent break rather than a compile
// error.
public class DiscoveryEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetInfoAsync(string? callerFingerprint = null)
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = SyncProtocol.InfoPath;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (callerFingerprint != null)
                c.Request.Headers["X-Flower-Fingerprint"] = callerFingerprint;
        });

        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        var status = (HttpStatusCode)context.Response.StatusCode;
        if (status != HttpStatusCode.OK)
            return (status, default);

        using var document = JsonDocument.Parse(body);
        return (status, document.RootElement.Clone());
    }

    [Fact]
    public async Task Answers_the_identity_handshake_a_client_resolves_peers_with()
    {
        var (status, body) = await GetInfoAsync();

        Assert.Equal(HttpStatusCode.OK, status);

        // Every field NetworkDiscoveryService reads, by the exact name it
        // reads it under.
        var signingKey = server.Services.GetRequiredService<DeviceSigningKey>();
        Assert.Equal(signingKey.Fingerprint, body.GetProperty("fingerprint").GetString());
        Assert.Equal(signingKey.PublicKeyBase64, body.GetProperty("publicKey").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("alias").GetString()));
        Assert.False(body.GetProperty("download").GetBoolean());
        Assert.True(body.TryGetProperty("libraryToken", out _));
    }

    [Fact]
    public async Task Always_identifies_itself_as_a_server()
    {
        // The one field that distinguishes this from another Flower app
        // answering the same handshake - it is what puts the peer under the
        // sidebar's servers section rather than its devices one.
        var (_, body) = await GetInfoAsync();

        Assert.True(body.GetProperty("isServer").GetBoolean());
        Assert.Equal("server", body.GetProperty("deviceType").GetString());
    }

    [Fact]
    public async Task Omits_trustsCaller_when_the_caller_did_not_identify_itself()
    {
        // Null rather than false, so an anonymous probe cannot be misread by a
        // paired client as "you have been revoked".
        var (_, body) = await GetInfoAsync();

        Assert.True(!body.TryGetProperty("trustsCaller", out var trusts)
                    || trusts.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Reports_trustsCaller_false_for_a_device_it_does_not_know()
    {
        var (_, body) = await GetInfoAsync(callerFingerprint: "a-device-that-never-paired");

        Assert.False(body.GetProperty("trustsCaller").GetBoolean());
    }

    [Fact]
    public async Task Reports_trustsCaller_true_once_the_device_is_paired()
    {
        // This is what a client polls every ~5s: it is how a revoked device
        // learns it was revoked on its own timetable, rather than at its next
        // failed sync.
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        const string fingerprint = "discovery-test-peer";
        await trustedPeers.ApproveAsync(fingerprint, "Test peer", "public-key");

        try
        {
            var (_, body) = await GetInfoAsync(callerFingerprint: fingerprint);
            Assert.True(body.GetProperty("trustsCaller").GetBoolean());
        }
        finally
        {
            await trustedPeers.RevokeAsync(fingerprint);
        }
    }

    [Fact]
    public async Task Is_reachable_without_any_credential()
    {
        // Deliberately ungated: a device has to learn this server's public key
        // here before either side can evaluate trust at all. LanGuard is the
        // only thing in front of it.
        var (status, _) = await GetInfoAsync();

        Assert.Equal(HttpStatusCode.OK, status);
    }
}

// The advertised name is what a user picks the server out by in a sidebar, so
// it has to be something rather than empty even when nobody configured one.
public class MdnsAdvertiserNamingTests
{
    [Fact]
    public void Falls_back_to_the_machine_name_when_no_alias_is_configured()
    {
        Assert.Equal(Environment.MachineName, MdnsAdvertiser.InstanceName(new FlowerServerOptions()));
    }

    [Fact]
    public void Prefers_a_configured_alias()
    {
        var options = new FlowerServerOptions { Alias = "  Basement NAS  " };

        Assert.Equal("Basement NAS", MdnsAdvertiser.InstanceName(options));
    }
}

// Which binds are worth announcing. An mDNS record resolves to the machine's
// LAN addresses whatever Kestrel bound, so announcing a loopback-only server
// hands every client on the network an address that refuses the connection -
// and, under the machine name, a row that collides with the real server's.
public class MdnsAdvertisablePortTests
{
    [Theory]
    [InlineData("http://0.0.0.0:4533")]
    [InlineData("http://[::]:4533")]
    [InlineData("http://+:4533")]
    [InlineData("http://*:4533")]
    [InlineData("http://192.168.50.172:4533")]
    public void A_bind_something_off_this_machine_can_reach_is_advertised(string address)
    {
        Assert.Equal(4533, MdnsAdvertiser.AdvertisablePort([address]));
    }

    [Theory]
    [InlineData("http://localhost:5599")]
    [InlineData("http://127.0.0.1:5599")]
    [InlineData("http://[::1]:5599")]
    public void A_loopback_only_bind_is_not_advertised(string address)
    {
        Assert.Null(MdnsAdvertiser.AdvertisablePort([address]));
    }

    [Fact]
    public void Nothing_bound_is_not_advertised()
    {
        Assert.Null(MdnsAdvertiser.AdvertisablePort([]));
    }

    [Fact]
    public void Unparseable_addresses_are_skipped_rather_than_throwing()
    {
        Assert.Equal(4533, MdnsAdvertiser.AdvertisablePort(["not a url", "http://0.0.0.0:4533"]));
    }

    // Kestrel binding both a loopback address and a real one is ordinary. The
    // reachable one is what a client has to be told about.
    [Fact]
    public void The_reachable_bind_wins_over_a_loopback_one_listed_first()
    {
        Assert.Equal(4533, MdnsAdvertiser.AdvertisablePort(["http://127.0.0.1:5599", "http://0.0.0.0:4533"]));
    }
}
