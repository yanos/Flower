using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // One third-party Subsonic client's credential (SYNC-PLAN.md, "Passwordless
    // by design" path B).
    //
    // Password is stored in the clear, which for once is a considered decision
    // rather than an oversight: the classic Subsonic scheme is
    // t=md5(password+salt) with a *client*-chosen salt, so the server has to
    // recompute that hash on every request and therefore must hold the original
    // password. Hashing at rest is simply not compatible with the protocol.
    // What makes that acceptable is that these are not user-chosen secrets and
    // never shared with anything else: each one is 32 random characters this
    // server generated, scoped to this server, individually revocable, and
    // reused nowhere. The file sits in the same data directory as
    // trusted-peers.json under the same filesystem permissions.
    public sealed record SubsonicCredential(
        string Username,
        string Password,
        string Label,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastSeenAt = null);

    // The registry behind path B. Third-party clients (DSub, substreamer,
    // Symfonium) implement a published protocol and will send u/t/s or an API
    // key, so they cannot join path A's keypair scheme - but they do not need a
    // second *subsystem*, only a second credential type. Same admin action
    // issues both, same list shows both, same revoke button retires both; only
    // the redemption differs (post a public key and the code, versus use the
    // code as the password).
    public class SubsonicCredentialStore
    {
        private readonly ILogger<SubsonicCredentialStore> _logger;

        public SubsonicCredentialStore(ILogger<SubsonicCredentialStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "subsonic-credentials.json");

        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _cacheLock = new();
        private List<SubsonicCredential>? _cached;

        // Cached for the same reason TrustedPeerStore caches: this is consulted
        // on every single /rest request, including the getCoverArt burst an
        // album grid produces, so an uncached read would be a synchronous file
        // read and full deserialize on the streaming hot path.
        public List<SubsonicCredential> Load()
        {
            lock (_cacheLock)
            {
                return _cached ??=
                    AtomicJsonFile.Read(StorePath, FlowerCoreJsonContext.Default.SubsonicCredentialList, _logger)
                    ?? new List<SubsonicCredential>();
            }
        }

        public SubsonicCredential? Find(string? username)
        {
            if (string.IsNullOrEmpty(username))
                return null;
            return Load().FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.Ordinal));
        }

        // Username is derived from the label so the credential is recognizable
        // in a client's own settings screen ("flower-phone" rather than an
        // opaque id), with a random suffix so two clients labelled the same way
        // don't collide.
        public async Task<SubsonicCredential> IssueAsync(string label)
        {
            var credential = new SubsonicCredential(
                Username: BuildUsername(label),
                Password: GenerateSecret(),
                Label: string.IsNullOrWhiteSpace(label) ? "Subsonic client" : label.Trim(),
                CreatedAt: DateTimeOffset.UtcNow);

            await _writeLock.WaitAsync();
            try
            {
                var credentials = Load().ToList();
                credentials.Add(credential);
                await SaveAsync(credentials);
            }
            finally
            {
                _writeLock.Release();
            }

            return credential;
        }

        public async Task<bool> RevokeAsync(string username)
        {
            await _writeLock.WaitAsync();
            try
            {
                var credentials = Load();
                var remaining = credentials.Where(c => c.Username != username).ToList();
                if (remaining.Count == credentials.Count)
                    return false;
                await SaveAsync(remaining);
                return true;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // Best-effort and deliberately lossy: last-seen is a convenience for the
        // admin's device list ("which of these is still in use?"), not an audit
        // log, so it is written at most once a minute per credential rather than
        // turning every authenticated /rest request into a file write.
        public async Task TouchAsync(string username, DateTimeOffset now)
        {
            var existing = Find(username);
            if (existing == null)
                return;
            if (existing.LastSeenAt is { } seen && now - seen < TimeSpan.FromMinutes(1))
                return;

            await _writeLock.WaitAsync();
            try
            {
                var credentials = Load().ToList();
                var index = credentials.FindIndex(c => c.Username == username);
                if (index < 0)
                    return;
                credentials[index] = credentials[index] with { LastSeenAt = now };
                await SaveAsync(credentials);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task SaveAsync(List<SubsonicCredential> credentials)
        {
            await AtomicJsonFile.WriteAsync(StorePath, credentials, FlowerCoreJsonContext.Default.SubsonicCredentialList);
            lock (_cacheLock)
            {
                _cached = null;
            }
        }

        private static string BuildUsername(string label)
        {
            var slug = new string((label ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray())
                .Trim('-');
            if (slug.Length > 24)
                slug = slug[..24];
            if (string.IsNullOrEmpty(slug))
                slug = "client";

            return slug + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2));
        }

        // 32 characters from an unambiguous alphabet: this is copied by hand or
        // scanned from a QR into a client's password box often enough that
        // 0/O/1/I confusion is a real support cost, and dropping them still
        // leaves ~5 bits per character - 160 bits total, far past anything the
        // /rest rate limiters need to defend.
        private const string SecretAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

        private static string GenerateSecret() =>
            RandomNumberGenerator.GetString(SecretAlphabet, 32);
    }
}
