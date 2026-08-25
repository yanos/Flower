using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace Flower.Tests.TestSupport;

// Captures what was logged, so a test can assert on the *level* a line came
// out at rather than only on the behaviour around it.
//
// That distinction is worth a test seam because level is the whole mechanism
// keeping a repeating failure from burying the 2000-entry InMemoryLogStore the
// Log window and the client log push both read from: a line that should have
// been Trace and came out at Debug is not a cosmetic slip, it is the flood.
// Nothing else here can see that difference - NullLogger swallows it, and the
// behaviour under test is identical either way.
public sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message);

    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    // NetworkDiscoveryService polls its peers off a background loop, so the
    // list is written from a thread other than the one asserting on it.
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public int CountAt(LogLevel level, string containing) =>
        Entries.Count(e => e.Level == level && e.Message.Contains(containing, StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
