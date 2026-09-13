using System;
using System.Collections.Generic;
using System.IO;

using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

[Collection("PlatformDataDirectory")]
public class ClientLogStoreTests : PinnedDataDirectory
{
    private string StorePath => Path.Combine(DataDirectory, "client-logs");

    private ClientLogStore Store() => new(StorePath);

    private static LogEntryDto Entry(string message, DateTimeOffset? timestamp = null) =>
        new(timestamp ?? DateTimeOffset.UtcNow, "Information", "Test", message, null);

    [Fact]
    public void Repeated_snapshots_merge_new_lines_and_deduplicate_overlap()
    {
        var store = Store();
        var first = Entry("first");
        var second = Entry("second", first.Timestamp.AddMilliseconds(1));
        var third = Entry("third", first.Timestamp.AddMilliseconds(2));

        store.SetSnapshot("fp-1", "Alias1", [first, second], DateTimeOffset.UtcNow);
        store.SetSnapshot("fp-1", "Alias1", [second, third], DateTimeOffset.UtcNow);

        var snapshot = store.Get("fp-1");
        Assert.NotNull(snapshot);
        Assert.Equal(["first", "second", "third"], snapshot!.Entries.Select(entry => entry.Message));
        Assert.Single(store.All());
    }

    [Fact]
    public void History_survives_reconstructing_the_store()
    {
        Store().SetSnapshot("fp-1", "Alias1", [Entry("before restart")], DateTimeOffset.UtcNow);

        var reopened = Store().Get("fp-1");

        Assert.NotNull(reopened);
        Assert.Equal("before restart", Assert.Single(reopened!.Entries).Message);
    }

    [Fact]
    public void Files_are_organized_by_device_with_clear_utc_dates()
    {
        var timestamp = new DateTimeOffset(2026, 8, 28, 21, 16, 54, TimeSpan.FromHours(-4));
        Store().SetSnapshot("5306d3acebf0e49870b4e44f338afd1c", "Mr Téléphone",
            [Entry("file me", timestamp)], timestamp);

        var deviceDirectory = Assert.Single(Directory.GetDirectories(StorePath));
        Assert.Contains("Mr Téléphone--5306d3acebf0e49870b4e44f338afd1c", deviceDirectory);
        Assert.True(File.Exists(Path.Combine(deviceDirectory, "2026-08-29T00-00-00Z.logs.jsonl")));
        Assert.True(File.Exists(Path.Combine(deviceDirectory, "device.json")));
    }

    [Fact]
    public void Entries_and_inactive_sources_older_than_one_week_are_dropped()
    {
        var old = DateTimeOffset.UtcNow.Subtract(ClientLogStore.Retention).AddMinutes(-1);
        Store().SetSnapshot("fp-old", "Old Phone", [Entry("expired", old)], old);

        var reopened = Store();

        Assert.Null(reopened.Get("fp-old"));
    }

    [Fact]
    public void An_old_line_in_a_current_snapshot_is_not_retained()
    {
        var now = DateTimeOffset.UtcNow;
        var store = Store();
        store.SetSnapshot("fp-1", "Alias1",
            [Entry("expired", now.Subtract(ClientLogStore.Retention).AddMinutes(-1)), Entry("fresh", now)], now);

        var snapshot = store.Get("fp-1");

        Assert.Equal("fresh", Assert.Single(snapshot!.Entries).Message);
    }

    [Fact]
    public void Distinct_fingerprints_coexist_independently()
    {
        var store = Store();
        store.SetSnapshot("fp-1", "Alias1", [Entry("a")], DateTimeOffset.UtcNow);
        store.SetSnapshot("fp-2", "Alias2", [Entry("b")], DateTimeOffset.UtcNow);

        Assert.Equal(2, store.All().Count);
        Assert.Equal("a", store.Get("fp-1")!.Entries[0].Message);
        Assert.Equal("b", store.Get("fp-2")!.Entries[0].Message);
    }

    [Fact]
    public void Get_on_unknown_fingerprint_returns_null()
    {
        Assert.Null(Store().Get("does-not-exist"));
    }

    [Fact]
    public void SnapshotUpdated_fires_with_the_correct_fingerprint()
    {
        var store = Store();
        string? received = null;
        store.SnapshotUpdated += (_, fingerprint) => received = fingerprint;

        store.SetSnapshot("fp-9", "Alias9", [Entry("x")], DateTimeOffset.UtcNow);

        Assert.Equal("fp-9", received);
    }
}
