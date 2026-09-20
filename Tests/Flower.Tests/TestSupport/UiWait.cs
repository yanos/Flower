using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

namespace Flower.Tests.TestSupport;

// Waiting for something a mobile navigation did off the UI thread to land.
//
// PlaybackWait is the same idea for the audio pipeline, and its comment has
// the general argument. This is its counterpart for the dispatcher, and it
// exists because the mobile drill-ins cannot be awaited by the code that
// starts them: SelectPlaylistCommand and friends are synchronous command
// bindings over an async DrillIntoAsync, so they hand the task to
// TaskExtensions.Forget and return immediately (see MobileMainViewModel).
// The rebuild behind them runs on the pool, and only when its continuation
// has been pumped back onto the dispatcher is _hasDrilledIn set and
// CurrentPlaylist non-null.
//
// So a test that drills in and reads the screen has to wait, and the two ways
// it was being done by hand were both wrong on a loaded runner:
//
// **A single RunJobs() is not a wait at all.** It pumps whatever happens to be
// queued right now. If the pool has not yet run the rebuild - which on a
// three-core hosted runner shared with the rest of the suite is most of the
// time - there is nothing queued to pump, and the assertion reads the screen
// the drill-in has not reached yet. That is what failed
// MobileSmartPlaylistEditorTests on macOS: CanEditCurrentPlaylist was false
// because CurrentPlaylist was still null.
//
// **Thread.Sleep between pumps blocks the thread doing the pumping.** The
// dispatcher thread is the one that has to run the continuation, so sleeping
// on it to wait for that continuation spends the budget holding it up. Await
// instead: it yields the thread back, so the continuation runs during the
// wait rather than after it.
public static class UiWait
{
    // Long enough that a machine simply being slow never fails a test, short
    // enough that something genuinely stuck still fails the job rather than
    // hanging it. Nothing healthy comes close - a drill-in over a test-sized
    // library lands in a pump or two.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    // Pumps the dispatcher until `condition` holds, then once more so anything
    // the condition becoming true posted has run too.
    public static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{because} (still false after {Budget.TotalSeconds:F0}s)");

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(PollInterval);
        }

        Dispatcher.UIThread.RunJobs();
    }

    // The same wait for anything driven by an *animation* rather than by a
    // continuation: a screen easing in, a list scrolling itself to the top.
    // Those need the real dispatcher loop, because what advances them is the
    // render timer rather than a queued job, so Until's RunJobs never moves
    // them at all.
    //
    // The pattern this replaces is a fixed Pump(500) followed by an assertion
    // about where the animation ended up. That is a clock, and a loaded macOS
    // runner outruns it: the easing was still at Y=144 on its way to 0 when
    // the assertion read it. Slicing the loop and re-checking costs nothing
    // when the animation has already landed - the common case exits on the
    // first check - and only spends the budget when something is genuinely
    // stuck.
    //
    // Synchronous, unlike Until, so it can be used from a plain [AvaloniaFact]:
    // MainLoop pumps on this thread rather than needing it yielded.
    public static void Settle(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{because} (still false after {Budget.TotalSeconds:F0}s)");

            using var slice = new CancellationTokenSource(PumpSlice);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    private static readonly TimeSpan PumpSlice = TimeSpan.FromMilliseconds(50);
}
