using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // callerFingerprint alone claims an identity without proving it - the shape
    // an attacker can trivially produce, since fingerprints are public. Passing
    // `signer` instead proves it, the way NetworkDiscoveryService.
    // ResolveAliasAsync now does.
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetInfoAsync(
        string? callerFingerprint = null, DeviceSigningKey? signer = null)
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = SyncProtocol.InfoPath;
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
            if (callerFingerprint != null)
                c.Request.Headers["X-Flower-Fingerprint"] = callerFingerprint;

            if (signer == null)
                return;

            var identity = new List<(string Key, string Value)>
            {
                ("X-Flower-Fingerprint", signer.Fingerprint),
                ("X-Flower-Alias", "Kitchen iPad"),
                ("X-Flower-Role", "client"),
                ("X-Flower-PublicKey", signer.PublicKeyBase64),
            };
            var (signature, timestamp, nonce) = signer.Sign("GET", SyncProtocol.InfoPath, identity, body: []);
            foreach (var (key, value) in identity.Concat(
                [("X-Flower-Signature", signature), ("X-Flower-Timestamp", timestamp), ("X-Flower-Nonce", nonce)]))
            {
                c.Request.Headers[key] = value;
            }
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
        // This is now the only thing on the network answering this handshake at
        // all - a Flower app browses, and no longer advertises itself. The
        // field survives the collapse of the two-kinds-of-peer distinction
        // because it is what a sighting says it is, and a peer answering with
        // anything else is a Flower this client does not understand.
        var (_, body) = await GetInfoAsync();

        Assert.Equal("server", body.GetProperty("deviceType").GetString());
        Assert.False(body.TryGetProperty("isServer", out _));
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
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Test peer", device.PublicKeyBase64);

        try
        {
            var (_, body) = await GetInfoAsync(signer: device);
            Assert.True(body.GetProperty("trustsCaller").GetBoolean());
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // What a client reads to decide whether to offer its holder the
    // administrator-only controls at all - "Generate Pairing Code", mainly.
    // Same three-state shape as trustsCaller, and for the same reason: a
    // caller who proved nothing is told nothing.
    [Fact]
    public async Task Omits_callerIsAdmin_when_the_caller_did_not_identify_itself()
    {
        var (_, body) = await GetInfoAsync();

        Assert.True(!body.TryGetProperty("callerIsAdmin", out var admin)
                    || admin.ValueKind == JsonValueKind.Null);
    }

    // Paired, and an ordinary listener. Every admin route would refuse this
    // device, so the client hides those controls rather than showing ones that
    // can only fail - see MainViewModel.CanInviteDeviceToSelectedServer.
    [Fact]
    public async Task Reports_callerIsAdmin_false_for_a_paired_listener()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Listener", device.PublicKeyBase64);

        try
        {
            var (_, body) = await GetInfoAsync(signer: device);
            Assert.False(body.GetProperty("callerIsAdmin").GetBoolean());
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task Reports_callerIsAdmin_true_for_a_paired_administrator()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Owner's desktop", device.PublicKeyBase64, isAdmin: true);

        try
        {
            var (_, body) = await GetInfoAsync(signer: device);
            Assert.True(body.GetProperty("callerIsAdmin").GetBoolean());
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // A fingerprint is public - it is in this very response - so claiming one
    // must buy nothing. Answering "yes, you are an administrator" to an unsigned
    // claim would tell an attacker which device to go after.
    [Fact]
    public async Task Withholds_callerIsAdmin_from_a_caller_that_only_claims_an_admin_fingerprint()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Owner's desktop", device.PublicKeyBase64, isAdmin: true);

        try
        {
            var (_, body) = await GetInfoAsync(callerFingerprint: device.Fingerprint);
            Assert.True(!body.TryGetProperty("callerIsAdmin", out var admin)
                        || admin.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task Is_reachable_without_any_credential()
    {
        // Deliberately ungated: a device has to learn this server's public key
        // here before either side can evaluate trust at all. What a signature
        // buys is a fuller answer, not access.
        var (status, _) = await GetInfoAsync();

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // The address list is the half of this handshake that describes where the
    // server lives rather than who it is - including, once a tailnet is in play,
    // an address that is otherwise only handed out by invitation. See
    // docs/OPEN-INTERNET-REVIEW.md.

    [Fact]
    public async Task Withholds_its_addresses_from_a_caller_that_did_not_identify_itself()
    {
        var (_, body) = await GetInfoAsync();

        Assert.True(!body.TryGetProperty("addresses", out var addresses)
                    || addresses.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Withholds_its_addresses_from_a_signed_but_untrusted_caller()
    {
        var (_, body) = await GetInfoAsync(signer: NewDevice());

        Assert.True(!body.TryGetProperty("addresses", out var addresses)
                    || addresses.ValueKind == JsonValueKind.Null);
    }

    // The finding itself, as a test. A fingerprint is public - it is in this
    // very response, and in every pairing invite - so claiming one has to buy
    // nothing. Before this, claiming a trusted peer's fingerprint was enough to
    // be told the server's tailnet address.
    [Fact]
    public async Task Withholds_its_addresses_from_a_caller_that_claims_a_trusted_fingerprint_without_signing()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Test peer", device.PublicKeyBase64);

        try
        {
            var (_, body) = await GetInfoAsync(callerFingerprint: device.Fingerprint);

            Assert.True(!body.TryGetProperty("addresses", out var addresses)
                        || addresses.ValueKind == JsonValueKind.Null);
            // And it is told nothing about the pairing either: a key is on file,
            // so this is a signature failure rather than a revocation.
            Assert.True(!body.TryGetProperty("trustsCaller", out var trusts)
                        || trusts.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task Reports_its_addresses_to_a_verified_trusted_peer()
    {
        // The other half, and the one REMOTE-ACCESS-PLAN.md depends on: a paired
        // client has to keep learning where this server can be reached, or it
        // stops working the moment it leaves the LAN.
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Test peer", device.PublicKeyBase64);

        try
        {
            var (_, body) = await GetInfoAsync(signer: device);

            var addresses = body.GetProperty("addresses");
            Assert.Equal(JsonValueKind.Array, addresses.ValueKind);
            Assert.All(addresses.EnumerateArray(), a => Assert.StartsWith("http", a.GetString()!));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    private static IEnumerable<string?> Addresses(JsonElement body) =>
        body.GetProperty("addresses").EnumerateArray().Select(a => a.GetString());

    [Fact]
    public async Task Reports_an_AdvertisedHost_changed_since_startup_without_a_restart()
    {
        // The settings route lets an administrator edit AdvertisedHost and
        // promises no restart (AdminEndpoints leaves it out of RestartRequired).
        // This route is what has to make that promise true: bound through
        // IOptions it answered with the value from process start, so the page
        // showed the new host while every client kept being handed the old one.
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var configuration = server.Services.GetRequiredService<IConfiguration>();
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Test peer", device.PublicKeyBase64);

        const string advertised = "https://flower.example:4543";
        try
        {
            var (_, before) = await GetInfoAsync(signer: device);
            Assert.DoesNotContain(advertised, Addresses(before));

            configuration["Flower:AdvertisedHost"] = advertised;
            (configuration as IConfigurationRoot)?.Reload();

            var (_, after) = await GetInfoAsync(signer: device);
            Assert.Contains(advertised, Addresses(after));
        }
        finally
        {
            configuration["Flower:AdvertisedHost"] = "";
            (configuration as IConfigurationRoot)?.Reload();
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
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

    // Two listeners is the ordinary shape now: a plain one for third-party
    // OpenSubsonic clients and old bookmarks, a TLS one for paired Flower
    // clients. /info reports both, which means asking for each by scheme.
    // See DiscoveryEndpoints.ReachableOrigins.
    [Fact]
    public void Each_scheme_reports_its_own_listener()
    {
        string[] bound = ["http://0.0.0.0:4533", "https://0.0.0.0:4534"];

        Assert.Equal(4533, MdnsAdvertiser.AdvertisablePort(bound, Uri.UriSchemeHttp));
        Assert.Equal(4534, MdnsAdvertiser.AdvertisablePort(bound, Uri.UriSchemeHttps));
    }

    // TLS turned off (Flower:HttpsPort = 0), which has to read as "there is no
    // https origin" rather than falling back to the plain port under an https
    // scheme - a client told to dial https on 4533 would fail every probe.
    [Fact]
    public void A_scheme_nothing_is_bound_for_reports_nothing()
    {
        Assert.Null(MdnsAdvertiser.AdvertisablePort(["http://0.0.0.0:4533"], Uri.UriSchemeHttps));
    }

    // Unfiltered, the answer is still the plain listener - mDNS asks this way,
    // and a sighting is dialled at a bare IP where a certificate has no name to
    // be issued for and the client holds nothing to pin with yet.
    [Fact]
    public void Asking_without_a_scheme_still_finds_the_plain_listener_first()
    {
        Assert.Equal(4533, MdnsAdvertiser.AdvertisablePort(["http://0.0.0.0:4533", "https://0.0.0.0:4534"]));
    }
}
