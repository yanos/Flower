using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// The client side of pairing: redeeming a one-time code against a
// Flower.Server (see that project's PairingEndpoints). Called from
// MainViewModel.PairWithServer, itself only reachable via an explicit user
// action - never off the back of an incidental request like AlbumArtLoader's
// remote art fetch.
public class PeerPairingService
{
    // A code redemption is a near-instant round trip, but it is also the one
    // request a user is watching a spinner for, so it gets a longer leash than
    // the background sync calls before it gives up and says so.
    // Pinned, for the same reason LibrarySyncService's is - see its remarks.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromSeconds(30));

    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _signingKey;
    private readonly ILogger<PeerPairingService> _logger;

    public PeerPairingService(DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, ILogger<PeerPairingService> logger)
    {
        _deviceIdentity = deviceIdentity;
        _signingKey = signingKey;
        _logger = logger;
    }

    // How pairing works, and the only way it works: nobody sits in front of a
    // headless server to tap Allow, so an admin issues a one-time code out of
    // band (a QR, or read out over the phone) and this redeems it - see
    // Flower.Server's PairingEndpoints and SYNC-PLAN.md's "Passwordless by
    // design", path A.
    //
    // The request is self-signed: the server has never seen this key before,
    // so the signature proves only that the sender holds the key it is
    // presenting, and the code is what supplies the authorization.
    public async Task<string?> RedeemPairingCodeAsync(DiscoveredDevice device, string code)
    {
        try
        {
            const string path = "/api/flower/v1/pair-redeem";
            var (signature, timestamp, nonce) = _signingKey.Sign("POST", path, [], body: []);

            using var request = new HttpRequestMessage(HttpMethod.Post, device.Url(path));
            // Percent-encoded, as on every header-transport call - see
            // IdentityHeaderEncoding. This is the first request a device ever
            // makes to its server, so an alias with an accent in it failed at
            // the one point where there is nothing yet to fall back on.
            request.Headers.Add("X-Flower-Fingerprint", IdentityHeaderEncoding.Encode(_deviceIdentity.Fingerprint));
            request.Headers.Add("X-Flower-Alias", IdentityHeaderEncoding.Encode(_deviceIdentity.Alias));
            request.Headers.Add("X-Flower-PublicKey", IdentityHeaderEncoding.Encode(_signingKey.PublicKeyBase64));
            request.Headers.Add("X-Flower-PairingCode", IdentityHeaderEncoding.Encode(code));
            request.Headers.Add("X-Flower-Signature", IdentityHeaderEncoding.Encode(signature));
            request.Headers.Add("X-Flower-Timestamp", IdentityHeaderEncoding.Encode(timestamp));
            request.Headers.Add("X-Flower-Nonce", IdentityHeaderEncoding.Encode(nonce));
            request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // A wrong or expired code is the ordinary case here, not an
                // exceptional one - the user mistyped, or took too long - so
                // it is logged at Information rather than as a failure.
                _logger.LogInformation(
                    "Pairing code rejected by {Alias} ({EndPoint}): {Status}",
                    device.Alias, device.BaseUri, response.StatusCode);
                return await DescribeRejectionAsync(device, response, _logger);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pair redeem to {Alias} ({EndPoint}) failed", device.Alias, device.BaseUri);
            return $"Could not reach \"{device.Alias}\" at {device.BaseUri}: {(ex.InnerException ?? ex).Message}";
        }
    }

    // The whole point of returning a sentence rather than a bool: "it did not
    // pair" on its own leaves the user with nowhere to go, and the four ways
    // this fails want four different next moves - retype the code, ask for a
    // fresh one, wait, or go check the server. The server phrases the common
    // one itself (PairingEndpoints returns {"error": ...} for a bad code), so
    // that text is preferred over anything invented here.
    private static async Task<string> DescribeRejectionAsync(
        DiscoveredDevice device, HttpResponseMessage response, ILogger logger)
    {
        var served = await ReadServerErrorAsync(response, logger);
        if (served != null)
            return served;

        return (int)response.StatusCode switch
        {
            401 => "That server would not accept this device's signature. Make sure both ends are on the same Flower version.",
            404 => $"\"{device.Alias}\" does not accept pairing codes - check the address points at a Flower server.",
            429 => "Too many attempts. Wait a minute, then try again.",
            var status => $"{device.Alias} refused the code (HTTP {status}).",
        };
    }

    private static async Task<string?> ReadServerErrorAsync(HttpResponseMessage response, ILogger logger)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
                return null;
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (Exception ex)
        {
            // A server that answered with something other than Flower's own
            // error shape has nothing useful to quote - fall back to the
            // status code. Logged because the alternative is a pairing failure
            // whose only explanation is a bare status: if this is a proxy's
            // HTML page rather than a Flower server at all, this line is what
            // says so.
            logger.LogDebug(ex, "Could not read a Flower error body from a pairing refusal; using the status code instead.");
            return null;
        }
    }
}
