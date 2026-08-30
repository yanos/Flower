using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Logging;

namespace Flower.Services;

// A week of this device's own log lines, on disk.
//
// InMemoryLogStore is a 2000-line ring in a single process, which is the wrong
// shape for the one thing these logs exist for: reading, on the server, what a
// phone was doing an hour or a day ago. A phone restarts constantly and spends
// most of its life unable to reach the server at all, so anything that only
// lives in the ring is gone before it is ever asked for.
//
// Deliberately the same ClientLogStore the server keeps its received logs in,
// with this device as the single tenant: identical retention, identical event
// hashing, identical ordering - and that last one is load-bearing, because the
// watermark handshake only works if both ends agree on which entry is "newest"
// (see LogWatermarkDto).
public sealed class DeviceLogArchive
{
    private readonly ClientLogStore _store;
    private readonly InMemoryLogStore _live;

    private readonly object _lock = new();
    private long _archived = InMemoryLogStore.BeforeFirstSequence;
    private bool _loaded;
    private IReadOnlyList<LogEntryDto> _retained = [];

    public DeviceLogArchive(ClientLogStore store, InMemoryLogStore live)
    {
        _store = store;
        _live = live;
    }

    // Drain whatever the live ring has gained since the last call onto disk.
    // Must run on its own schedule rather than inside a push: lines logged
    // while no server is listed are exactly the ones worth keeping, and the
    // ring drops them within a session if nobody is draining it.
    public void Ingest(string fingerprint, string alias)
    {
        lock (_lock)
        {
            var slice = _live.SnapshotAfter(_archived);
            if (slice.Entries.Count == 0 && _loaded)
                return;

            var snapshot = _store.SetSnapshot(
                fingerprint,
                alias,
                slice.Entries.Select(LogEntryDto.FromEntry).ToList(),
                DateTimeOffset.UtcNow);

            _archived = slice.LastSequence;
            _retained = snapshot.Entries;
            _loaded = true;
        }
    }

    // Everything retained that orders after the server's watermark. A null
    // watermark means the server has nothing at all for this device, so it gets
    // the whole retained week.
    public IReadOnlyList<LogEntryDto> EntriesAfter(LogWatermarkDto? watermark)
    {
        lock (_lock)
        {
            if (watermark?.LastEntryTimestamp is not { } timestamp)
                return _retained;

            var eventId = watermark.LastEventId ?? string.Empty;
            return _retained
                .Where(entry => entry.Timestamp > timestamp
                                || (entry.Timestamp == timestamp
                                    && string.CompareOrdinal(ClientLogStore.EventId(entry), eventId) > 0))
                .ToList();
        }
    }

    // The watermark the server would report back after receiving everything
    // just sent - used to advance the local mark when a push succeeds without
    // waiting to be told, and to sanity-check the ack against.
    public static LogWatermarkDto WatermarkOf(IReadOnlyList<LogEntryDto> sent) =>
        sent.Count == 0
            ? new LogWatermarkDto(null, null)
            : Watermark(ClientLogStore.Ordered(sent)[^1]);

    public static LogWatermarkDto Watermark(LogEntryDto newest) =>
        new(newest.Timestamp, ClientLogStore.EventId(newest));
}
