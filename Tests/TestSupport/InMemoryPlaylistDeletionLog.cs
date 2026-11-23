using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Services;

namespace Flower.Tests.TestSupport;

public sealed class InMemoryPlaylistDeletionLog : IPlaylistDeletionLog
{
    private readonly object _lock = new();
    private readonly HashSet<(string Listener, Guid Id)> _deleted = [];

    public IReadOnlySet<Guid> DeletedFor(string listener)
    {
        lock (_lock)
            return _deleted.Where(d => d.Listener == listener).Select(d => d.Id).ToHashSet();
    }

    public void Record(string listener, IReadOnlyCollection<Guid> playlistIds)
    {
        lock (_lock)
        {
            foreach (var id in playlistIds)
                _deleted.Add((listener, id));
        }
    }

    public void Forget(string listener, IReadOnlyCollection<Guid> playlistIds)
    {
        lock (_lock)
        {
            foreach (var id in playlistIds)
                _deleted.Remove((listener, id));
        }
    }
}
