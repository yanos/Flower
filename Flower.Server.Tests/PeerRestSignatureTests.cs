using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Tests;

// Path A over /rest: a paired Flower client browses this server with a device
// signature and no username/password at all, exactly the way it browses
// another client's embedded SyncHttpServer (see PeerOpenSubsonicClientFactory,
// which sends empty u/p on purpose). Without this the pairing flow completed
// and then every browse came back "Wrong username or password", because the
// /rest filter only knew about path-B credentials and stream tickets.
public class PeerRestSignatureTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // The same headers PeerOpenSubsonicClientFactory builds, over the same
    // empty-credential query - if these two ever disagree on what is signed,
    // the real client stops being able to reach the real server.
    private async Task<(HttpStatusCode Status, string Body)> SendSignedAsync(
        DeviceSigningKey device, string path, string remoteIp, bool inQuery = false)
    {
        var query = new List<(string Key, string Value)>
        {
            ("u", ""), ("t", ""), ("s", ""), ("v", "1.16.1"), ("c", "tests"), ("f", "json"),
        };
        var identity = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-Alias", "Kitchen iPad"),
            ("X-Flower-Role", "client"),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        };
        var (signature, timestamp, nonce) = device.Sign("GET", path, query.Concat(identity), body: []);
        var credentials = identity.Concat(
        [
            ("X-Flower-Signature", signature),
            ("X-Flower-Timestamp", timestamp),
            ("X-Flower-Nonce", nonce),
        ]).ToList();

        var sent = inQuery ? query.Concat(credentials).ToList() : query;
        var queryString = "?" + string.Join("&", sent.Select(p =>
            $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Request.QueryString = new QueryString(queryString);
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            if (!inQuery)
            {
                foreach (var (key, value) in credentials)
                    c.Request.Headers[key] = value;
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static async Task TrustAsync(TrustedPeerStore trustedPeers, DeviceSigningKey device) =>
        await trustedPeers.ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64, isAdmin: false);

    [Fact]
    public async Task A_paired_device_browses_with_a_signature_and_no_password()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();
        await TrustAsync(trustedPeers, device);

        try
        {
            var (status, body) = await SendSignedAsync(device, "/rest/getAlbumList2", "10.0.1.1");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"status\":\"ok\"", body);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // The stream/cover-art case: a URL handed to LibVLC can't carry headers,
    // so the same credentials travel as query params instead.
    [Fact]
    public async Task The_signature_is_accepted_in_the_query_string_too()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();
        await TrustAsync(trustedPeers, device);

        try
        {
            var (status, body) = await SendSignedAsync(device, "/rest/getAlbumList2", "10.0.1.2", inQuery: true);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"status\":\"ok\"", body);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task An_unpaired_device_is_still_refused()
    {
        using var device = NewDevice();

        var (_, body) = await SendSignedAsync(device, "/rest/getAlbumList2", "10.0.1.3");

        // Refused, and told why: a signing device that is not trusted is a
        // pairing problem, and saying "wrong username or password" to a client
        // that holds no password at all is what sent the user hunting for one.
        Assert.Contains("not paired with this server", body);
    }

    [Fact]
    public async Task A_revoked_device_loses_access()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();
        await TrustAsync(trustedPeers, device);
        await trustedPeers.RevokeAsync(device.Fingerprint);

        var (_, body) = await SendSignedAsync(device, "/rest/getAlbumList2", "10.0.1.4");

        Assert.Contains("not paired with this server", body);
    }
}
