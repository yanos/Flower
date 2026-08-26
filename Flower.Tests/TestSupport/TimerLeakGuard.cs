using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Threading;

using Xunit;
using Xunit.v3;

[assembly: Flower.Tests.TestSupport.TimerLeakGuard]

namespace Flower.Tests.TestSupport;

// Fails the test suite when a DispatcherTimer owned by Flower code outlives the
// test that started one.
//
// Why this matters here and not in a normal test project: the whole assembly
// shares one Avalonia dispatcher (see TestAppBuilder for why it has to), and
// Dispatcher's timer list is an instance field. Under per-test isolation a timer
// a test forgot to stop died with that test's dispatcher; now it keeps ticking
// for the rest of the run, and a Tick is not inert - it can reach Ioc.Default
// and the process-global PlatformDataDirectory.Current, which is exactly the
// shape of the accident AssemblySetup documents (a debounced save landing after
// a pinned data directory was restored, overwriting the developer's real
// settings.json).
//
// The guard runs as a per-test hook rather than only at the end of the run
// because of how the two failures read. An assembly fixture's disposal sits
// outside the test-result model: it sets the exit code but prints no message and
// names no test, so "the suite fails and I cannot see why" is the best anyone
// gets. Failing inside a test gives a normal failure, with the message, in the
// normal output.
//
// Attribution, so the failure names the culprit and not just the victim: After
// each test, every Flower-owned timer not seen before is tagged with the test
// that was running. Before each test, any tagged timer still alive is a leak,
// and it is reported with the name of the test that started it.
//
// Two deliberate limits:
//
//   - Only timers tagged by the *same test collection* are acted on. Collections
//     run in parallel, so a timer another collection legitimately has running
//     right now would otherwise fail an innocent test here. Within a collection
//     tests are serialized, so a still-alive timer really has outlived its test -
//     including its class's Dispose, which xUnit runs before the next test.
//   - Tagging is only trusted when this test had the run to itself. The check
//     above is collection-scoped, but the tagging that feeds it was not: After
//     tags every timer it has not seen before with the test that just finished,
//     even when that timer was created by a *different* collection running
//     concurrently. Such a timer is then "owned" by an innocent collection, and
//     fails its next test if the real owner is still legitimately using it -
//     which is exactly what a live MainViewModel elsewhere looks like, since
//     PeerSyncCoordinator's log-push timer runs for that view model's whole
//     lifetime. Observed on CI as SearchTextTests, TaskExtensionsTests and
//     LibraryOpenSubsonicMapperTests each blaming one of their own fast cases
//     for a PeerSyncCoordinator none of them constructs. So a timer first seen
//     while another collection had a test in flight is tagged ambiguous: kept
//     for the end-of-run backstop, never used to fail a test.
//   - A leak from the last test of a collection has no following test to fail,
//     and is caught by HeadlessSessionWarmup's end-of-run backstop instead.
//   - AnimationClock is skipped per-test, because it cannot be attributed to a
//     test at all: AnimationClock.Current is process-wide, and its 60Hz timer
//     exists exactly while *something anywhere* is animating, so a collection
//     running a spinner keeps it alive while an unrelated collection is between
//     tests. Measured before this exclusion existed: 29 tests across 29
//     collections failed on it, each blaming a different innocent test. The
//     end-of-run backstop still checks it, and that check is the meaningful one -
//     a clock still ticking after every test has finished means a subscription
//     was never disposed.
//
// Everything here reads private Avalonia fields, and reads a list the dispatcher
// thread may be mutating. It gives up silently on anything unexpected rather
// than failing: a guard against flakiness has no business becoming a source of
// it. A missed check costs nothing, because the next test checks again.
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TimerLeakGuardAttribute : BeforeAfterTestAttribute
{
    private sealed record Tag(
        WeakReference<DispatcherTimer> Timer, string Owner, string Test, string Collection, bool Ambiguous);

    private static readonly List<Tag> s_tagged = new();

    // A test currently between Before and After, and whether it has had the run
    // to itself for all of that - see the tagging limit in the class comment.
    private sealed class Running(string collection)
    {
        public string Collection { get; } = collection;

        public bool Solo { get; set; } = true;
    }

    // Keyed by test unique id rather than by the IXunitTest instance, which is
    // not promised to be the same object in Before and After. Two invocations of
    // one test never overlap, so the id is unambiguous while it is in here.
    private static readonly Dictionary<string, Running> s_running = new();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var collection = CollectionOf(test);

        var leaked = TakeLeaksFrom(collection);
        if (leaked.Count > 0)
        {
            // Thrown before this test is registered as running, on purpose:
            // xUnit skips After for an attribute whose Before threw, so
            // registering first would leave an entry nothing ever removes and
            // every later test would look non-solo.
            throw new InvalidOperationException(Explain(leaked));
        }

        lock (s_running)
        {
            var entry = new Running(collection);

            // Overlap is symmetric: neither this test nor anything already in
            // flight from another collection can claim a timer that appears
            // from here on.
            if (s_running.Values.Any(r => r.Collection != collection))
            {
                entry.Solo = false;
                foreach (var other in s_running.Values)
                    other.Solo = false;
            }

            s_running[test.UniqueID] = entry;
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var collection = CollectionOf(test);

        bool solo;
        lock (s_running)
        {
            solo = s_running.Remove(test.UniqueID, out var entry) && entry.Solo;
        }

        foreach (var (timer, owner) in FlowerOwnedTimers().Where(t => !IsSharedClock(t.Owner)))
        {
            lock (s_tagged)
            {
                if (s_tagged.Any(t => t.Timer.TryGetTarget(out var known) && ReferenceEquals(known, timer)))
                    continue;

                s_tagged.Add(new Tag(
                    new WeakReference<DispatcherTimer>(timer), owner, test.TestDisplayName, collection, !solo));
            }
        }
    }

    // Leaks attributable to `collection`, removed from the tag list as they are
    // reported so one leak fails one test rather than every test after it.
    private static List<Tag> TakeLeaksFrom(string collection)
    {
        var found = new List<Tag>();

        lock (s_tagged)
        {
            if (s_tagged.Count == 0)
                return found;

            var alive = FlowerOwnedTimers().Select(t => t.Timer).ToList();

            s_tagged.RemoveAll(tag =>
            {
                if (!tag.Timer.TryGetTarget(out var timer))
                    return true;
                if (tag.Ambiguous || tag.Collection != collection)
                    return false;
                if (!alive.Any(a => ReferenceEquals(a, timer)))
                    return true;

                found.Add(tag);
                return true;
            });
        }

        return found;
    }

    private static string Explain(IEnumerable<Tag> leaked) =>
        "A DispatcherTimer owned by Flower code outlived the test that started it, and is now " +
        "ticking during unrelated tests on the shared dispatcher. Dispose whatever owns it - a " +
        "MainViewModel dropped without Dispose is the usual cause, and MainViewModelHarness.Parts " +
        "is IDisposable for exactly this reason. Leaked: " +
        string.Join("; ", leaked.Select(t => $"{t.Owner} started by {t.Test}"));

    // The end-of-run backstop's view: every Flower-owned timer still alive,
    // whichever collection started it, named with its culprit test where the
    // tag survived. Null when there is nothing to report.
    internal static string? DescribeSurvivors()
    {
        var alive = FlowerOwnedTimers();
        if (alive.Count == 0)
            return null;

        lock (s_tagged)
        {
            var described = alive.Select(a =>
            {
                var tag = s_tagged.FirstOrDefault(
                    t => t.Timer.TryGetTarget(out var known) && ReferenceEquals(known, a.Timer));
                return tag switch
                {
                    null                       => a.Owner,
                    { Ambiguous: true }        => $"{a.Owner} (first seen during {tag.Test}, which ran "
                                                  + "alongside other collections - that is where to start looking, "
                                                  + "not necessarily the culprit)",
                    _                          => $"{a.Owner} started by {tag.Test}",
                };
            });

            return "A DispatcherTimer owned by Flower code was still running after every test " +
                   "finished; it will have been ticking during unrelated tests. Leaked: " +
                   string.Join("; ", described);
        }
    }

    // Every timer registered on the shared dispatcher whose Tick handler leads
    // back to Flower code. Avalonia's own timers (the headless render clock and
    // a few internals) always outlive the run and are not leaks, which is why
    // this filters by owning assembly rather than counting.
    internal static List<(DispatcherTimer Timer, string Owner)> FlowerOwnedTimers()
    {
        var found = new List<(DispatcherTimer, string)>();

        try
        {
            if (typeof(Dispatcher).GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance) is not { } field)
                return found;
            if (field.GetValue(Dispatcher.UIThread) is not IEnumerable timers)
                return found;

            foreach (var timer in timers.Cast<object>().OfType<DispatcherTimer>().ToList())
            {
                if (TickHandler(timer) is not { } handler)
                    continue;

                foreach (var owner in OwnersOf(handler))
                {
                    if (owner.Assembly.GetName().Name?.StartsWith("Flower", StringComparison.Ordinal) != true)
                        continue;

                    found.Add((timer, $"{owner.FullName} (interval {timer.Interval})"));
                    break;
                }
            }
        }
        catch (Exception)
        {
            // The dispatcher thread was mid-mutation, or Avalonia moved the
            // field. Either way, say nothing.
        }

        return found;
    }

    // See the per-test limits in the class comment: the shared 60Hz clock's
    // timer belongs to no single test.
    private static bool IsSharedClock(string owner) =>
        owner.StartsWith("Flower.Services.AnimationClock", StringComparison.Ordinal);

    private static Delegate? TickHandler(DispatcherTimer timer) =>
        (typeof(DispatcherTimer).GetField("Tick", BindingFlags.NonPublic | BindingFlags.Instance)
         ?? typeof(DispatcherTimer).GetField("_tick", BindingFlags.NonPublic | BindingFlags.Instance))
        ?.GetValue(timer) as Delegate;

    // The type that would have to be disposed to stop this timer. Usually just
    // the handler's target, but DispatcherTimer.Run/RunOnce wrap the caller's
    // callback in a closure of Avalonia's own, so a timer started that way looks
    // like Avalonia's until one level of closure field is opened.
    private static IEnumerable<Type> OwnersOf(Delegate handler)
    {
        foreach (var entry in handler.GetInvocationList())
        {
            var target = entry.Target;
            yield return target?.GetType() ?? entry.Method.DeclaringType!;

            if (target is null)
                continue;

            foreach (var nested in target.GetType()
                         .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         .Select(f => f.GetValue(target))
                         .OfType<Delegate>())
            {
                yield return nested.Target?.GetType() ?? nested.Method.DeclaringType!;
            }
        }
    }

    private static string CollectionOf(IXunitTest test) =>
        test.TestCase.TestCollection.TestCollectionDisplayName;
}
