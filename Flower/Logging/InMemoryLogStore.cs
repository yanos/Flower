using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Logging
{
    // Bounded live log buffer backing the Log window's "This Device" view (see
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

        private readonly object _lock = new();
        private readonly Queue<InMemoryLogEntry> _entries = new();

        public event EventHandler<InMemoryLogEntry>? EntryAdded;

        private InMemoryLogStore()
        {
        }

        public void Add(InMemoryLogEntry entry)
        {
            lock (_lock)
            {
                _entries.Enqueue(entry);
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
                return _entries.ToArray();
        }
    }
}
