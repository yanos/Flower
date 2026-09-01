using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Which smart playlist has to be evaluated before which.
//
// A membership rule lets one smart playlist ask about another - "genre is Rock
// and is not in Already Heard" - so Already Heard has to have been worked out
// before Fresh Rock can be. That is all this is: a dependency graph over
// SmartField.Playlist references, walked leaves-first.
//
// Ordinary playlists never appear in the graph. They hold their members
// outright, so they are always ready to be asked about and never depend on
// anything.
//
// The failure case is a cycle - A = "not in B", B = "not in A". There is no
// order that satisfies both and no correct answer either: whichever ran first
// would decide the other, and the pair would swap contents on every recompute.
// The editor is expected to make that unrepresentable by asking
// ReferenceCandidates what it may offer; EvaluationOrder still refuses loudly,
// because a rules blob can also arrive from a peer or a hand-edited database.
public static class SmartPlaylistGraph
{
    public static IReadOnlySet<Guid> DependenciesOf(SmartPlaylistRules rules)
    {
        var ids = new HashSet<Guid>();
        Collect(rules.Conditions, ids);
        return ids;
    }

    private static void Collect(IEnumerable<SmartCondition> conditions, HashSet<Guid> into)
    {
        foreach (var condition in conditions)
        {
            if (condition.Field == SmartField.Playlist && condition.Value is SmartValue.PlaylistRef reference)
                into.Add(reference.PlaylistId);
        }
    }

    // Every smart playlist, ordered so that each comes after everything it
    // references. Playlists that reference nothing keep their given order, so a
    // library with no membership rules recomputes in a stable, obvious order.
    public static IReadOnlyList<Guid> EvaluationOrder(IReadOnlyDictionary<Guid, SmartPlaylistRules> smart)
    {
        ArgumentNullException.ThrowIfNull(smart);

        var order = new List<Guid>(smart.Count);
        var finished = new HashSet<Guid>();
        // The nodes on the current descent. A dependency that lands back on one
        // of these is the cycle - that is the whole detection.
        var onPath = new List<Guid>();
        var onPathSet = new HashSet<Guid>();

        foreach (var id in smart.Keys)
            Visit(id, smart, order, finished, onPath, onPathSet);

        return order;
    }

    private static void Visit(
        Guid id,
        IReadOnlyDictionary<Guid, SmartPlaylistRules> smart,
        List<Guid> order,
        HashSet<Guid> finished,
        List<Guid> onPath,
        HashSet<Guid> onPathSet)
    {
        if (finished.Contains(id))
            return;

        // An ordinary playlist, or one this device does not have: a leaf, and
        // not ours to order. Membership resolution tolerates the missing case
        // by treating it as empty - see SmartPlaylistEvaluator.MatchesPlaylist.
        if (!smart.TryGetValue(id, out var rules))
            return;

        if (!onPathSet.Add(id))
            throw new SmartPlaylistCycleException([.. onPath.SkipWhile(step => step != id), id]);

        onPath.Add(id);

        foreach (var dependency in DependenciesOf(rules))
            Visit(dependency, smart, order, finished, onPath, onPathSet);

        onPath.RemoveAt(onPath.Count - 1);
        onPathSet.Remove(id);
        finished.Add(id);
        order.Add(id);
    }

    // Which playlists the editor may offer as the target of a membership rule
    // on `editing`: everything except itself and everything that already
    // depends on it, directly or through a chain. Refusing at edit time is what
    // keeps a cycle out of the database in the first place.
    //
    // allPlaylistIds is every playlist on the device, ordinary ones included -
    // they are always safe to reference, since they depend on nothing.
    public static IReadOnlyList<Guid> ReferenceCandidates(
        Guid editing,
        IEnumerable<Guid> allPlaylistIds,
        IReadOnlyDictionary<Guid, SmartPlaylistRules> smart)
    {
        var dependents = DependentsOf(editing, smart);
        return allPlaylistIds.Where(id => id != editing && !dependents.Contains(id)).ToList();
    }

    // Would saving these rules for this playlist create a cycle? The question
    // ReferenceCandidates answers per-target, asked about a whole rule set -
    // for validating something typed, pasted or synced rather than picked.
    public static bool WouldCycle(
        Guid editing,
        SmartPlaylistRules rules,
        IReadOnlyDictionary<Guid, SmartPlaylistRules> smart)
    {
        var proposed = new Dictionary<Guid, SmartPlaylistRules>(smart) { [editing] = rules };
        try
        {
            EvaluationOrder(proposed);
            return false;
        }
        catch (SmartPlaylistCycleException)
        {
            return true;
        }
    }

    // Everything that would have to be evaluated after `id`, transitively.
    private static HashSet<Guid> DependentsOf(Guid id, IReadOnlyDictionary<Guid, SmartPlaylistRules> smart)
    {
        var dependents = new HashSet<Guid>();
        var changed = true;

        // Repeated sweeps rather than a reversed graph: the set is a handful of
        // playlists, and this cannot recurse into a cycle that already exists.
        while (changed)
        {
            changed = false;
            foreach (var (candidate, rules) in smart)
            {
                if (dependents.Contains(candidate))
                    continue;

                var dependencies = DependenciesOf(rules);
                if (dependencies.Contains(id) || dependencies.Any(dependents.Contains))
                    changed = dependents.Add(candidate);
            }
        }

        return dependents;
    }
}

public sealed class SmartPlaylistCycleException(IReadOnlyList<Guid> cycle)
    : Exception($"Smart playlists reference each other in a loop: {string.Join(" -> ", cycle)}.")
{
    // The playlists forming the loop, in the order they were walked, starting
    // and ending on the same id. Enough for the editor to name them.
    public IReadOnlyList<Guid> Cycle { get; } = cycle;
}
