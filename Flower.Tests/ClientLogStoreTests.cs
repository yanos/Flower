using System;
using System.Collections.Generic;

using Flower.Services;

namespace Flower.Tests;

public class ClientLogStoreTests
{
    private static List<LogEntryDto> Entries(params string[] messages)
    {
        var list = new List<LogEntryDto>();
        foreach (var message in messages)
            list.Add(new LogEntryDto(DateTimeOffset.UtcNow, "Information", null, message, null));
        return list;
    }

    [Fact]
    public void SetSnapshot_twice_for_the_same_fingerprint_replaces_not_appends()
    {
        var store = new ClientLogStore();
        store.SetSnapshot("fp-1", "Alias1", Entries("first"), DateTimeOffset.UtcNow);
        store.SetSnapshot("fp-1", "Alias1", Entries("second", "third"), DateTimeOffset.UtcNow);

        var snapshot = store.Get("fp-1");
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Entries.Count);
        Assert.Equal("second", snapshot.Entries[0].Message);
        Assert.Equal("third", snapshot.Entries[1].Message);
        Assert.Single(store.All());
    }

    [Fact]
    public void Distinct_fingerprints_coexist_independently()
    {
        var store = new ClientLogStore();
        store.SetSnapshot("fp-1", "Alias1", Entries("a"), DateTimeOffset.UtcNow);
        store.SetSnapshot("fp-2", "Alias2", Entries("b"), DateTimeOffset.UtcNow);

        Assert.Equal(2, store.All().Count);
        Assert.Equal("a", store.Get("fp-1")!.Entries[0].Message);
        Assert.Equal("b", store.Get("fp-2")!.Entries[0].Message);
    }

    [Fact]
    public void Get_on_unknown_fingerprint_returns_null()
    {
        var store = new ClientLogStore();
        Assert.Null(store.Get("does-not-exist"));
    }

    [Fact]
    public void SnapshotUpdated_fires_with_the_correct_fingerprint()
    {
        var store = new ClientLogStore();
        string? received = null;
        store.SnapshotUpdated += (_, fingerprint) => received = fingerprint;

        store.SetSnapshot("fp-9", "Alias9", Entries("x"), DateTimeOffset.UtcNow);

        Assert.Equal("fp-9", received);
    }
}
