using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    public sealed record DeviceNickname(string Fingerprint, string Nickname);

    // A user-chosen local override for how a *peer's* name is displayed in this
    // device's own sidebar (MainViewModel.AddOrUpdateDeviceSidebarItem) and
    // Trusted Devices window - independent of DeviceIdentityStore, which is the
    // opposite direction (what this device calls itself to others). Keyed by
    // fingerprint rather than InstanceName/Alias so it survives that peer
    // renaming itself or being rediscovered under a new mDNS instance name.
    // Deliberately separate from TrustedPeerStore: a nickname can be set before
    // a device is ever trusted (or after it is later revoked), and setting one
    // is not itself a trust decision.
    public class DeviceNicknameStore
    {
        private readonly ILogger<DeviceNicknameStore> _logger;

        public DeviceNicknameStore(ILogger<DeviceNicknameStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "device-nicknames.json");

        public List<DeviceNickname> Load() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.DeviceNicknameList, _logger) ?? new List<DeviceNickname>();

        public string? Get(string fingerprint) =>
            string.IsNullOrEmpty(fingerprint)
                ? null
                : Load().FirstOrDefault(n => n.Fingerprint == fingerprint)?.Nickname;

        // An empty/whitespace nickname clears the override (falls back to
        // whatever the peer reports as its own alias) rather than persisting a
        // blank name.
        public async Task SetAsync(string fingerprint, string nickname)
        {
            if (string.IsNullOrEmpty(fingerprint))
                return;

            // Load and save are one critical section: this is a whole-file
            // read-modify-write, so two peers being renamed at once would
            // otherwise each save a list built before the other's change.
            await _writeLock.WaitAsync();
            try
            {
                var nicknames = Load().Where(n => n.Fingerprint != fingerprint).ToList();
                if (!string.IsNullOrWhiteSpace(nickname))
                    nicknames.Add(new DeviceNickname(fingerprint, nickname.Trim()));

                await AtomicJsonFile.WriteAsync(StorePath, nicknames, FlowerJsonContext.Default.DeviceNicknameList);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private readonly SemaphoreSlim _writeLock = new(1, 1);
    }
}
