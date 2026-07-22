using System;
using Flower.Logging;

namespace Flower.Tests;

// InMemoryLogStore.Instance is a process-wide static singleton (see its own
// doc comment for why), so these tests construct entries and exercise the
// shared instance directly rather than needing a fresh instance per test -
// each test uses a unique marker string in its entries to avoid asserting on
// state another test (or another parallel test class) may have added.
public class InMemoryLogStoreTests
{
    [Fact]
    public void Snapshot_returns_an_isolated_copy_not_affected_by_later_adds()
    {
        var marker = Guid.NewGuid().ToString();
        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Information", "Test", marker, null));

        var snapshot = InMemoryLogStore.Instance.Snapshot();
        var countBefore = snapshot.Count;

        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Information", "Test", Guid.NewGuid().ToString(), null));

        Assert.Equal(countBefore, snapshot.Count);
    }

    [Fact]
    public void EntryAdded_fires_once_per_Add_with_the_added_entry()
    {
        var marker = Guid.NewGuid().ToString();
        InMemoryLogEntry? received = null;
        var fireCount = 0;

        void Handler(object? sender, InMemoryLogEntry e)
        {
            if (e.Message == marker)
            {
                received = e;
                fireCount++;
            }
        }

        InMemoryLogStore.Instance.EntryAdded += Handler;
        try
        {
            InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Warning", "Test", marker, null));
        }
        finally
        {
            InMemoryLogStore.Instance.EntryAdded -= Handler;
        }

        Assert.Equal(1, fireCount);
        Assert.NotNull(received);
        Assert.Equal("Warning", received!.Level);
    }

    [Fact]
    public void Snapshot_retains_newest_entries_in_order_once_over_capacity()
    {
        // MaxEntries is 2000 - push well past it and confirm the most
        // recently added entries survive in the order they were added,
        // oldest-evicted-first.
        var marker = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2100; i++)
            InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Debug", "Test", $"{marker}-{i}", null));

        var snapshot = InMemoryLogStore.Instance.Snapshot();
        Assert.True(snapshot.Count <= 2000);

        var lastMatching = snapshot[^1];
        Assert.Equal($"{marker}-2099", lastMatching.Message);
    }
}
