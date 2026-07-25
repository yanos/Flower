using System;
using System.Net.Http;
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
    private readonly ILogger<PeerPairingService> _logger;

    public PeerPairingService(DeviceIdentity deviceIdentity, ILogger<PeerPairingService> logger)
    {
        _deviceIdentity = deviceIdentity;
        _logger = logger;
    }

    public async Task<bool> RequestPairingAsync(DiscoveredDevice device)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{device.EndPoint}/api/flower/v1/pair-request");
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pair request to {Alias} ({EndPoint}) failed", device.Alias, device.EndPoint);
            return false;
        }
    }
}
