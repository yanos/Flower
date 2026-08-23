using System;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// Best-effort sender for SyncHttpServer's POST /api/flower/v1/unpair-notify -
// the server-initiated half of a revoke (see TrustedDevicesView's "Forget"
// button), so a peer can proactively clear its own stale pairing instead of
// only discovering it passively via a later 403 or /info poll. Deliberately
// fire-and-forget: revocation itself (TrustedPeerStore.RevokeAsync) must
// complete instantly regardless of network state, and the peer may simply be
// offline right now - that's fine, it falls back to the existing passive
// discovery path in that case.
public class PeerUnpairNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly NetworkDiscoveryService _discovery;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _signingKey;
    private readonly ILogger<PeerUnpairNotifier> _logger;

    public PeerUnpairNotifier(NetworkDiscoveryService discovery, DeviceIdentity deviceIdentity, DeviceSigningKey signingKey, ILogger<PeerUnpairNotifier> logger)
    {
        _discovery = discovery;
        _deviceIdentity = deviceIdentity;
        _signingKey = signingKey;
        _logger = logger;
    }

    public void NotifyFireAndForget(string fingerprint)
    {
        var peer = _discovery.FindByFingerprint(fingerprint);
        if (peer == null)
            return; // Offline right now - nothing to notify; the peer learns of the revoke passively instead.

        _ = Task.Run(async () =>
        {
            try
            {
                const string path = "/api/flower/v1/unpair-notify";
                var (signature, timestamp, nonce) = _signingKey.Sign("POST", path, [], body: []);

                using var request = new HttpRequestMessage(HttpMethod.Post, peer.Url(path));
                request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
                request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
                request.Headers.Add("X-Flower-PublicKey", _signingKey.PublicKeyBase64);
                request.Headers.Add("X-Flower-Signature", signature);
                request.Headers.Add("X-Flower-Timestamp", timestamp);
                request.Headers.Add("X-Flower-Nonce", nonce);
                request.Headers.ConnectionClose = true;

                using var response = await Http.SendAsync(request);
                _logger.LogDebug("Unpair notification to {Alias} ({Fingerprint}) returned {StatusCode}", peer.Alias, fingerprint, response.StatusCode);
            }
            catch (Exception ex)
            {
                // Never fatal - the local revoke already completed.
                _logger.LogDebug(ex, "Unpair notification to {Fingerprint} failed - not fatal, the peer will discover the revoke passively", fingerprint);
            }
        });
    }
}
