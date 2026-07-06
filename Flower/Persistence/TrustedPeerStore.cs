using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Persistence
{
    public sealed record TrustedPeer(string Fingerprint, string Alias, DateTimeOffset ApprovedAt);

    // Peers this device has approved for the OpenSubsonic-shaped sync endpoints
    // (see SyncHttpServer's trust gate, SYNC-PLAN.md Phase 3). Approval is a
    // one-time "Allow" prompt per unrecognized fingerprint - same interaction
    // shape as Bluetooth pairing/AirDrop's "Accept" - after which that peer is
    // never prompted again. Revoking is the manual "forget this device" action
    // in TrustedDevicesWindow. Deliberately does not persist denials: a
    // denied/ignored peer is simply prompted again on its next request rather
    // than being remembered as blocked.
    public class TrustedPeerStore
    {
        public static string StorePath => Path.Combine(AppDataDirectory.Path, "trusted-peers.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public List<TrustedPeer> Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<TrustedPeer>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<TrustedPeer>>(json, Options) ?? new List<TrustedPeer>();
            }
            catch
            {
                return new List<TrustedPeer>();
            }
        }

        public bool IsTrusted(string fingerprint) =>
            Load().Any(p => p.Fingerprint == fingerprint);

        // Re-approving an already-trusted fingerprint (e.g. it reconnected with a
        // new alias) replaces its entry rather than duplicating it.
        public async Task ApproveAsync(string fingerprint, string alias)
        {
            var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
            peers.Add(new TrustedPeer(fingerprint, alias, DateTimeOffset.UtcNow));
            await SaveAsync(peers);
        }

        public async Task RevokeAsync(string fingerprint)
        {
            var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
            await SaveAsync(peers);
        }

        private static async Task SaveAsync(List<TrustedPeer> peers)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(peers, Options));
        }
    }
}
