using System;

using Foundation;
using Security;

using Flower.Persistence;

namespace Flower.iOS;

// PlatformSecureStore on iOS: generic-password items in the app's own Keychain,
// one per name. Chosen for the one property nothing under the app's container
// has - an item outlives the app being deleted - so a reinstalled Flower finds
// its device key and its pairing where it left them (see ISecureStore).
//
// AfterFirstUnlockThisDeviceOnly, on both counts deliberately. After first
// unlock, because the key signs every request, including the ones background
// playback makes with the screen locked. This device only, because the item
// then never travels in an iCloud or iTunes backup: restoring one onto a new
// phone while the old one is still in use would give two phones one identity,
// which the server has no way to tell apart. A new phone pairs as itself.
public sealed class KeychainSecureStore : ISecureStore
{
    // Scopes the items to Flower, apart from anything else in the access group.
    private const string Service = "com.yanos.flower.secure-store";

    public string? Read(string name)
    {
        var data = SecKeyChain.QueryAsData(Query(name), false, out var status);
        if (status == SecStatusCode.ItemNotFound)
            return null;

        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain read of '{name}' failed: {status}");

        return data?.ToString(NSStringEncoding.UTF8);
    }

    // Remove-then-add rather than an update: it is the one sequence that
    // behaves the same whether or not the item exists, and it also replaces
    // the accessibility class if an older build wrote a different one.
    public void Write(string name, string value)
    {
        Delete(name);

        var record = Query(name);
        record.ValueData = NSData.FromString(value, NSStringEncoding.UTF8);
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;

        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
            throw new InvalidOperationException($"Keychain write of '{name}' failed: {status}");
    }

    public void Delete(string name)
    {
        var status = SecKeyChain.Remove(Query(name));
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new InvalidOperationException($"Keychain delete of '{name}' failed: {status}");
    }

    private static SecRecord Query(string name) =>
        new(SecKind.GenericPassword) { Service = Service, Account = name };
}
