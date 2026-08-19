using System;
using System.Collections.Generic;

namespace Flower.Services;

// Keeps the "-=" for every "+=", so a long-lived subscriber can actually let go.
//
// Nothing in the ViewModel layer unsubscribed from anything: MainViewModel's
// constructor alone attaches to sixteen event sources and implemented no
// IDisposable, which is harmless only because it is a process-lifetime
// singleton - a property nothing enforced, and the thing that made it
// impossible to construct one per test. See docs/ARCHITECTURE-REVIEW.md
// Tier 2.3.
//
// The awkwardness this exists to remove is that unsubscribing from a lambda
// requires keeping the delegate instance around: `x.E += (_, _) => ...` cannot
// be undone at all, and doing it by hand means a named local plus a matching
// teardown line per subscription, far from each other, which is exactly the
// arrangement that goes stale. Here the two halves are one call.
public sealed class SubscriptionBag : IDisposable
{
    private readonly List<Action> _undo = new();

    public int Count => _undo.Count;

    public void Add<THandler>(THandler handler, Action<THandler> subscribe, Action<THandler> unsubscribe)
        where THandler : Delegate
    {
        subscribe(handler);
        _undo.Add(() => unsubscribe(handler));
    }

    // Idempotent: disposing twice unsubscribes once, so an owner that is
    // disposed by both the container and a test teardown is fine.
    public void Dispose()
    {
        foreach (var undo in _undo)
            undo();
        _undo.Clear();
    }
}
