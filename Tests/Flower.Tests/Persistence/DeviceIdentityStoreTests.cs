using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Persistence;

// This device's own identity on disk: its alias and fingerprint, its signing
// key, and the nicknames it gives other devices.
[Collection("PlatformDataDirectory")]
public class DeviceIdentityStoreTests : IsolatedHome
{
    [Fact]
    public void DeviceIdentityStore_Load_seeds_a_default_alias_on_first_run()
    {
        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.False(string.IsNullOrEmpty(identity.Alias));
        Assert.Equal("fp-derived", identity.Fingerprint);
    }

    // Fingerprint is now derived from the device's signing keypair (see
    // DeviceKeyStore/SignedRequestCanonicalizer.ComputeFingerprint), not an
    // independent value stored in device.json - Load() must overwrite a
    // stale/legacy fingerprint with whatever the caller says is currently
    // derived, not preserve the old one.
    [Fact]
    public void DeviceIdentityStore_Load_overwrites_a_stale_fingerprint_with_the_derived_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceIdentityStore.StorePath)!);
        File.WriteAllText(DeviceIdentityStore.StorePath, """{"Fingerprint":"fp-legacy","Alias":"Desktop"}""");

        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.Equal("fp-derived", identity.Fingerprint);
        Assert.Equal("Desktop", identity.Alias);

        var reloaded = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");
        Assert.Equal("fp-derived", reloaded.Fingerprint);
    }

    [Fact]
    public async Task DeviceIdentityStore_SaveAsync_round_trips_a_renamed_alias()
    {
        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");
        identity.Alias = "Yanos's iPhone";

        await new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).SaveAsync(identity);
        var reloaded = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.Equal("Yanos's iPhone", reloaded.Alias);
        Assert.Equal(identity.Fingerprint, reloaded.Fingerprint);
    }

    [Fact]
    public void DeviceKeyStore_Load_generates_once_and_persists_across_reloads()
    {
        var (key1, publicKeyRaw1) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load();
        var (key2, publicKeyRaw2) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load();

        Assert.Equal(Convert.ToBase64String(publicKeyRaw1), Convert.ToBase64String(publicKeyRaw2));
        Assert.Equal(
            key1.ExportParameters(false).Q.X,
            key2.ExportParameters(false).Q.X);
        key1.Dispose();
        key2.Dispose();
    }

    [Fact]
    public void DeviceKeyStore_writes_the_private_key_readable_only_by_its_owner()
    {
        // POSIX modes only - on Windows the file relies on the per-user
        // profile directory's ACL instead, and SetUnixFileMode throws.
        if (OperatingSystem.IsWindows())
            return;

        // The key sits in plaintext JSON (no OS keychain - see DeviceKeyStore's
        // own remarks), so 0600 is what keeps another local user on a shared
        // machine from lifting this device's identity outright.
        new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load().Key.Dispose();

        var mode = File.GetUnixFileMode(DeviceKeyStore.StorePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void DeviceNicknameStore_Get_returns_null_when_no_nickname_is_set()
    {
        Assert.Null(new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance).Get("fp-1"));
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_then_Get_round_trips_a_nickname()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "Yanos's iPhone");

        Assert.Equal("Yanos's iPhone", store.Get("fp-1"));
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_replaces_rather_than_duplicates_an_existing_fingerprint()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "Old Name");

        await store.SetAsync("fp-1", "New Name");

        Assert.Equal("New Name", store.Get("fp-1"));
        Assert.Single(store.Load());
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_with_an_empty_nickname_clears_the_override()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "A Name");

        await store.SetAsync("fp-1", "");

        Assert.Null(store.Get("fp-1"));
    }
}
