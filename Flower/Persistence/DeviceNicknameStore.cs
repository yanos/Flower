using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public List<DeviceNickname> Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<DeviceNickname>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<DeviceNickname>>(json, Options) ?? new List<DeviceNickname>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load device nicknames from {Path}; starting with none set", path);
                return new List<DeviceNickname>();
            }
        }

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

            var nicknames = Load().Where(n => n.Fingerprint != fingerprint).ToList();
            if (!string.IsNullOrWhiteSpace(nickname))
                nicknames.Add(new DeviceNickname(fingerprint, nickname.Trim()));

            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(nicknames, Options));
        }
    }
}
