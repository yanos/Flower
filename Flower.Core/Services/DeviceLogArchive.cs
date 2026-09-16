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

    // The retained week, held rather than re-read. Ordered oldest-first, which
    // is what makes the retention check below an O(1) look at the head rather
    // than a pass over the whole list every drain.
    private readonly List<LogEntryDto> _retained = [];

    public DeviceLogArchive(ClientLogStore store, InMemoryLogStore live)
    {
        _store = store;
        _live = live;
    }

    // How far the live ring has got, whether or not it has been drained yet -
    // what a push compares against to tell "something was logged since" from
    // "nothing was".
    public long LiveSequence => _live.LastSequence;

    // Drain whatever the live ring has gained since the last call onto disk.
    // Must run on its own schedule rather than inside a push: lines logged
    // while no server is listed are exactly the ones worth keeping, and the
    // ring drops them within a session if nobody is draining it.
    //
    // Costs one read of the archive per session, on the first drain, and an
    // append per drain after that. It used to read and re-hash the entire
    // retained week on every drain - see ClientLogStore.Append, which exists
    // because that is a second of a phone's CPU every five seconds once the
    // week is big enough.
    public void Ingest(string fingerprint, string alias)
    {
        lock (_lock)
        {
            var slice = _live.SnapshotAfter(_archived);
            if (slice.Entries.Count == 0 && _loaded)
                return;

            var now = DateTimeOffset.UtcNow;
            var entries = slice.Entries.Select(LogEntryDto.FromEntry).ToList();

            if (_loaded)
            {
                _store.Append(fingerprint, alias, entries, now);
            }
            else
            {
                // The one drain that has to read: it is where a previous
                // session's week comes back into memory. SetSnapshot also
                // dedups, which matters exactly here - a crash between the
                // append and the sequence advance would otherwise re-archive
                // whatever the ring still holds from before the restart.
                _retained.AddRange(_store.SetSnapshot(fingerprint, alias, entries, now).Entries);
                _archived = slice.LastSequence;
                _loaded = true;
                return;
            }

            _retained.AddRange(entries);
            DropExpired(now);
            _archived = slice.LastSequence;
        }
    }

    // Retention, in memory. The list is ordered oldest-first and lines arrive
    // in time order, so anything expired is a prefix - which makes the common
    // case (nothing has aged out since the last drain) a single comparison.
    // The caller holds _lock.
    private void DropExpired(DateTimeOffset now)
    {
        var cutoff = now.Subtract(ClientLogStore.Retention);
        if (_retained.Count == 0 || _retained[0].Timestamp >= cutoff)
            return;

        var expired = 0;
        while (expired < _retained.Count && _retained[expired].Timestamp < cutoff)
            expired++;

        _retained.RemoveRange(0, expired);
    }

    // Everything retained that orders after the server's watermark. A null
    // watermark means the server has nothing at all for this device, so it gets
    // the whole retained week.
    public IReadOnlyList<LogEntryDto> EntriesAfter(LogWatermarkDto? watermark)
    {
        lock (_lock)
        {
            // Copied rather than handed over: _retained is appended to by
            // every drain now, and a caller serializing it on another thread
            // must not have it grow underneath them.
            if (watermark?.LastEntryTimestamp is not { } timestamp)
                return _retained.ToList();

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
