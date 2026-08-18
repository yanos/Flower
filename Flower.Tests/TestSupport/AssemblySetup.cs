using System.Runtime.CompilerServices;
using System.Threading;

namespace Flower.Tests.TestSupport;

// xUnit runs test collections in parallel by default, and several of them
// (GaplessCoordinatorTests, GaplessAudioManagerTests, TrackDecoderTests,
// GaplessCoordinatorRealDecodeTests, ...) drive their own async/callback-
// driven code via SpinWait.SpinUntil - a busy-wait on the calling thread,
// not a real yield. With enough of those spinning concurrently, the CLR
// ThreadPool's default slow thread-injection rate (about one new thread per
// several hundred ms once the pool is saturated) can leave genuinely async
// continuations elsewhere in the same process - a Task.Run, an
// HttpListener.GetContextAsync() continuation, a LibVLC Media.Parse's async
// completion - waiting tens of seconds for a worker thread that never comes
// available in time, even though the actual work behind them takes
// milliseconds. Confirmed directly: StreamingNetworkOutageTests' real-
// HTTP-server tests passed in isolation every time but failed/hung for 45s+
// only when run as part of the full suite, and disappeared entirely once
// this raised the minimum pool size - full-suite runtime also dropped from
// up to 48s to about 1s in the same before/after comparison, meaning this
// starvation was silently inflating every test's wall-clock time, not just
// the ones that outright failed.
internal static class AssemblySetup
{
    [ModuleInitializer]
    public static void RaiseThreadPoolMinimumToAvoidStarvationUnderParallelTestExecution()
    {
        ThreadPool.SetMinThreads(100, 100);
    }
}

// ── A note on Dispatcher.UIThread.MainLoop in tests ──────────────────────────
//
// A DispatcherTimer cannot be advanced in headless any other way: RunJobs()
// drains the dispatcher queue but never the timer queue, and neither
// AvaloniaHeadlessPlatform.ForceRenderTimerTick() nor any DispatcherPriority
// makes one tick (all measured directly). The only thing that works is running
// the real loop - Dispatcher.UIThread.MainLoop(cts.Token). ScreenStackPanelSwipeTests
// (the 280ms commit easing) and TrackRowViewModelTests (the download spinner)
// both need it.
//
// Prefer Thread.Sleep + RunJobs() wherever a plain Dispatcher.Post is all that
// is being waited on - LogViewModelTests and MainViewModelSyncTriggerTests both
// do. Only reach for MainLoop when a DispatcherTimer is genuinely what is under
// test.
//
// KNOWN ISSUE, not yet solved: this suite fails intermittently - roughly 1 run
// in 8 - inside Avalonia's own headless session setup
// (HeadlessUnitTestSession.EnsureIsolatedApplication ->
// "The calling thread cannot access this object because a different thread owns
// it"). It surfaces as an unrelated [AvaloniaFact] failing, most often one of
// CompositionRootTests, and is never an assertion failure in the code under
// test. Measured findings, so the next person does not repeat them:
//   - Disabling collection parallelization does NOT fix it (8 full runs).
//   - Excluding the two MainLoop suites does NOT fix it either (1 in 8, same as
//     with them). MainLoop makes it much worse when combined with a leaked
//     short-interval DispatcherTimer (see below), but is not the whole story.
//   - What DID take it from about 1 run in 3 down to 1 in 8: not letting a
//     MainViewModel be constructed while MainViewModel.ContentSyncCooldown is
//     shortened. Its constructor starts a periodic _logPushTimer at that
//     interval and never stops it, so such a MainViewModel leaks a 150ms
//     DispatcherTimer onto the shared dispatcher for the rest of the run. See
//     MainViewModelSyncTriggerTests.ShortCooldown.
// The leaked _logPushTimer (MainViewModel has no Dispose) is the most promising
// thread to pull on next.
