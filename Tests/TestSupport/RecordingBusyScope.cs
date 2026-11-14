using System.Collections.Generic;

using Flower.ViewModels;

namespace Flower.Tests.TestSupport;

// A status-bar scope that keeps what it was told, for whatever reports through
// IBusyScope without needing BusyState's dispatcher.
public sealed class RecordingBusyScope : IBusyScope
{
    private readonly List<string> _said = [];

    public RecordingBusyScope(string? opening = null)
    {
        Opening = opening;
    }

    // The message it was opened with.
    public string? Opening { get; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> Said
    {
        get
        {
            lock (_said)
                return [.. _said];
        }
    }

    public void Say(string message)
    {
        lock (_said)
            _said.Add(message);
    }

    public void Dispose() => Disposed = true;
}
