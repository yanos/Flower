using System;
using System.Threading;

namespace Flower.Tests.TestSupport;

// Waiting for a real-time-paced sink to have consumed some amount of audio.
//
// Two things were wrong with doing this by hand, and both cost CI a red
// build rather than finding a bug.
//
// **A flat deadline measures the runner, not the code.** FakeAudioSink paces
// itself to GaplessFormat.SampleRate, so it can only ever be slower than real
// time, never faster - Thread.Sleep(n) sleeps *at least* n, and the overshoot
// compounds once several decode-heavy test classes run in parallel on a
// three-core hosted runner. Asking for three seconds of audio inside fifteen
// looks like five times the headroom and is not: it is one budget covering
// decode, pacing slop and scheduler contention at once, and a loaded macOS
// runner spends it. What the assertion then reports is "playback stalled",
// which is a claim about the pipeline that the timeout never actually tested.
//
// So the deadline here is on *progress*, not on total elapsed time. A stall -
// the thing these tests exist to catch, a promoted decoder that never resumed
// feeding the shared ring - is bytes that stop arriving, and it shows up
// within seconds however slow the machine is. A slow runner is bytes that keep
// arriving late, which is not a defect and no longer reads as one. The overall
// cap is only there so a genuinely wedged pipeline fails the job instead of
// hanging it.
//
// **Spinning competes with the work.** SpinWait.SpinUntil burns a core to
// watch a counter that another thread has to be scheduled to move. On the
// machine where this actually matters - the contended one - the waiter was
// taking CPU from the decoder and the pump it was waiting on. Sleeping between
// polls costs a few milliseconds of latency and gives the core back.
public static class PlaybackWait
{
    // How long without a single new byte counts as stopped. Generous next to
    // the millisecond timescale a working pipeline moves on, and far below the
    // old flat budgets.
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(8);

    // Backstop, not a budget: nothing healthy comes near it, and it exists so
    // a wedged run fails rather than sits there until the job is killed.
    private static readonly TimeSpan OverallCap = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    // Waits for `progress` to reach `target`, tolerating any amount of slowness
    // so long as it keeps moving.
    public static void UntilReaches(Func<long> progress, long target, string because)
        => UntilReaches(progress, target, because, DefaultStallTimeout);

    public static void UntilReaches(Func<long> progress, long target, string because, TimeSpan stallTimeout)
    {
        var started = DateTime.UtcNow;
        var lastMoved = started;
        var seen = progress();

        while (seen < target)
        {
            Thread.Sleep(PollInterval);

            var now = progress();
            if (now != seen)
            {
                seen = now;
                lastMoved = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - lastMoved > stallTimeout)
            {
                Assert.Fail(
                    $"{because} (stopped at {seen} of {target} and produced nothing for " +
                    $"{stallTimeout.TotalSeconds:F0}s)");
            }

            if (DateTime.UtcNow - started > OverallCap)
            {
                Assert.Fail(
                    $"{because} (still only at {seen} of {target} after {OverallCap.TotalMinutes:F0} minutes)");
            }
        }
    }

    // The same rule for something that is a flag rather than a counter -
    // Drained having fired, a seek having landed. There is no progress to
    // watch, so this is a plain deadline; it is here so those waits stop
    // spinning too, and so the one number they share lives in one place.
    public static void UntilTrue(Func<bool> condition, string because)
        => UntilTrue(condition, because, TimeSpan.FromSeconds(60));

    public static void UntilTrue(Func<bool> condition, string because, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{because} (still false after {budget.TotalSeconds:F0}s)");

            Thread.Sleep(PollInterval);
        }
    }
}
