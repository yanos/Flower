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

// The roster of paired devices: approvals, keys, revocations, denials, and the
// last-seen stamp that is only written once per resolution window.
[Collection("PlatformDataDirectory")]
public class TrustedPeerStoreTests : IsolatedHome
{
    [Fact]
    public async Task TrustedPeerStore_Approve_then_IsTrusted_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.False(store.IsTrusted("fp-1"));

        await store.ApproveAsync("fp-1", "Yanos's iPhone", "pubkey-1");

        Assert.True(store.IsTrusted("fp-1"));
        var peer = Assert.Single(store.Load());
        Assert.Equal("fp-1", peer.Fingerprint);
        Assert.Equal("Yanos's iPhone", peer.Alias);
        Assert.Equal("pubkey-1", peer.PublicKey);
    }

    [Fact]
    public async Task TrustedPeerStore_GetPublicKey_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.Null(store.GetPublicKey("fp-1"));

        await store.ApproveAsync("fp-1", "Desktop", "pubkey-1");

        Assert.Equal("pubkey-1", store.GetPublicKey("fp-1"));
    }

    [Fact]
    public async Task TrustedPeerStore_Revoke_removes_a_previously_approved_peer()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "Desktop", "pubkey-1");
        await store.ApproveAsync("fp-2", "iPad", "pubkey-2");

        await store.RevokeAsync("fp-1");

        Assert.False(store.IsTrusted("fp-1"));
        Assert.True(store.IsTrusted("fp-2"));
    }

    // Pairing is the first time a device is seen, so a fresh approval is never
    // "not seen yet".
    [Fact]
    public async Task TrustedPeerStore_Approve_counts_as_being_seen()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var before = DateTimeOffset.UtcNow;

        await store.ApproveAsync("fp-1", "iPhone", "pubkey-1");

        Assert.True(Assert.Single(store.Load()).LastSeenAt >= before);
    }

    // Recorded, persisted, and not rewritten on every request inside the
    // resolution window - a playing phone signs a request every few seconds.
    [Fact]
    public async Task TrustedPeerStore_RecordSeen_persists_only_once_per_resolution_window()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "iPhone", "pubkey-1");
        var approvedSeen = store.Load().Single().LastSeenAt!.Value;

        var soon = approvedSeen + TrustedPeerStore.LastSeenResolution / 2;
        await store.RecordSeenAsync("fp-1", soon);
        Assert.Equal(approvedSeen, store.Load().Single().LastSeenAt);

        var later = approvedSeen + TrustedPeerStore.LastSeenResolution;
        await store.RecordSeenAsync("fp-1", later);
        Assert.Equal(later, new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance).Load().Single().LastSeenAt);
    }

    // A request verified just before the device was forgotten must not bring
    // it back by writing its last-seen time over a list that still had it.
    [Fact]
    public async Task TrustedPeerStore_RecordSeen_does_not_revive_a_revoked_peer()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "iPhone", "pubkey-1");
        await store.RevokeAsync("fp-1");

        await store.RecordSeenAsync("fp-1", DateTimeOffset.UtcNow + TrustedPeerStore.LastSeenResolution);

        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task TrustedPeerStore_Approve_replaces_rather_than_duplicates_an_existing_fingerprint()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "Old Alias", "pubkey-1");

        await store.ApproveAsync("fp-1", "New Alias", "pubkey-1-rotated");

        var peer = Assert.Single(store.Load());
        Assert.Equal("New Alias", peer.Alias);
        Assert.Equal("pubkey-1-rotated", peer.PublicKey);
    }

    [Fact]
    public void TrustedPeerStore_IsTrusted_is_false_when_no_file_exists()
    {
        Assert.False(new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance).IsTrusted("anything"));
    }

    [Fact]
    public async Task TrustedPeerStore_Deny_then_LoadDenied_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.Empty(store.LoadDenied());

        await store.DenyAsync("fp-1", "Suspicious Device");

        var denied = Assert.Single(store.LoadDenied());
        Assert.Equal("fp-1", denied.Fingerprint);
        Assert.Equal("Suspicious Device", denied.Alias);
    }

    [Fact]
    public async Task TrustedPeerStore_ForgetDenial_removes_the_entry()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.DenyAsync("fp-1", "Denied Device");

        await store.ForgetDenialAsync("fp-1");

        Assert.Empty(store.LoadDenied());
    }

    [Fact]
    public async Task TrustedPeerStore_Approve_clears_a_matching_denial()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.DenyAsync("fp-1", "Reconsidered Device");

        await store.ApproveAsync("fp-1", "Reconsidered Device", "pubkey-1");

        Assert.Empty(store.LoadDenied());
        Assert.True(store.IsTrusted("fp-1"));
    }
}
