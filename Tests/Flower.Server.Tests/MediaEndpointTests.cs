using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// GET /api/flower/v1/stream and /download - Flower's own media routes, and since
// the OpenSubsonic adapter was retired (docs/SYNC-PLAN.md) the only ones.
//
// These are about the gate rather than the serving: who gets in, what a ticket
// may spend itself on, which budget the traffic is charged to, and the one verb
// the whole streaming path depends on being refused.
//
// A seeded track's file does not exist, so an admitted request answers 404
// (FlowerServerFixture seeds rows, not files). That is the signal used
// throughout: 404 means the gate let it through and the handler looked for the
// file, where 401 means it never got that far.
public class MediaEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    private string ASeededSongId => server.Seeded[0].Id.ToString("N");

    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // The credentials in the query string rather than in headers, which is the
    // shape that matters here: what opens a stream URL is a decoder or an
    // <audio> element, and neither can attach a header. See
    // PeerMediaClient.GetStreamUrlAsync, which builds exactly this.
    private async Task<HttpStatusCode> SignedGetAsync(
        DeviceSigningKey device, string path, string id, string remoteIp)
    {
        List<(string Key, string Value)> query = [("id", id)];
        List<(string Key, string Value)> identity =
        [
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-Alias", "Kitchen iPad"),
            ("X-Flower-Role", "client"),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        ];
        var (signature, timestamp, nonce) = device.Sign("GET", path, query.Concat(identity), body: []);
        var sent = query.Concat(identity).Concat(
        [
            ("X-Flower-Signature", signature),
            ("X-Flower-Timestamp", timestamp),
            ("X-Flower-Nonce", nonce),
        ]);

        return await GetAsync(path, "?" + string.Join("&", sent.Select(p =>
            $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}")), remoteIp);
    }

    private async Task<HttpStatusCode> GetAsync(
        string path, string query, string remoteIp, string method = "GET")
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Request.QueryString = new QueryString(query);
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        });

        return (HttpStatusCode)context.Response.StatusCode;
    }

    private async Task<T> WithAPairedDeviceAsync<T>(Func<DeviceSigningKey, Task<T>> body)
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        using var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64, isAdmin: false);

        try
        {
            return await body(device);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
        }
    }

    [Theory]
    [InlineData("/api/flower/v1/stream")]
    [InlineData("/api/flower/v1/download")]
    public async Task A_paired_device_reaches_the_media_routes_with_a_signed_url(string path)
    {
        var status = await WithAPairedDeviceAsync(d => SignedGetAsync(d, path, ASeededSongId, "10.0.5.1"));

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // 403 rather than 401, which is this group's own distinction: a caller it
    // holds no key for is "not trusted" (a client reads that as revoked and
    // unpairs), where a signature that merely failed to verify is a 401. A
    // request with no credentials at all is the former.
    [Theory]
    [InlineData("/api/flower/v1/stream")]
    [InlineData("/api/flower/v1/download")]
    public async Task An_unsigned_request_reaches_neither(string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(path, $"?id={ASeededSongId}", "10.0.5.2"));
    }

    // The browser player's whole path: a tab signs for a ticket, and the
    // <audio> element it hands the URL to presents nothing but that ticket.
    [Fact]
    public async Task A_stream_ticket_opens_the_stream_route()
    {
        var tickets = server.Services.GetRequiredService<StreamTicketService>();
        var (ticket, _) = tickets.Issue(ASeededSongId, "some-browser");

        var status = await GetAsync(
            "/api/flower/v1/stream", $"?id={ASeededSongId}&ticket={Uri.EscapeDataString(ticket)}", "10.0.5.3");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // The point of scoping it by route. A ticket is minted for one track and
    // must not become a key to the catalog, the playlists or the log - which is
    // what it would be if the group's gate simply accepted one anywhere.
    [Theory]
    [InlineData("/api/flower/v1/library")]
    [InlineData("/api/flower/v1/playlists")]
    [InlineData("/api/flower/v1/log/watermark")]
    public async Task A_stream_ticket_opens_nothing_else_in_the_group(string path)
    {
        var tickets = server.Services.GetRequiredService<StreamTicketService>();
        var (ticket, _) = tickets.Issue(ASeededSongId, "some-browser");

        var status = await GetAsync(path, $"?id={ASeededSongId}&ticket={Uri.EscapeDataString(ticket)}", "10.0.5.4");

        // Refused as an unknown caller: outside the media routes the ticket is
        // not consulted at all, so this is simply a request with no signature.
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // A ticket is scoped to the two media routes it was issued for, so it opens
    // nothing else on the surface - the catalog included. What it used to be
    // tested against was the retired /rest mirror; the scoping is the point, and
    // it outlives that surface.
    [Fact]
    public async Task A_stream_ticket_opens_no_route_but_the_media_ones()
    {
        var tickets = server.Services.GetRequiredService<StreamTicketService>();
        var (ticket, _) = tickets.Issue(ASeededSongId, "some-browser");

        var status = await GetAsync(
            "/api/flower/v1/library", $"?ticket={Uri.EscapeDataString(ticket)}", "10.0.5.5");

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // The route is MapGet, so a HEAD matches no endpoint and every client
    // reaches a track's length through the ranged-GET probe instead.
    // Flower.DeviceChecks pins the client half of this on five platforms; this
    // is the server half, and answering HEAD would take that probe out of every
    // check at once.
    //
    // 404 rather than routing's 405: WebUiHosting's fallback matches every
    // method and answers /api with a 404. Callers only ask whether the response
    // succeeded, so it makes no difference to them - but it is asserted as what
    // it is rather than what three comments used to claim it was.
    [Theory]
    [InlineData("/api/flower/v1/stream")]
    [InlineData("/api/flower/v1/download")]
    public async Task A_HEAD_to_a_media_route_is_refused_so_the_probe_stays_the_only_path(string path)
    {
        var status = await GetAsync(path, $"?id={ASeededSongId}", "10.0.5.6", method: "HEAD");

        Assert.False(((int)status) is >= 200 and < 300, $"a HEAD must not succeed, but got {(int)status}");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // The plane split, from the side that motivated it. Sync is sixty requests
    // a minute, which is the right budget for a handful of large manifests and
    // nothing like what an album costs: a probe, a body GET and a reopen per
    // track, doubled by decode-ahead. Sharing one budget means playing music
    // throttles syncing, and then whichever request comes next is the one
    // refused.
    [Fact]
    public async Task Streaming_does_not_spend_the_sync_budget()
    {
        const string ip = "10.0.5.7";

        await WithAPairedDeviceAsync(async device =>
        {
            // Has to stay above BulkLimiter's ceiling to demonstrate anything:
            // the whole claim is that these did not come out of the bulk
            // budget, and a loop shorter than that budget would pass whether
            // they did or not. Raised with it from twenty-five when the ceiling
            // went from twenty to sixty.
            for (var request = 0; request < 65; request++)
            {
                var status = await SignedGetAsync(device, "/api/flower/v1/stream", ASeededSongId, ip);
                Assert.Equal(HttpStatusCode.NotFound, status);
            }

            // Well past BulkLimiter's sixty, and the catalog is still reachable.
            Assert.NotEqual(
                HttpStatusCode.TooManyRequests,
                await SignedGetAsync(device, "/api/flower/v1/library", ASeededSongId, ip));

            return true;
        });
    }
}
