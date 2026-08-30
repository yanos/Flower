using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Logging
{
    // A contiguous run of entries a caller has not seen yet, plus the sequence
    // to ask for next time. LastSequence is the store's own high-water mark at
    // the moment of the read, not the sequence of the last entry returned:
    // anything in between was evicted by the ring and is gone for good, so a
    // caller that advances to it is correctly declining to wait for lines that
    // will never come back. See SnapshotAfter.
    public readonly record struct LogSlice(long LastSequence, IReadOnlyList<InMemoryLogEntry> Entries);

    // Bounded live log buffer backing the Log window's view of this device (see
    // Flower.ViewModels.LogViewModel) and the payload LibrarySyncService pushes
    // to a paired Server (see LogSyncContracts). Must be a static singleton,
    // not a DI-registered instance: it's wired into Serilog inside
    // AppLogging.Initialize(), a static method that runs before App.axaml.cs's
    // DI container exists - see AppLogging's own doc comment for why that
    // ordering matters. The same instance is later handed to DI as a singleton
    // (see App.axaml.cs) so constructor-injected classes can share it.
    public sealed class InMemoryLogStore
    {
        public static readonly InMemoryLogStore Instance = new();

        // Log volume here is sparse (event-driven state transitions/errors, not
        // per-frame chatter - confirmed ~100 call sites total across the whole
        // app), so this comfortably spans many sessions' worth of activity, not
        // just a few seconds.
        private const int MaxEntries = 2000;

        // The sequence a caller passes to SnapshotAfter to mean "I have seen
        // nothing at all, give me everything still buffered".
        public const long BeforeFirstSequence = -1;

        private readonly object _lock = new();
        private readonly Queue<(long Sequence, InMemoryLogEntry Entry)> _entries = new();

        // Monotonic and never reset for the life of the process, so a sequence
        // stays meaningful after the ring has evicted the entry it belonged to.
        private long _nextSequence;

        public event EventHandler<InMemoryLogEntry>? EntryAdded;

        private InMemoryLogStore()
        {
        }

        // Highest sequence assigned so far, or BeforeFirstSequence when nothing
        // has ever been logged.
        public long LastSequence
        {
            get
            {
                lock (_lock)
                    return _nextSequence - 1;
            }
        }

        public void Add(InMemoryLogEntry entry)
        {
            lock (_lock)
            {
                _entries.Enqueue((_nextSequence++, entry));
                while (_entries.Count > MaxEntries)
                    _entries.Dequeue();
            }

            EntryAdded?.Invoke(this, entry);
        }

        // Isolated copy - safe for a caller to enumerate off-thread without
        // racing a concurrent Add.
        public IReadOnlyList<InMemoryLogEntry> Snapshot()
        {
            lock (_lock)
                return _entries.Select(e => e.Entry).ToArray();
        }

        // Everything buffered past afterSequence. This is what makes a log push
        // a delta rather than a full re-send of the buffer: a caller keeps the
        // returned LastSequence and hands it back next time, so a quiet device
        // sends nothing and a chatty one sends only its new lines. Pass
        // BeforeFirstSequence for the first read of a session.
        public LogSlice SnapshotAfter(long afterSequence)
        {
            lock (_lock)
            {
                var entries = _entries
                    .Where(e => e.Sequence > afterSequence)
                    .Select(e => e.Entry)
                    .ToArray();
                return new LogSlice(_nextSequence - 1, entries);
            }
        }
    }
}
