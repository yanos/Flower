using System;
using System.IO;
using System.Linq;

using Flower.Logging;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The client's half of the log handshake: a week of its own lines on disk, and
// "everything after the point the server named".
//
// The disk part is what makes the rest work. A memory ring can only ever offer
// what one process has left, so a phone that restarted - or spent the afternoon
// with the server switched off - had nothing to hand over however politely it
// was asked.
public class DeviceLogArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flower-archive-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DeviceLogArchive NewArchive() =>
        new(new ClientLogStore(Path.Combine(_root, "logs", "devices")), InMemoryLogStore.Instance);

    private static LogEntryDto Entry(DateTimeOffset at, string message) =>
        new(at, "Information", "Flower.Test", message, null);

    // Two lines can share a timestamp - the platform clock is coarse enough on
    // Windows for a burst to land on one - so the watermark carries the event
    // id as a tie-break. A timestamp alone would force a choice between
    // re-sending the newest line forever and dropping its neighbours.
    [Fact]
    public void Entries_sharing_the_watermarks_timestamp_are_split_by_event_id()
    {
        // One timestamp, two lines - what a burst looks like on a coarse clock.
        var at = DateTimeOffset.UtcNow;
        var marker = Guid.NewGuid().ToString();
        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(at, "Information", "Flower.Test", marker + "-a", null));
        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(at, "Information", "Flower.Test", marker + "-b", null));

        var archive = NewArchive();
        archive.Ingest("fp", "Client");

        var pair = ClientLogStore.Ordered(
            archive.EntriesAfter(null).Where(entry => entry.Message.StartsWith(marker, StringComparison.Ordinal)));
        Assert.Equal(2, pair.Count);

        // Told "I have the first of them", the archive offers the second and
        // not the first - which a timestamp alone could not express.
        var pending = archive.EntriesAfter(DeviceLogArchive.Watermark(pair[0]));

        Assert.DoesNotContain(pending, entry => entry.Message == pair[0].Message);
        Assert.Contains(pending, entry => entry.Message == pair[1].Message);
    }

    // The whole point of persisting: a fresh archive over the same directory is
    // the same client on its next launch, and it still has yesterday to offer.
    [Fact]
    public void A_new_archive_over_the_same_directory_still_holds_what_was_written()
    {
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        new ClientLogStore(Path.Combine(_root, "logs", "devices"))
            .SetSnapshot("fp", "Client", [Entry(yesterday, "from yesterday")], DateTimeOffset.UtcNow);

        var reopened = new ClientLogStore(Path.Combine(_root, "logs", "devices")).Get("fp");

        Assert.Equal("from yesterday", Assert.Single(reopened!.Entries).Message);
    }

    // Bounded at a week, the same bound the server keeps. Anything older is not
    // worth carrying and definitely not worth pushing.
    [Fact]
    public void Lines_older_than_a_week_are_not_retained()
    {
        var store = new ClientLogStore(Path.Combine(_root, "logs", "devices"));
        var stale = DateTimeOffset.UtcNow - ClientLogStore.Retention - TimeSpan.FromHours(1);

        var stored = store.SetSnapshot("fp", "Client",
            [Entry(stale, "ancient"), Entry(DateTimeOffset.UtcNow, "recent")], DateTimeOffset.UtcNow);

        Assert.Equal("recent", Assert.Single(stored.Entries).Message);
    }

    // A null watermark is the server saying it has nothing for this device at
    // all - a first pairing, or a restored backup. Send the lot.
    [Fact]
    public void An_empty_watermark_asks_for_everything_retained()
    {
        var archive = NewArchive();
        archive.Ingest("fp", "Client");

        var everything = archive.EntriesAfter(new LogWatermarkDto(null, null));

        Assert.Equal(archive.EntriesAfter(null).Count, everything.Count);
    }
}
