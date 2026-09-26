using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // What this device knows about the server it is paired with, as it is kept
    // in the secure store. The settings fields that say which server and where,
    // plus the server's public key, which the client pins every TLS connection
    // against (PeerHttpClient.IsPinnedServerKey) and so cannot do without.
    public sealed record PairingBackupRecord(
        string ServerFingerprint,
        string? ServerAlias,
        string? ServerPublicKey,
        bool ServerIsAdmin,
        bool TrustConfirmed,
        List<string> Addresses,
        List<string> ManualAddresses);

    // Keeps the pairing where a reinstall cannot reach it, and puts it back.
    //
    // A pairing is three files - the device key (DeviceKeyStore), the server
    // pointer and its addresses (settings.json), and the server's pinned key
    // (trusted-peers.json) - and deleting the app deleted all three. The key
    // now survives in the secure store on its own; this is the other two. With
    // both, a phone whose app was deleted and installed again launches already
    // paired: the same key the server trusts, the server it trusted it on, and
    // the addresses to find it at from outside the house, so the library syncs
    // back without anyone typing anything.
    //
    // Mirrored on every settings save, since every change to the pairing ends
    // in one, and restored only on a fresh install - no settings.json at all -
    // so an explicit Unpair, which saves settings with no server, deletes the
    // backup rather than being undone by it on the next launch.
    public class PairingBackup
    {
        public const string SecureStoreName = "pairing";

        private readonly ISecureStore? _store;
        private readonly TrustedPeerStore _trustedPeers;
        private readonly ILogger<PairingBackup> _logger;
        private readonly object _gate = new();

        // What was last written, so the many settings saves that change
        // nothing about the pairing - a column resize, a volume change - do not
        // each rewrite a Keychain item. Null until the first mirror, which
        // therefore always writes (or deletes) once per process.
        private string? _lastWritten;
        private bool _mirroredOnce;

        public PairingBackup(ISecureStore? store, TrustedPeerStore trustedPeers, ILogger<PairingBackup> logger)
        {
            _store = store;
            _trustedPeers = trustedPeers;
            _logger = logger;
        }

        public void Mirror(AppSettings settings)
        {
            if (_store is null)
                return;

            var json = Describe(settings) is { } record
                ? JsonSerializer.Serialize(record, FlowerJsonContext.Default.PairingBackupRecord)
                : null;

            lock (_gate)
            {
                if (_mirroredOnce && json == _lastWritten)
                    return;

                try
                {
                    if (json is null)
                        _store.Delete(SecureStoreName);
                    else
                        _store.Write(SecureStoreName, json);

                    _lastWritten = json;
                    _mirroredOnce = true;
                }
                catch (Exception ex)
                {
                    // Best effort, like DeviceKeyStore's: the pairing is still
                    // in settings.json, it just will not survive a reinstall.
                    _logger.LogWarning(ex, "Could not keep the pairing in the secure store; it will not survive the app being reinstalled");
                }
            }
        }

        // Fills a fresh install's settings back in from the secure store, and
        // re-trusts the server's key. False when there was nothing to restore.
        public bool RestoreInto(AppSettings settings)
        {
            if (_store is null)
                return false;

            PairingBackupRecord? record;
            try
            {
                record = _store.Read(SecureStoreName) is { Length: > 0 } json
                    ? JsonSerializer.Deserialize(json, FlowerJsonContext.Default.PairingBackupRecord)
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The pairing kept in the secure store could not be read; starting unpaired");
                return false;
            }

            if (record is not { ServerFingerprint.Length: > 0 })
                return false;

            settings.PairedServerFingerprint = record.ServerFingerprint;
            settings.PairedServerAlias = record.ServerAlias;
            settings.PairedServerTrustConfirmed = record.TrustConfirmed;
            settings.PairedServerAddresses = record.Addresses.ToList();
            settings.ManualServerAddresses = record.ManualAddresses.ToList();

            // Off the calling thread: this runs while the app is still being
            // assembled, possibly on a UI thread whose dispatcher an awaited
            // continuation would wait for.
            if (record.ServerPublicKey is { Length: > 0 } publicKey)
            {
                Task.Run(() => _trustedPeers.ApproveAsync(
                    record.ServerFingerprint, record.ServerAlias ?? "", publicKey, record.ServerIsAdmin)).GetAwaiter().GetResult();
            }

            _logger.LogInformation(
                "Restored the pairing with {Alias} ({Fingerprint}) from the secure store - this app was reinstalled",
                record.ServerAlias, record.ServerFingerprint);
            return true;
        }

        private PairingBackupRecord? Describe(AppSettings settings)
        {
            if (settings.PairedServerFingerprint is not { Length: > 0 } fingerprint)
                return null;

            var server = _trustedPeers.Load().FirstOrDefault(p => p.Fingerprint == fingerprint);
            return new PairingBackupRecord(
                fingerprint,
                settings.PairedServerAlias,
                server?.PublicKey,
                server?.IsAdmin ?? false,
                settings.PairedServerTrustConfirmed,
                settings.PairedServerAddresses.ToList(),
                settings.ManualServerAddresses.ToList());
        }
    }
}
