using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
using Flower.Persistence;
using Flower.Server.Endpoints;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The browser head, on the routes it lives on: GET /api/flower/v1/library,
// POST /api/flower/v1/stream-tickets, cover art, and writing a playlist back.
//
// These used to be reached with an admin-session bearer token, because
// .NET-for-WebAssembly has no asymmetric crypto and a tab had nothing to sign
// with. It has a WebCrypto keypair now (see BrowserPeerCredentials), so what a
// tab presents here is an ordinary device signature and these routes are gated
// on nothing else. Pinned rather than described because the claim that matters
// is a negative one - the bearer path is gone, not merely unused.
//
// A real tab's key lives behind crypto.subtle and cannot be reached from a test
// process. What can be pinned is the wire: the same curve, the same canonical
// form, the same identity headers a tab sends. That is the whole of what the
// server sees, so signing here with a plain ECDsa is not a weaker stand-in - it
// is the same bytes.
public class BrowserDeviceAccessTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // Exactly the header set BrowserPeerCredentials.AuthorizeAsync produces.
    private async Task<(HttpStatusCode Status, string Body)> AsBrowserAsync(
        DeviceSigningKey? device, string method, string path, string remoteIp,
        string? query = null, string? body = null)
    {
        var bodyBytes = body == null ? [] : System.Text.Encoding.UTF8.GetBytes(body);
        var identity = device == null
            ? []
            : new List<(string Key, string Value)>
            {
                ("X-Flower-Fingerprint", device.Fingerprint),
                ("X-Flower-Alias", "Living room browser"),
                ("X-Flower-Role", "client"),
                ("X-Flower-PublicKey", device.PublicKeyBase64),
            };

        var queryPairs = ParseQuery(query);
        var signed = device?.Sign(method, path, queryPairs.Concat(identity), bodyBytes);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            if (query != null)
                c.Request.QueryString = new QueryString(query);
            if (body != null)
            {
                c.Request.Body = new MemoryStream(bodyBytes);
                c.Request.ContentType = "application/json";
                c.Request.ContentLength = bodyBytes.Length;
            }
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            foreach (var (key, value) in identity)
                c.Request.Headers[key] = value;
            if (signed is var (signature, timestamp, nonce))
            {
                c.Request.Headers["X-Flower-Signature"] = signature;
                c.Request.Headers["X-Flower-Timestamp"] = timestamp;
                c.Request.Headers["X-Flower-Nonce"] = nonce;
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static List<(string Key, string Value)> ParseQuery(string? query) =>
        string.IsNullOrEmpty(query)
            ? []
            : query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(parts => (Uri.UnescapeDataString(parts[0]),
                                  parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : ""))
                .ToList();

    private async Task<DeviceSigningKey> APairedBrowserAsync()
    {
        var device = NewDevice();
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, "Living room browser", device.PublicKeyBase64, isAdmin: true);
        return device;
    }

    // The whole bootstrap, as a browser tab really performs it: generate a key,
    // redeem the code that was in the page fragment, then sign. Nothing about
    // this is browser-specific on the wire, which is the point - the tab is a
    // device, and pairing one is one mechanism.
    [Fact]
    public async Task A_tab_redeems_the_code_from_its_url_and_then_signs_its_way_in()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var pairing = server.Services.GetRequiredService<PairingCodeService>();
        using var device = NewDevice();

        try
        {
            // Before the code is spent, this key is nobody.
            var (before, _) = await AsBrowserAsync(device, "GET", "/api/flower/v1/library", "10.0.9.10");
            Assert.Equal(HttpStatusCode.Forbidden, before);

            var (code, _) = pairing.GenerateCode(grantsAdmin: true);
            var redeemed = await server.Server.SendAsync(c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/api/flower/v1/pair-redeem";
                c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.9.10");
                var (signature, timestamp, nonce) = device.Sign("POST", "/api/flower/v1/pair-redeem", [], []);
                c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
                c.Request.Headers["X-Flower-Alias"] = "Living room browser";
                c.Request.Headers["X-Flower-PublicKey"] = device.PublicKeyBase64;
                c.Request.Headers["X-Flower-PairingCode"] = code;
                c.Request.Headers["X-Flower-Signature"] = signature;
                c.Request.Headers["X-Flower-Timestamp"] = timestamp;
                c.Request.Headers["X-Flower-Nonce"] = nonce;
            });
            Assert.Equal(StatusCodes.Status200OK, redeemed.Response.StatusCode);

            // ...and afterwards it is a device, on its own key, with no
            // server-minted credential involved anywhere.
            var (library, body) = await AsBrowserAsync(device, "GET", "/api/flower/v1/library", "10.0.9.10");
            Assert.Equal(HttpStatusCode.OK, library);
            Assert.Equal(server.Seeded.Length, JsonSerializer.Deserialize<LibrarySyncManifestDto>(body)!.Songs.Count);

            // The code granted admin, so the settings page opens too - which is
            // what the desktop's "Server Settings..." button is issuing.
            var settings = await AsBrowserAsync(device, "GET", "/api/admin/settings", "10.0.9.10");
            Assert.Equal(HttpStatusCode.OK, settings.Status);

            // Single-use: a reload whose fragment survived cannot pair a second
            // device off the same code.
            Assert.False(pairing.TryConsume(code, out _));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_signed_tab_mints_a_ticket_bound_to_the_track_and_to_itself()
    {
        using var device = await APairedBrowserAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var tickets = server.Services.GetRequiredService<StreamTicketService>();

        try
        {
            var (status, body) = await AsBrowserAsync(
                device, "POST", "/api/flower/v1/stream-tickets", "10.0.9.2", query: "?id=sg-7");
            Assert.Equal(HttpStatusCode.OK, status);

            var minted = JsonSerializer.Deserialize<StreamTicketResponse>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            Assert.True(tickets.TryRedeem(minted.Ticket, "sg-7", DateTimeOffset.UtcNow));
            // Bound to one track. The tab can sign for itself now, but what it
            // hands an <audio> element still buys exactly one song.
            Assert.False(tickets.TryRedeem(minted.Ticket, "sg-8", DateTimeOffset.UtcNow));

            // Attributed to the minting device, so revoking that browser kills
            // its outstanding tickets rather than leaving them playable.
            Assert.Equal(1, tickets.RevokeFor(device.Fingerprint));
            Assert.False(tickets.TryRedeem(minted.Ticket, "sg-7", DateTimeOffset.UtcNow));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_signed_tab_reaches_cover_art_that_rest_would_have_refused_it()
    {
        using var device = await APairedBrowserAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();

        try
        {
            var albumId = SubsonicIdentity.AlbumId("Aurora", "Alpha Album");
            var query = $"?id={Uri.EscapeDataString(albumId)}";

            var (signed, _) = await AsBrowserAsync(device, "GET", "/api/flower/v1/cover-art", "10.0.9.6", query);
            var (unsigned, _) = await AsBrowserAsync(null, "GET", "/api/flower/v1/cover-art", "10.0.9.6", query);

            // NotFound, not OK: the fixture seeds rows whose paths point at no
            // real file, so there is no art to read. That is the assertion -
            // reaching the handler at all is what the signature buys, and a 404
            // says it got there where a 403 would say the gate stopped it.
            Assert.Equal(HttpStatusCode.NotFound, signed);
            Assert.Equal(HttpStatusCode.Forbidden, unsigned);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_signed_tab_writes_a_playlist_back_and_can_then_read_it_again()
    {
        // Worth pinning on this side rather than only in OriginPlaylistWriter's
        // own tests, because "the tab produces a well-formed POST" and "this
        // server accepts that POST" are two different claims and only the second
        // one is about the gate.
        using var device = await APairedBrowserAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var library = server.Services.GetRequiredService<Library>();

        try
        {
            // Named by the tag/duration triple PlaylistSyncMapper resolves
            // against the library, not by path - a track this server does not
            // have would be dropped and this would pass for the wrong reason.
            var track = server.Seeded[0];
            var pushed = JsonSerializer.Serialize(new PlaylistSyncManifestDto("browser",
            [
                new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Made in a tab", DateTimeOffset.UtcNow,
                [
                    new PlaylistSyncTrackDto(track.Title, track.Artists, track.Album, Track.RoundedSeconds(track.Duration)),
                ]),
            ]));

            var (refused, _) = await AsBrowserAsync(
                null, "POST", "/api/flower/v1/playlists/apply", "10.0.9.7", body: pushed);
            Assert.Equal(HttpStatusCode.Forbidden, refused);

            var (applied, _) = await AsBrowserAsync(
                device, "POST", "/api/flower/v1/playlists/apply", "10.0.9.7", body: pushed);
            Assert.Equal(HttpStatusCode.NoContent, applied);

            var (read, body) = await AsBrowserAsync(device, "GET", "/api/flower/v1/playlists", "10.0.9.7");
            Assert.Equal(HttpStatusCode.OK, read);

            var served = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(body)!;
            var playlist = Assert.Single(served.Playlists);
            Assert.Equal("Made in a tab", playlist.Name);
            Assert.Equal(track.Title, Assert.Single(playlist.Tracks).Title);
        }
        finally
        {
            library.ReplacePlaylists([]);
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task A_revoked_browser_stops_getting_the_catalog_immediately()
    {
        using var device = await APairedBrowserAsync();
        await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(device.Fingerprint);

        var (status, _) = await AsBrowserAsync(device, "GET", "/api/flower/v1/library", "10.0.9.3");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // The negative that the whole change rests on. A session token used to open
    // both of these; the header is now just a header.
    [Fact]
    public async Task A_bearer_token_opens_nothing_on_the_sync_surface()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        var library = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/flower/v1/library";
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.9.4");
            c.Request.Headers["X-Flower-Admin-Session"] = token;
        });
        var ticket = await server.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/api/flower/v1/stream-tickets";
            c.Request.QueryString = new QueryString("?id=sg-7");
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.9.4");
            c.Request.Headers["X-Flower-Admin-Session"] = token;
        });

        // 403 on the sync route because the caller presented no fingerprint this
        // server has a key for, which is the same answer an unknown signer gets.
        Assert.Equal(StatusCodes.Status403Forbidden, library.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, ticket.Response.StatusCode);
    }

    [Fact]
    public async Task No_credentials_at_all_still_opens_nothing()
    {
        var (library, _) = await AsBrowserAsync(null, "GET", "/api/flower/v1/library", "10.0.9.5");
        var (ticket, _) = await AsBrowserAsync(
            null, "POST", "/api/flower/v1/stream-tickets", "10.0.9.5", query: "?id=sg-7");

        Assert.Equal(HttpStatusCode.Forbidden, library);
        Assert.Equal(HttpStatusCode.Unauthorized, ticket);
    }
}
