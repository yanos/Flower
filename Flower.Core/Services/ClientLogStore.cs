using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Services;

// One paired device's most recently pushed log snapshot, keyed by the
// fingerprint the signature check already validated the request against - not
// whatever fingerprint the request body itself claims, which a caller is free
// to write anything into. See SyncEndpoints' /log/report route.
public sealed record ClientLogSnapshot(string Fingerprint, string Alias, DateTimeOffset ReceivedAt, IReadOnlyList<LogEntryDto> Entries);

// Lives on Flower.Server, in memory only - not persisted. A restart clears it,
// and the next sync from each paired device repopulates it. That is the right
// trade for what this is: a diagnostic snapshot of what a listener's phone has
// been doing lately, wanted while something is going wrong and worth nothing
// afterwards. Persisting it would mean keeping other people's file paths and
// exception text on disk indefinitely for no gain.
//
// Each SetSnapshot call is a full replace, not an append/merge - there is
// nothing to reconcile line-by-line, since every push already carries a fresh
// full snapshot of that device's current buffer.
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
