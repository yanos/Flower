using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Persistence;
using Flower.Server.Endpoints;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// The admin session token, used on the two routes it did not used to reach:
// GET /api/flower/v1/library and POST /api/flower/v1/stream-tickets. Both are
// signature-gated, and the browser head is the one caller that cannot sign
// anything - .NET-for-WebAssembly has no asymmetric crypto - so without this it
// is a music player with an empty library and nothing it can play.
//
// The point of testing it here rather than trusting PeerOrSessionAuth's own
// shape: this deliberately widens a bearer token past /api/admin, so what it
// does and does not open has to be pinned rather than described.
public class BrowserSessionSyncAccessTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // No signature headers at all, which is the whole point: this is exactly
    // what a browser tab can produce.
    private async Task<(HttpStatusCode Status, string Body)> SendWithSessionAsync(
        string method, string path, string remoteIp, string? token, string? query = null)
    {
        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            if (query != null)
                c.Request.QueryString = new QueryString(query);
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            if (token != null)
                // Off AdminSessionCredentials rather than AdminEndpoints,
                // which is internal - and better this way round: that is the
                // client class the browser actually sends with, so this pins
                // the two ends of the header to each other rather than to a
                // literal repeated in both.
                c.Request.Headers[AdminSessionCredentials.HeaderName] = token;
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    // A device that is trusted *and* holds a live session, which is the state a
    // browser tab is in after the desktop's "Server Settings..." button hands it
    // one in the URL fragment.
    private async Task<(DeviceSigningKey Device, string Token)> SessionForATrustedDeviceAsync()
    {
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var sessions = server.Services.GetRequiredService<AdminSessionService>();

        var device = NewDevice();
        await trustedPeers.ApproveAsync(device.Fingerprint, "Living room browser", device.PublicKeyBase64, isAdmin: true);
        var (token, _) = sessions.Issue(device.Fingerprint);
        return (device, token);
    }

    [Fact]
    public async Task A_session_token_reads_the_catalog_with_no_signature_at_all()
    {
        var (device, token) = await SessionForATrustedDeviceAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();

        try
        {
            var (status, body) = await SendWithSessionAsync("GET", "/api/flower/v1/library", "10.0.9.1", token);

            Assert.Equal(HttpStatusCode.OK, status);
            var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(body)!;
            Assert.Equal(server.Seeded.Length, manifest.Songs.Count);
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
            device.Dispose();
        }
    }

    [Fact]
    public async Task A_session_token_mints_a_ticket_bound_to_the_track_and_to_the_device_that_minted_it()
    {
        var (device, token) = await SessionForATrustedDeviceAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();
        var tickets = server.Services.GetRequiredService<StreamTicketService>();

        try
        {
            var (status, body) = await SendWithSessionAsync(
                "POST", "/api/flower/v1/stream-tickets", "10.0.9.2", token, query: "?id=sg-7");
            Assert.Equal(HttpStatusCode.OK, status);

            var minted = JsonSerializer.Deserialize<StreamTicketResponse>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            Assert.True(tickets.TryRedeem(minted.Ticket, "sg-7", DateTimeOffset.UtcNow));
            // Bound to one track, exactly as a signed mint is - a session does
            // not buy a wider ticket than a signature would have.
            Assert.False(tickets.TryRedeem(minted.Ticket, "sg-8", DateTimeOffset.UtcNow));

            // Attributed to the minting device rather than to the session, so
            // revoking that device kills the ticket too. A ticket credited to
            // nobody would outlive the revoke for its whole lifetime.
            Assert.Equal(1, tickets.RevokeFor(device.Fingerprint));
            Assert.False(tickets.TryRedeem(minted.Ticket, "sg-7", DateTimeOffset.UtcNow));
        }
        finally
        {
            await trustedPeers.RevokeAsync(device.Fingerprint);
            device.Dispose();
        }
    }

    [Fact]
    public async Task A_session_whose_device_has_since_been_revoked_no_longer_opens_the_catalog()
    {
        var (device, token) = await SessionForATrustedDeviceAsync();
        var trustedPeers = server.Services.GetRequiredService<TrustedPeerStore>();

        // Revoked from the trust store only - the token itself is untouched and
        // still well inside its lifetime. Revoking normally kills sessions too
        // (AdminSessionService.RevokeFor), but a gate that relies on that having
        // been called is a gate that fails open the day a second revocation path
        // appears, so the trust check is made live on every request.
        await trustedPeers.RevokeAsync(device.Fingerprint);
        device.Dispose();

        var (status, _) = await SendWithSessionAsync("GET", "/api/flower/v1/library", "10.0.9.3", token);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task A_made_up_session_token_opens_nothing()
    {
        var forged = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        var (library, _) = await SendWithSessionAsync("GET", "/api/flower/v1/library", "10.0.9.4", forged);
        var (ticket, _) = await SendWithSessionAsync(
            "POST", "/api/flower/v1/stream-tickets", "10.0.9.4", forged, query: "?id=sg-7");

        // 403 on the sync route because the caller presented no fingerprint this
        // server has a key for, which is the same answer an unknown signer gets.
        Assert.Equal(HttpStatusCode.Forbidden, library);
        Assert.Equal(HttpStatusCode.Unauthorized, ticket);
    }

    [Fact]
    public async Task No_credentials_at_all_still_opens_nothing()
    {
        var (library, _) = await SendWithSessionAsync("GET", "/api/flower/v1/library", "10.0.9.5", token: null);
        var (ticket, _) = await SendWithSessionAsync(
            "POST", "/api/flower/v1/stream-tickets", "10.0.9.5", token: null, query: "?id=sg-7");

        Assert.Equal(HttpStatusCode.Forbidden, library);
        Assert.Equal(HttpStatusCode.Unauthorized, ticket);
    }
}
