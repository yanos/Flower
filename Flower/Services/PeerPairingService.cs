using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// The client side of SyncHttpServer's pair-request endpoint - the one and only
// call that can raise a peer's PeerApprovalRequested prompt (see that file's
// top-of-file doc comment). Called from MainViewModel.PairWithServer, itself
// only reachable via an explicit "Ask to pair" user action - never off the back
// of an incidental request like AlbumArtLoader's remote art fetch.
public class PeerPairingService
{
    // Long enough to outlast SyncHttpServer.ApprovalTimeout (60s) - unlike every
    // other HttpClient in this codebase's sync services, this one is meant to
    // sit waiting on an actual human tapping Allow/Deny on the other device, not
    // for a near-instant reply.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(75) };

    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _signingKey;
    private readonly ILogger<PeerPairingService> _logger;

    public PeerPairingService(DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, ILogger<PeerPairingService> logger)
    {
        _deviceIdentity = deviceIdentity;
        _signingKey = signingKey;
        _logger = logger;
    }

    // The headless-server half of pairing: no live approval prompt, because
    // there is nobody in front of a server to tap Allow. Instead an admin
    // issues a one-time code out of band (a QR, or read out over the phone)
    // and this redeems it - see Flower.Server's PairingEndpoints and
    // SYNC-PLAN.md's "Passwordless by design", path A. Only reachable for a
    // peer whose handshake said deviceType=server (DiscoveredDevice.
    // PairsByCode); an app peer has no code to give.
    //
    // Same self-signed request shape as RequestPairingAsync - the server has
    // never seen this key before, so the signature proves only that the
    // sender holds the key it is presenting, and the code is what supplies
    // the authorization.
    public async Task<string?> RedeemPairingCodeAsync(DiscoveredDevice device, string code)
    {
        try
        {
            const string path = "/api/flower/v1/pair-redeem";
            var (signature, timestamp, nonce) = _signingKey.Sign("POST", path, [], body: []);

            using var request = new HttpRequestMessage(HttpMethod.Post, device.Url(path));
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            request.Headers.Add("X-Flower-PublicKey", _signingKey.PublicKeyBase64);
            request.Headers.Add("X-Flower-PairingCode", code);
            request.Headers.Add("X-Flower-Signature", signature);
            request.Headers.Add("X-Flower-Timestamp", timestamp);
            request.Headers.Add("X-Flower-Nonce", nonce);
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
                return await DescribeRejectionAsync(device, response);
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
    private static async Task<string> DescribeRejectionAsync(DiscoveredDevice device, HttpResponseMessage response)
    {
        var served = await ReadServerErrorAsync(response);
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

    private static async Task<string?> ReadServerErrorAsync(HttpResponseMessage response)
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
        catch (Exception)
        {
            // A server that answered with something other than Flower's own
            // error shape has nothing useful to quote - fall back to the
            // status code.
            return null;
        }
    }

    public async Task<bool> RequestPairingAsync(DiscoveredDevice device)
    {
        try
        {
            const string path = "/api/flower/v1/pair-request";
            var (signature, timestamp, nonce) = _signingKey.Sign("POST", path, [], body: []);

            using var request = new HttpRequestMessage(HttpMethod.Post, device.Url(path));
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            request.Headers.Add("X-Flower-PublicKey", _signingKey.PublicKeyBase64);
            request.Headers.Add("X-Flower-Signature", signature);
            request.Headers.Add("X-Flower-Timestamp", timestamp);
            request.Headers.Add("X-Flower-Nonce", nonce);
            request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pair request to {Alias} ({EndPoint}) failed", device.Alias, device.BaseUri);
            return false;
        }
    }
}
