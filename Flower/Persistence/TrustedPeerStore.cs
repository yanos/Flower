using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // PublicKey defaults to "" (not a required constructor param) specifically
    // so a pre-signing-scheme trusted-peers.json (written before this field
    // existed) deserializes without throwing - see GetPublicKey's own doc
    // comment for what that means for an old entry.
    public sealed record TrustedPeer(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, string PublicKey = "");

    public sealed record DeniedPeer(string Fingerprint, string Alias, DateTimeOffset DeniedAt);

    // Peers this device has approved for the OpenSubsonic-shaped sync endpoints
    // (see SyncHttpServer's trust gate, SYNC-PLAN.md Phase 3). Approval is a
    // one-time "Allow" prompt per unrecognized fingerprint - same interaction
    // shape as Bluetooth pairing/AirDrop's "Accept" - after which that peer is
    // never prompted again. Revoking is the manual "forget this device" action
    // in TrustedDevicesView. Denials are persisted separately (DeniedPeer/
    // denied-peers.json) so a device can see who it turned away and forget
    // that refusal explicitly, rather than the peer just being silently
    // re-prompted on its next request.
    public class TrustedPeerStore
    {
        private readonly ILogger<TrustedPeerStore> _logger;

        public TrustedPeerStore(ILogger<TrustedPeerStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "trusted-peers.json");
        public static string DeniedStorePath => Path.Combine(AppDataDirectory.Path, "denied-peers.json");

        public List<TrustedPeer> Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<TrustedPeer>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, FlowerJsonContext.Default.TrustedPeerList) ?? new List<TrustedPeer>();
            }
            catch (Exception ex)
            {
                // A corrupt/unreadable trusted-peers.json silently means "0
                // trusted peers" - every previously-approved device would start
                // getting denied by SyncHttpServer.AuthorizeAsync with no clue
                // why, exactly the kind of thing worth a warning for.
                _logger.LogWarning(ex, "Failed to load trusted peers from {Path}; starting with none trusted", path);
                return new List<TrustedPeer>();
            }
        }

        public bool IsTrusted(string fingerprint) =>
            Load().Any(p => p.Fingerprint == fingerprint);

        // The actual cryptographic trust anchor (see SignatureVerifier) - null
        // both for an unknown fingerprint and for a pre-signing-scheme entry
        // with no key on file (PublicKey == ""), which the caller treats
        // identically: no usable key means the signature can never verify, so
        // that peer fails closed the same as an outright stranger until it
        // re-pairs and a real key gets captured.
        public string? GetPublicKey(string fingerprint) =>
            Load().FirstOrDefault(p => p.Fingerprint == fingerprint) is { PublicKey.Length: > 0 } peer
                ? peer.PublicKey
                : null;

        // Re-approving an already-trusted fingerprint (e.g. it reconnected with a
        // new alias/key) replaces its entry rather than duplicating it. Also
        // clears any pending denial for the same fingerprint, so a later
        // approval doesn't leave a stale "denied" entry sitting alongside it.
        public async Task ApproveAsync(string fingerprint, string alias, string publicKey)
        {
            var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
            peers.Add(new TrustedPeer(fingerprint, alias, DateTimeOffset.UtcNow, publicKey));
            await SaveAsync(peers);

            var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
            await SaveDeniedAsync(denied);
        }

        public async Task RevokeAsync(string fingerprint)
        {
            var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
            await SaveAsync(peers);
        }

        public List<DeniedPeer> LoadDenied()
        {
            var path = DeniedStorePath;
            if (!File.Exists(path))
                return new List<DeniedPeer>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, FlowerJsonContext.Default.DeniedPeerList) ?? new List<DeniedPeer>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load denied peers from {Path}; starting with none denied", path);
                return new List<DeniedPeer>();
            }
        }

        // Called for both an explicit Deny tap and an unanswered/timed-out
        // pairing prompt (see SyncHttpServer.RequestApprovalAsync) - both are
        // "this device did not approve fingerprint X," which is exactly what
        // the denied-devices list is for. Replaces rather than duplicates a
        // repeat denial of the same fingerprint.
        public async Task DenyAsync(string fingerprint, string alias)
        {
            var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
            denied.Add(new DeniedPeer(fingerprint, alias, DateTimeOffset.UtcNow));
            await SaveDeniedAsync(denied);
        }

        public async Task ForgetDenialAsync(string fingerprint)
        {
            var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
            await SaveDeniedAsync(denied);
        }

        private static async Task SaveAsync(List<TrustedPeer> peers)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(peers, FlowerJsonContext.Default.TrustedPeerList));
        }

        private static async Task SaveDeniedAsync(List<DeniedPeer> denied)
        {
            var path = DeniedStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(denied, FlowerJsonContext.Default.DeniedPeerList));
        }
    }
}
