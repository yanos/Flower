using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

using Flower.Persistence;

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

    // Where AppDataDirectory resolves to for any test that has not pinned a
    // directory of its own. Never null, and that is the whole point: null means
    // "the real one", i.e. the developer's own ~/Library/Application Support/
    // Flower.
    //
    // PinnedDataDirectory exists so a test's stores write somewhere disposable,
    // but pinning is per-test-class and the thing being protected is a
    // process-global static, so the gap is everything that writes *outside* a
    // pinned class's lifetime:
    //
    //   - TestIoc registers a ColumnManager over a throwaway AppSettings, and
    //     any column PropertyChanged - a width change during a resize gesture is
    //     enough - schedules a fire-and-forget save 500ms later. That
    //     ColumnManager is a process-wide singleton (Ioc.Default can only be
    //     configured once), so the save lands wherever Current happens to point
    //     when the timer fires, in whatever class is running by then.
    //   - Any other fire-and-forget SaveAsync still in flight when a pinned
    //     class's Dispose has already restored Current.
    //
    // Both did happen, and the damage is not abstract: a default AppSettings
    // written over the real settings.json is a wiped library folder list, a
    // re-enabled iTunes integration and a forgotten paired server - and since
    // AtomicJsonFile keeps exactly one generation of backup, a second such write
    // takes settings.json.bak with it and there is nothing left to recover from.
    //
    // Pinning a temp directory here makes the *floor* safe rather than relying
    // on every future test remembering. Restoring Current to this value, not to
    // null, is the other half - see the Dispose of the classes that pin.
    public static string DefaultDataDirectory { get; } =
        Directory.CreateTempSubdirectory("flower-test-appdata-default").FullName;

    [ModuleInitializer]
    public static void KeepEveryTestOutOfTheRealApplicationSupportDirectory()
    {
        PlatformDataDirectory.Current = DefaultDataDirectory;
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
// A MainViewModel must always be disposed by the test that builds one. Its
// PeerSyncCoordinator owns a periodic DispatcherTimer, and a test that shortens
// MainViewModel.ContentSyncCooldown and then drops the view model leaks a 150ms
// timer onto the shared dispatcher for the rest of the run. MainViewModelHarness
// .Parts is IDisposable for this reason and every caller either scopes it with
// `using` or hands it to PinnedDataDirectory.Own; keep it that way. That is no
// longer left to discipline alone - TimerLeakGuard fails the next test in the
// collection, naming the test that started the timer.
//
// The intermittent headless-session failure that used to be documented here -
// "The calling thread cannot access this object because a different thread owns
// it", roughly 1 run in 8, inside Avalonia's HeadlessUnitTestSession - is
// fixed. It was a race over which thread gets to own Avalonia's UI dispatcher,
// and it is now won deliberately rather than left to chance. See TestAppBuilder
// for the mechanism and HeadlessSessionWarmup for the fixture that closes it.
