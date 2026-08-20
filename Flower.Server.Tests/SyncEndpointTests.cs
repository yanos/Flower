using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Tests;

// Flower's own /api/flower/v1/* sync protocol, which this server answered with
// a flat 404 until SyncEndpoints existed - a client paired fine and then failed
// its first library sync, since a Client pulls its catalog from its Server
// through GET /library, not through /rest.
public class SyncEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // Signed exactly the way LibrarySyncService/PlaylistSyncService sign:
    // identity in headers, empty query, the body hashed into the signature.
    private async Task<(HttpStatusCode Status, string Body, string? ETag)> SendAsync(
        DeviceSigningKey device, string method, string path, string remoteIp,
        string? body = null, string? ifNoneMatch = null)
    {
        var bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        var (signature, timestamp, nonce) = device.Sign(method, path, [], bodyBytes);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-Alias"] = "Kitchen iPad";
            c.Request.Headers["X-Flower-Role"] = "client";
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            if (ifNoneMatch != null)
                c.Request.Headers.IfNoneMatch = ifNoneMatch;
            if (body != null)
            {
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bodyBytes);
                c.Request.ContentLength = bodyBytes.Length;
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.ETag.ToString() is { Length: > 0 } etag ? etag : null);
    }

    private static async Task<DeviceSigningKey> TrustedDeviceAsync(TrustedPeerStore trustedPeers)
    {
        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64, isAdmin: false);
        return device;
    }

    [Fact]
    public async Task GET_library_returns_the_whole_catalog_in_one_response()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = await TrustedDeviceAsync(trustedPeers);

        try
        {
            var (status, body, etag) = await SendAsync(device, "GET", "/api/flower/v1/library", "10.0.2.1");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.NotNull(etag);
            // PascalCase with nulls omitted - whatever the client's
            // FlowerJsonContext reads, which is the actual contract here.
            var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(body)!;
            Assert.Equal(server.Services.GetRequiredService<DeviceSigningKey>().Fingerprint, manifest.DeviceFingerprint);
            Assert.Equal(server.Seeded.Length, manifest.Songs.Count);
            // The fields LibrarySyncMapper.ToPlaceholderTrack actually reads -
            // a manifest that round-trips structurally but arrives with empty
            // titles would merge in a library of blank rows.
            Assert.All(manifest.Songs, song => Assert.False(string.IsNullOrEmpty(song.Title)));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task GET_library_answers_304_for_the_token_the_caller_already_holds()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = await TrustedDeviceAsync(trustedPeers);

        try
        {
            var (_, _, etag) = await SendAsync(device, "GET", "/api/flower/v1/library", "10.0.2.2");
            var (status, body, _) = await SendAsync(
                device, "GET", "/api/flower/v1/library", "10.0.2.2", ifNoneMatch: etag);

            Assert.Equal(HttpStatusCode.NotModified, status);
            Assert.Empty(body);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task An_untrusted_device_gets_403_rather_than_the_catalog()
    {
        using var device = NewDevice();

        var (status, _, _) = await SendAsync(device, "GET", "/api/flower/v1/library", "10.0.2.3");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Playlists_round_trip_through_apply()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();
        using var device = await TrustedDeviceAsync(trustedPeers);

        try
        {
            // A playlist naming one seeded track by the tag/duration triple
            // PlaylistSyncMapper resolves against the library by SyncKey - a
            // track the server does not have would simply be dropped, which
            // would make this pass for the wrong reason.
            var track = server.Seeded[0];
            var pushed = new PlaylistSyncManifestDto(device.Fingerprint,
            [
                new PlaylistSyncPlaylistDto(Guid.NewGuid(), "From the phone", DateTimeOffset.UtcNow,
                [
                    new PlaylistSyncTrackDto(track.Title, track.Artists, track.Album, Track.RoundedSeconds(track.Duration)),
                ]),
            ]);

            var (applyStatus, _, _) = await SendAsync(
                device, "POST", "/api/flower/v1/playlists/apply", "10.0.2.4",
                body: JsonSerializer.Serialize(pushed));
            Assert.Equal(HttpStatusCode.NoContent, applyStatus);

            var (getStatus, body, _) = await SendAsync(device, "GET", "/api/flower/v1/playlists", "10.0.2.4");
            Assert.Equal(HttpStatusCode.OK, getStatus);

            var served = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(body)!;
            var playlist = Assert.Single(served.Playlists);
            Assert.Equal("From the phone", playlist.Name);
            Assert.Equal(track.Title, Assert.Single(playlist.Tracks).Title);
        }
        finally
        {
            library.ReplacePlaylists([]);
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }
}
