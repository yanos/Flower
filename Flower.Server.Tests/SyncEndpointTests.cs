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

    // The 401/403 split, from the endpoint's side. A trusted peer whose
    // signature has simply gone stale - a laptop that suspended with the
    // request in flight and delivered it many minutes later - must not be
    // answered like a revoked one: a client reads 403 off a sync route as
    // "this server has revoked me" and unpairs itself permanently, which is
    // how a real pairing was lost. See
    // PeerSignatureAuth.AuthenticateTrustedPeer.
    [Fact]
    public async Task A_trusted_device_whose_signature_went_stale_gets_401_not_403()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        var fingerprint = SignedRequestCanonicalizer.ComputeFingerprint(publicKeyRaw);
        await trustedPeers.ApproveAsync(fingerprint, "Sleepy Laptop", Convert.ToBase64String(publicKeyRaw), isAdmin: false);

        try
        {
            // Signed correctly, just seventeen minutes ago.
            const string path = "/api/flower/v1/library";
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(-17).ToUnixTimeSeconds().ToString();
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var signature = Convert.ToBase64String(ecdsa.SignData(
                SignedRequestCanonicalizer.Build("GET", path, [], [], timestamp, nonce),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

            var context = await server.Server.SendAsync(c =>
            {
                c.Request.Method = "GET";
                c.Request.Path = path;
                c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.2.4");
                c.Request.Headers["X-Flower-Fingerprint"] = fingerprint;
                c.Request.Headers["X-Flower-Signature"] = signature;
                c.Request.Headers["X-Flower-Timestamp"] = timestamp;
                c.Request.Headers["X-Flower-Nonce"] = nonce;
            });

            Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)context.Response.StatusCode);
        }
        finally
        {
            await trustedPeers.RevokeAsync(fingerprint);
        }
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

    // The return leg. A play reported here is stored on this server and has to
    // come back out in the manifest, or a tab counts a play and then never sees
    // it again - the count was kept and never served, which is how this looked
    // in a real tab before SubsonicMapper.ToChild filled these two fields.
    [Fact]
    public async Task The_manifest_carries_this_servers_own_counts_and_last_played_back()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();
        using var device = await TrustedDeviceAsync(trustedPeers);
        var track = library.Tracks.First(t => t.Title == "Second Song");
        var countBefore = track.PlayCount;

        try
        {
            var report = new PlayReportDto(
            [
                new PlayEventDto(Guid.NewGuid().ToString("N"), track.Id.ToKey(),
                    DateTimeOffset.UtcNow, Started: true, Completed: true),
            ]);
            await SendAsync(device, "POST", "/api/flower/v1/plays", "10.0.2.8",
                body: JsonSerializer.Serialize(report));

            var (_, body, _) = await SendAsync(device, "GET", "/api/flower/v1/library", "10.0.2.8");
            var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(body)!;
            var song = manifest.Songs.Single(s => s.Id == track.Id.ToKey());

            // Under this server's own fingerprint - a client files it as this
            // device's tally, not as its own. See Track.RemotePlayCounts.
            var fingerprint = server.Services.GetRequiredService<DeviceSigningKey>().Fingerprint;
            Assert.Equal(countBefore + 1, song.PlayCounts![fingerprint]);
            Assert.NotNull(song.LastPlayed);
        }
        finally
        {
            track.PlayCount = countBefore;
            track.LastPlayedAt = null;
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // The two halves a browser tab reports separately, so the server ends up
    // with the History a local player would have had - a skipped track stamped
    // as played without its count claiming a listen. See IPlayReporter.
    [Fact]
    public async Task A_reported_play_counts_and_stamps_the_track_it_names()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();
        using var device = await TrustedDeviceAsync(trustedPeers);
        var track = library.Tracks.First(t => t.Title == "Love Song");
        var countBefore = track.PlayCount;

        try
        {
            var report = new PlayReportDto(
            [
                new PlayEventDto(Guid.NewGuid().ToString("N"), track.Id.ToKey(),
                    DateTimeOffset.UtcNow, Started: true, Completed: false),
                new PlayEventDto(Guid.NewGuid().ToString("N"), track.Id.ToKey(),
                    DateTimeOffset.UtcNow, Started: false, Completed: true),
            ]);

            var (status, _, _) = await SendAsync(
                device, "POST", "/api/flower/v1/plays", "10.0.2.5",
                body: JsonSerializer.Serialize(report));

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(countBefore + 1, track.PlayCount);
            Assert.NotNull(track.LastPlayedAt);
        }
        finally
        {
            track.PlayCount = countBefore;
            track.LastPlayedAt = null;
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // What makes the reporter's retry safe: a batch the server applied but
    // whose response never came back is re-sent verbatim, and an increment
    // applied twice is simply wrong. The event id is what stops it.
    [Fact]
    public async Task The_same_play_event_sent_twice_is_only_counted_once()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();
        using var device = await TrustedDeviceAsync(trustedPeers);
        var track = library.Tracks.First(t => t.Title == "Alpha Song");
        var countBefore = track.PlayCount;

        try
        {
            var body = JsonSerializer.Serialize(new PlayReportDto(
            [
                new PlayEventDto("event-sent-twice", track.Id.ToKey(),
                    DateTimeOffset.UtcNow, Started: true, Completed: true),
            ]));

            await SendAsync(device, "POST", "/api/flower/v1/plays", "10.0.2.6", body: body);
            await SendAsync(device, "POST", "/api/flower/v1/plays", "10.0.2.6", body: body);

            Assert.Equal(countBefore + 1, track.PlayCount);
        }
        finally
        {
            track.PlayCount = countBefore;
            track.LastPlayedAt = null;
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    // A tab whose library is stale, or one pointed at a different server's
    // track. Nothing to count it against, and nothing to fail over either -
    // the rest of the batch still lands.
    [Fact]
    public async Task A_play_of_a_track_this_server_does_not_have_is_ignored()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();
        using var device = await TrustedDeviceAsync(trustedPeers);
        var track = library.Tracks.First(t => t.Title == "Beta Song");
        var countBefore = track.PlayCount;

        try
        {
            var report = new PlayReportDto(
            [
                new PlayEventDto(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToKey(),
                    DateTimeOffset.UtcNow, Started: true, Completed: true),
                new PlayEventDto(Guid.NewGuid().ToString("N"), track.Id.ToKey(),
                    DateTimeOffset.UtcNow, Started: true, Completed: true),
            ]);

            var (status, _, _) = await SendAsync(
                device, "POST", "/api/flower/v1/plays", "10.0.2.7",
                body: JsonSerializer.Serialize(report));

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Equal(countBefore + 1, track.PlayCount);
        }
        finally
        {
            track.PlayCount = countBefore;
            track.LastPlayedAt = null;
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }
}
