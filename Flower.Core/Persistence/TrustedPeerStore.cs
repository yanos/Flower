using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // PublicKey is the cryptographic trust anchor (see SignatureVerifier), not
    // an optional annotation on the entry - an approval without one is not a
    // usable approval, so it's a plain required field.
    //
    // IsAdmin is a capability flag, never an authentication mechanism: an admin
    // peer authenticates exactly like any other peer, with the same signature
    // over the same canonical request, and this only decides whether it may
    // also reach /api/admin. That is the whole of SYNC-PLAN.md's "the browser
    // is a device" collapse - a browser tab pairs, signs and is verified like a
    // phone, and the only thing that distinguishes it is this bool. Defaults to
    // false so the flag has to be granted deliberately, by redeeming a code
    // that was itself issued as admin-granting.
    public sealed record TrustedPeer(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, string PublicKey, bool IsAdmin = false);

    public sealed record DeniedPeer(string Fingerprint, string Alias, DateTimeOffset DeniedAt);

    // Peers approved for the sync endpoints (see SyncEndpoints' gate,
    // SYNC-PLAN.md Phase 3). Approval is redeeming an admin-issued one-time code
    // (PairingEndpoints), after which that peer is
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
        // by the signature gate with no clue why, which is why this
        // goes through AtomicJsonFile's recover-from-.bak path rather than just
        // shrugging and returning empty.
        //
        // Cached in memory after the first read. This is not a cold-path
        // settings load: PeerSignatureAuth.VerifyTrustedPeer calls GetPublicKey on
        // *every* gated request, so a browsing peer turned this into a
        // synchronous File.ReadAllText plus a full deserialize on the streaming
        // hot path, dozens of times a minute. Every mutation below runs through
        // this class and invalidates the cache, so the only way to observe a
        // stale value is to edit trusted-peers.json underneath a running app -
        // which was never supported anyway. See ARCHITECTURE-REVIEW Tier 1.5.
        public List<TrustedPeer> Load()
        {
            lock (_cacheLock)
            {
                return _cachedPeers ??=
                    AtomicJsonFile.Read(StorePath, FlowerCoreJsonContext.Default.TrustedPeerList, _logger) ?? new List<TrustedPeer>();
            }
        }

        private readonly object _cacheLock = new();
        private List<TrustedPeer>? _cachedPeers;
        private List<DeniedPeer>? _cachedDenied;

        private void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedPeers = null;
                _cachedDenied = null;
            }
        }

        public bool IsTrusted(string fingerprint) =>
            Load().Any(p => p.Fingerprint == fingerprint);

        // Distinct from IsTrusted rather than folded into it: every gated route
        // needs "is this a peer at all", and only /api/admin needs "and may it
        // administer the server". Unknown fingerprint is false, same fail-closed
        // shape as GetPublicKey.
        public bool IsAdmin(string fingerprint) =>
            Load().FirstOrDefault(p => p.Fingerprint == fingerprint)?.IsAdmin ?? false;

        // Whether anyone can administer this server yet. Program.cs uses it at
        // startup to decide whether to mint and print a bootstrap pairing code:
        // no admin peer means nobody can reach /api/admin to issue one, so the
        // server has to break that circularity itself.
        public bool HasAdmin() => Load().Any(p => p.IsAdmin);

        // Null for an unknown fingerprint, which the caller fails closed on:
        // no key means the signature can never verify.
        public string? GetPublicKey(string fingerprint) =>
            Load().FirstOrDefault(p => p.Fingerprint == fingerprint)?.PublicKey;

        // Re-approving an already-trusted fingerprint (e.g. it reconnected with a
        // new alias/key) replaces its entry rather than duplicating it. Also
        // clears any pending denial for the same fingerprint, so a later
        // approval doesn't leave a stale "denied" entry sitting alongside it.
        public async Task ApproveAsync(string fingerprint, string alias, string publicKey, bool isAdmin = false)
        {
            await _writeLock.WaitAsync();
            try
            {
                var peers = Load().Where(p => p.Fingerprint != fingerprint).ToList();
                peers.Add(new TrustedPeer(fingerprint, alias, DateTimeOffset.UtcNow, publicKey, isAdmin));
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

        // Cached the same way, and for the same reason, as Load above.
        public List<DeniedPeer> LoadDenied()
        {
            lock (_cacheLock)
            {
                return _cachedDenied ??=
                    AtomicJsonFile.Read(DeniedStorePath, FlowerCoreJsonContext.Default.DeniedPeerList, _logger) ?? new List<DeniedPeer>();
            }
        }

        // Written when an app could be paired *to* and a human tapped Deny, or
        // let the prompt time out - both being "this device did not approve
        // fingerprint X," which is exactly what
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

        // Both assume _writeLock is already held by the caller, and both drop
        // the read cache so the next Load/LoadDenied reflects what was written.
        private async Task SaveAsync(List<TrustedPeer> peers)
        {
            await AtomicJsonFile.WriteAsync(StorePath, peers, FlowerCoreJsonContext.Default.TrustedPeerList);
            InvalidateCache();
        }

        private async Task SaveDeniedAsync(List<DeniedPeer> denied)
        {
            await AtomicJsonFile.WriteAsync(DeniedStorePath, denied, FlowerCoreJsonContext.Default.DeniedPeerList);
            InvalidateCache();
        }
    }
}
