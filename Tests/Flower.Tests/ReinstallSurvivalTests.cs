using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// Deleting the app and installing it again, as far as the app can tell: every
// file under its data directory gone, and the platform's secure store (the iOS
// Keychain - see ISecureStore) still holding whatever was put there. What has
// to come back is the pairing: the same device key the server trusts, the
// server it trusts it on, and that server's key to pin.
[Collection("PlatformDataDirectory")]
public class ReinstallSurvivalTests : PinnedDataDirectory
{
    private const string ServerFingerprint = "2ba46d920b629e2a1cc063980a9da712";
    private const string ServerPublicKey = "BNiAgFr27UWgPULcQF4suJBserverkey=";

    private readonly FakeSecureStore _keychain = new();

    private void Reinstall()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(DataDirectory))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    private DeviceKeyStore KeyStore() => new(NullLogger<DeviceKeyStore>.Instance, _keychain);

    private static string FingerprintOf(byte[] publicKeyRaw) => SignedRequestCanonicalizer.ComputeFingerprint(publicKeyRaw);

    private (AppSettingsStore Settings, TrustedPeerStore Peers) Stores()
    {
        var peers = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var backup = new PairingBackup(_keychain, peers, NullLogger<PairingBackup>.Instance);
        return (new AppSettingsStore(NullLogger<AppSettingsStore>.Instance, backup), peers);
    }

    private async Task PairAsync()
    {
        var (settings, peers) = Stores();
        await peers.ApproveAsync(ServerFingerprint, "Flower", ServerPublicKey, isAdmin: false);
        var paired = settings.Load();
        paired.PairedServerFingerprint = ServerFingerprint;
        paired.PairedServerAlias = "Flower";
        paired.PairedServerTrustConfirmed = true;
        paired.PairedServerAddresses = ["https://192.168.50.52:4534", "https://38.133.38.247:4534"];
        paired.ManualServerAddresses = ["music.example.com:4534"];
        await settings.SaveAsync(paired);
    }

    [Fact]
    public void The_device_key_survives_a_reinstall()
    {
        var (_, before) = KeyStore().Load();

        Reinstall();
        var (_, after) = KeyStore().Load();

        Assert.Equal(FingerprintOf(before), FingerprintOf(after));
        Assert.True(File.Exists(DeviceKeyStore.StorePath)); // and the file is back
    }

    // Every install that predates the secure store has its key only on disk.
    // Its first launch with it copies the key in, so the reinstall after that
    // one keeps the identity the server already trusts.
    [Fact]
    public void A_key_that_was_only_on_disk_is_copied_in_on_first_load()
    {
        var (_, original) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance, null).Load();

        KeyStore().Load();
        Reinstall();
        var (_, after) = KeyStore().Load();

        Assert.Equal(FingerprintOf(original), FingerprintOf(after));
    }

    [Fact]
    public void An_unreadable_secure_store_entry_falls_back_to_the_file()
    {
        var (_, onDisk) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance, null).Load();
        _keychain.Values[DeviceKeyStore.SecureStoreName] = "{ not a key";

        var (_, loaded) = KeyStore().Load();

        Assert.Equal(FingerprintOf(onDisk), FingerprintOf(loaded));
    }

    [Fact]
    public async Task The_pairing_survives_a_reinstall()
    {
        await PairAsync();

        Reinstall();
        var (store, peers) = Stores();
        var restored = store.Load();

        Assert.Equal(ServerFingerprint, restored.PairedServerFingerprint);
        Assert.Equal("Flower", restored.PairedServerAlias);
        Assert.True(restored.PairedServerTrustConfirmed);
        Assert.Equal(["https://192.168.50.52:4534", "https://38.133.38.247:4534"], restored.PairedServerAddresses);
        Assert.Equal(["music.example.com:4534"], restored.ManualServerAddresses);

        // The pin every TLS connection to the server is checked against.
        Assert.Equal(ServerPublicKey, peers.GetPublicKey(ServerFingerprint));
    }

    // Unpairing on purpose is a settings save with no server in it, and that
    // has to delete the backup - otherwise the next reinstall would quietly
    // re-pair a device its owner had unpaired.
    [Fact]
    public async Task Unpairing_deletes_the_backup()
    {
        await PairAsync();
        var (store, _) = Stores();
        var settings = store.Load();
        settings.PairedServerFingerprint = null;
        settings.PairedServerAlias = null;
        settings.PairedServerAddresses = [];
        await store.SaveAsync(settings);

        Reinstall();
        var restored = Stores().Settings.Load();

        Assert.Null(restored.PairedServerFingerprint);
        Assert.False(_keychain.Values.ContainsKey(PairingBackup.SecureStoreName));
    }

    // Restoring is for a fresh install only. A settings file that exists and
    // says "not paired" is the owner's own answer, not a gap to fill in.
    [Fact]
    public async Task An_existing_unpaired_install_is_not_re_paired_behind_its_back()
    {
        await PairAsync();
        var paired = _keychain.Values[PairingBackup.SecureStoreName];

        // settings.json written without the store, as an older build or a
        // hand edit would: unpaired, with the backup left in place.
        File.WriteAllText(AppSettingsStore.StorePath, "{}");
        _keychain.Values[PairingBackup.SecureStoreName] = paired;

        var loaded = Stores().Settings.Load();

        Assert.Null(loaded.PairedServerFingerprint);
    }

    // Settings are saved on a column resize or a volume change. None of those
    // should touch the Keychain.
    [Fact]
    public async Task A_save_that_changes_nothing_about_the_pairing_does_not_rewrite_it()
    {
        await PairAsync();
        var (store, _) = Stores();
        var settings = store.Load();
        var writes = _keychain.Writes;

        settings.LastScrollOffsetY = 1234;
        await store.SaveAsync(settings);
        settings.LastScrollOffsetY = 99;
        await store.SaveAsync(settings);

        Assert.Equal(writes, _keychain.Writes);
    }

    // An install paired before the secure store existed has nothing in it.
    // Its first launch with it has to back the pairing up straight away: the
    // reinstall it exists for could come before any settings save does.
    [Fact]
    public async Task An_install_paired_before_the_secure_store_is_backed_up_at_launch()
    {
        await PairAsync();
        _keychain.Values.Clear();

        Stores().Settings.Load();

        Assert.True(_keychain.Values.ContainsKey(PairingBackup.SecureStoreName));
    }

    [Fact]
    public async Task Nothing_is_restored_without_a_secure_store()
    {
        var peers = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance,
            new PairingBackup(null, peers, NullLogger<PairingBackup>.Instance));
        var settings = store.Load();
        settings.PairedServerFingerprint = ServerFingerprint;
        await store.SaveAsync(settings);

        Reinstall();

        Assert.Null(store.Load().PairedServerFingerprint);
    }

    private sealed class FakeSecureStore : ISecureStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public int Writes { get; private set; }

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string value)
        {
            Values[name] = value;
            Writes++;
        }

        public void Delete(string name) => Values.Remove(name);
    }
}
