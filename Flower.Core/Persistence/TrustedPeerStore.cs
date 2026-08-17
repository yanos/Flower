using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        // Every mutation below is a load-modify-save over the whole file, so the
        // load has to be inside the same critical section as the save - two
        // approvals landing at once would otherwise each write a list built
        // before the other's entry existed, silently dropping one.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // A corrupt/unreadable trusted-peers.json silently means "0 trusted
        // peers" - every previously-approved device would start getting denied
        // by SyncHttpServer.AuthorizeAsync with no clue why, which is why this
        // goes through AtomicJsonFile's recover-from-.bak path rather than just
        // shrugging and returning empty.
        public List<TrustedPeer> Load() =>
            AtomicJsonFile.Read(StorePath, FlowerCoreJsonContext.Default.TrustedPeerList, _logger) ?? new List<TrustedPeer>();

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
            await _writeLock.WaitAsync();
            try
            {
                var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
                peers.Add(new TrustedPeer(fingerprint, alias, DateTimeOffset.UtcNow, publicKey));
                await SaveAsync(peers);

                var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
                await SaveDeniedAsync(denied);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task RevokeAsync(string fingerprint)
        {
            await _writeLock.WaitAsync();
            try
            {
                var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
                await SaveAsync(peers);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public List<DeniedPeer> LoadDenied() =>
            AtomicJsonFile.Read(DeniedStorePath, FlowerCoreJsonContext.Default.DeniedPeerList, _logger) ?? new List<DeniedPeer>();

        // Called for both an explicit Deny tap and an unanswered/timed-out
        // pairing prompt (see SyncHttpServer.RequestApprovalAsync) - both are
        // "this device did not approve fingerprint X," which is exactly what
        // the denied-devices list is for. Replaces rather than duplicates a
        // repeat denial of the same fingerprint.
        public async Task DenyAsync(string fingerprint, string alias)
        {
            await _writeLock.WaitAsync();
            try
            {
                var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
                denied.Add(new DeniedPeer(fingerprint, alias, DateTimeOffset.UtcNow));
                await SaveDeniedAsync(denied);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task ForgetDenialAsync(string fingerprint)
        {
            await _writeLock.WaitAsync();
            try
            {
                var denied = LoadDenied().Where(p => p.Fingerprint != fingerprint).ToList();
                await SaveDeniedAsync(denied);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // Both assume _writeLock is already held by the caller.
        private static Task SaveAsync(List<TrustedPeer> peers) =>
            AtomicJsonFile.WriteAsync(StorePath, peers, FlowerCoreJsonContext.Default.TrustedPeerList);

        private static Task SaveDeniedAsync(List<DeniedPeer> denied) =>
            AtomicJsonFile.WriteAsync(DeniedStorePath, denied, FlowerCoreJsonContext.Default.DeniedPeerList);
    }
}
