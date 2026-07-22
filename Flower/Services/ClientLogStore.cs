using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Services;

// One Client's most recently pushed log snapshot, keyed by the fingerprint
// SyncHttpServer's AuthorizeAsync already validated the request against (not
// whatever fingerprint the request body itself claims - see
// SyncHttpServer.HandleReportLogAsync).
public sealed record ClientLogSnapshot(string Fingerprint, string Alias, DateTimeOffset ReceivedAt, IReadOnlyList<LogEntryDto> Entries);

// Server-side only, in-memory only - not persisted. A restart clears it; the
// next time each paired Client syncs, its next push repopulates this store.
// Each SetSnapshot call is a full replace, not an append/merge - there is
// nothing to reconcile line-by-line since every push already carries a fresh
// full snapshot of the client's current buffer (see LogViewModel, which
// treats a SnapshotUpdated event as "redraw this row's lines from scratch").
public sealed class ClientLogStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ClientLogSnapshot> _snapshots = new();

    // string argument is the fingerprint whose snapshot just changed.
    public event EventHandler<string>? SnapshotUpdated;

    public void SetSnapshot(string fingerprint, string alias, IReadOnlyList<LogEntryDto> entries, DateTimeOffset receivedAt)
    {
        lock (_lock)
            _snapshots[fingerprint] = new ClientLogSnapshot(fingerprint, alias, receivedAt, entries);

        SnapshotUpdated?.Invoke(this, fingerprint);
    }

    public ClientLogSnapshot? Get(string fingerprint)
    {
        lock (_lock)
            return _snapshots.GetValueOrDefault(fingerprint);
    }

    public IReadOnlyList<ClientLogSnapshot> All()
    {
        lock (_lock)
            return _snapshots.Values.ToList();
    }
}
