using Avalonia;
using Avalonia.Headless;

using Flower.Tests.TestSupport;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// One Application and one Dispatcher for the whole assembly, rather than the
// default of tearing both down and rebuilding them around every single
// [AvaloniaFact].
//
// This, together with HeadlessSessionWarmup, is what fixed the intermittent
// failure this suite had for a long time - roughly 1 run in 8, always "The
// calling thread cannot access this object because a different thread owns it"
// thrown out of Avalonia's own HeadlessUnitTestSession, never an assertion
// failure in the code under test. The mechanism, once the stack trace was
// finally captured, is a race over a single global:
//
//   - Dispatcher.UIThread returns Dispatcher.CurrentDispatcher when Avalonia's
//     global s_uiThread field is null, and the Dispatcher constructor does
//     "s_uiThread ??= this". So whichever thread asks first while nothing owns
//     the UI thread becomes its owner, permanently.
//   - The next application setup builds the compositor's render loop against
//     whatever that field now points at, and VerifyAccess throws. That is
//     exactly the stack: DefaultRenderLoop.Add <- ServerCompositor..ctor <-
//     AvaloniaHeadlessPlatform.Initialize <- AppBuilder.SetupUnsafe.
//   - Tests run in parallel and plenty of them are plain [Fact]s that touch
//     view models, so there is no shortage of threads able to ask first.
//
// PerTest isolation loses that race occasionally because it re-opens it
// constantly: it nulls s_uiThread before every single test, so each of the
// thousand-odd tests is another roll of the dice. Switching to PerAssembly
// alone is not enough and is in fact worse on its own - the window shrinks to
// one, but losing that one poisons every [AvaloniaFact] in the run rather than
// one of them. The window has to be not just narrowed but won, which is what
// the warmup fixture does.
//
// This also settles the two things tried before and recorded as not working:
// disabling collection parallelization (the thief is a background thread, not
// a parallel test) and closing a leaked 150ms DispatcherTimer (fewer threads
// asking, same window - it went from 1-in-3 to 1-in-8, which is what "fewer
// dice rolls" looks like). Chasing individual callers was never going to close
// it; every legitimate Dispatcher.UIThread.Post in the app is a candidate.
//
// The trade is real and worth stating exactly. PerTest wrapped each test in an
// AvaloniaLocator scope, so Application.Current (it is just a locator lookup),
// the FontManager, the ToolTipService and the Dispatcher were all rebuilt per
// test; here they are built once. In particular Dispatcher._timers is an
// instance field, so a leaked DispatcherTimer used to die with its test's
// dispatcher and now ticks for the rest of the run - which is why disposing a
// MainViewModel is an invariant and not just good manners (see AssemblySetup).
// TimerLeakGuard enforces that invariant rather than trusting it.
//
// What it does not trade away is isolation that this suite actually had.
// AvaloniaTestRunner only wraps [AvaloniaFact]/[AvaloniaTheory]; the plain
// [Fact]s that make up most of this suite never entered a session at all, and
// they run in parallel with the ones that do. EnterScope swaps a *global*
// current locator, so a plain [Fact] touching a view model was already reading
// whichever Application happened to exist mid-teardown. The change is from
// unstable shared state to stable shared state, not from isolated to shared.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Flower.Tests.TestSupport;

// Gives [AvaloniaFact] tests a real Dispatcher.UIThread to run against,
// headlessly - no window/rendering needed, just enough of an Avalonia app
// for Dispatcher.UIThread.Post (used by PlaylistControlViewModel's
// EndReached handler) to actually execute instead of throwing/no-op'ing
// without one.
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
