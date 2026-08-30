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

    // The delta read behind incremental log push (see LibrarySyncLogPushTests):
    // hand back the sequence you were given last time, get only what has been
    // logged since.
    [Fact]
    public void SnapshotAfter_returns_only_entries_logged_since_the_given_sequence()
    {
        var before = InMemoryLogStore.Instance.LastSequence;
        var marker = Guid.NewGuid().ToString("N");
        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Information", "Test", marker, null));

        var first = InMemoryLogStore.Instance.SnapshotAfter(before);
        Assert.Contains(first.Entries, e => e.Message == marker);

        var second = InMemoryLogStore.Instance.SnapshotAfter(first.LastSequence);
        Assert.DoesNotContain(second.Entries, e => e.Message == marker);
    }

    // A caller that falls far enough behind for the ring to evict what it was
    // waiting for must still be moved forward, not pinned to a sequence that
    // will never be served again.
    [Fact]
    public void SnapshotAfter_advances_past_entries_the_ring_has_evicted()
    {
        var before = InMemoryLogStore.Instance.LastSequence;
        var marker = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2100; i++)
            InMemoryLogStore.Instance.Add(new InMemoryLogEntry(DateTimeOffset.Now, "Debug", "Test", $"{marker}-{i}", null));

        var slice = InMemoryLogStore.Instance.SnapshotAfter(before);

        Assert.True(slice.Entries.Count <= 2000);
        Assert.DoesNotContain(slice.Entries, e => e.Message == $"{marker}-0");
        Assert.True(slice.LastSequence >= before + 2100);
    }
}
