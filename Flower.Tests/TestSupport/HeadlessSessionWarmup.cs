using System;
using System.Threading;

using Avalonia.Headless;

using Xunit;

[assembly: AssemblyFixture(typeof(Flower.Tests.TestSupport.HeadlessSessionWarmup))]

namespace Flower.Tests.TestSupport;

// Claims Avalonia's UI thread for the headless session's own thread before any
// test can race it for the claim. Without this, [AvaloniaFact] fails
// intermittently across the whole suite; the mechanism is written up in
// TestAppBuilder, and the short version is that whichever thread first asks for
// Dispatcher.UIThread while nothing owns it becomes the owner for good.
//
// An assembly fixture rather than a [ModuleInitializer] because the two run at
// very different times. Module initializers fire the moment the assembly is
// touched, which includes the short-lived process xUnit v3 launches purely to
// read assembly info - building an Avalonia application in there leaves it
// alive past the runner's 60-second patience and the whole run is reported as
// "no tests matched". An assembly fixture is constructed at execution time,
// after discovery and before the first test collection, which is exactly the
// window this needs.
//
// Dispatching a single empty action is the whole job: it is what drives
// HeadlessUnitTestSession's application setup onto its dispatcher thread, and
// with PerAssembly isolation that application - and its dispatcher - is then
// the one every test sees.
//
// Disposal is the backstop half of the DispatcherTimer leak guard: see
// AssertNoFlowerTimerOutlivedTheRun.
public sealed class HeadlessSessionWarmup : IDisposable
{
    private readonly HeadlessUnitTestSession _session;

    public HeadlessSessionWarmup()
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSessionWarmup).Assembly);
        _session.Dispatch(() => { }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => AssertNoFlowerTimerOutlivedTheRun();

    // Catches a leaked timer that TimerLeakGuard could not: one started by the
    // last test of its collection, which has no following test to fail.
    //
    // This is the backstop and not the main event because of how badly it
    // reports. An assembly fixture's disposal sits outside xUnit's test-result
    // model, so:
    //
    //   - `dotnet test` exits NON-ZERO. That is the signal, and it is what CI
    //     reads. Trust the exit code, not the summary.
    //   - The summary line still says "Passed!  - Failed: 0". It is not lying
    //     about the tests; there is no test to attribute this to.
    //   - "[Test Assembly Cleanup Failure ...]" prints just above the summary.
    //     That line is the only human-visible tell.
    //   - Neither the message nor the exception type reaches the console - xUnit
    //     wraps both in a TestPipelineException. Writing to Console.Out or
    //     Console.Error from here was tried and measured: both go nowhere. To
    //     read what leaked:
    //
    //       dotnet test Flower.Tests/Flower.Tests.csproj --logger "console;verbosity=detailed"
    //
    // That mute channel is the entire reason TimerLeakGuard exists as a per-test
    // hook, where the same finding surfaces as an ordinary named test failure.
    private void AssertNoFlowerTimerOutlivedTheRun()
    {
        string? leaked;
        try
        {
            // On the dispatcher thread, because that is the thread that owns
            // the timer list and a leaked timer may still be mutating it.
            leaked = _session.Dispatch(TimerLeakGuardAttribute.DescribeSurvivors, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return;
        }

        if (leaked is not null)
            throw new InvalidOperationException(leaked);
    }
}
