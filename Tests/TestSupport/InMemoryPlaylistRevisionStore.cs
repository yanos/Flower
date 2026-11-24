using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Services;

namespace Flower.Tests.TestSupport;

// PlaylistRevisionRepository's rule, without a database: what a scenario's
// server keeps, and what restoring a backup puts back.
public sealed class InMemoryPlaylistRevisionStore : IPlaylistRevisionStore
{
    private readonly object _lock = new();
    private Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> _rows = new();

    public Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> Load()
    {
        lock (_lock)
            return _rows.ToDictionary(e => e.Key, e => e.Value.ToList());
    }

    public void Append(IReadOnlyList<(string Listener, Guid Id, PlaylistRevision Revision)> issued, int keep)
    {
        lock (_lock)
        {
            foreach (var (listener, id, revision) in issued)
            {
                if (!_rows.TryGetValue((listener, id), out var revisions))
                    _rows[(listener, id)] = revisions = [];
                revisions.RemoveAll(r => r.Revision == revision.Revision);
                revisions.Add(revision);
                revisions.Sort((a, b) => a.Revision.CompareTo(b.Revision));
                if (revisions.Count > keep)
                    revisions.RemoveRange(0, revisions.Count - keep);
            }
        }
    }

    public void Replace(Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> rows)
    {
        lock (_lock)
            _rows = rows.ToDictionary(e => e.Key, e => e.Value.ToList());
    }
}
