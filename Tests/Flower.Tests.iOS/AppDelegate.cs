using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.IosRunner;

using Foundation;

using UIKit;

using Xunit.Runner.Common;
using Xunit.Runner.InProc.SystemConsole;

namespace Flower.Tests.iOS;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public const string TranscriptName = "flower-tests.log";

    // The last line of every run, and the only one scripts/ios-tests.sh acts on:
    // "FLOWER-TESTS exit 0" is a pass, any other code is a failure, and no such
    // line means the run never finished.
    private const string TallyPrefix = "FLOWER-TESTS ";

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        RunnerTranscript.Start(TranscriptName);

        // Off the main thread: the suite blocks for minutes, and a watchdog
        // kill partway through would look like a failing test rather than a
        // hung main thread.
        Task.Run(RunTests);

        return true;
    }

    private static async Task RunTests()
    {
        int exit;
        try
        {
            exit = await Run(Arguments());
        }
        catch (Exception crashed)
        {
            // A throw out here is not a failing test, it is the suite being
            // unable to run at all, and has to read differently from one.
            RunnerTranscript.WriteLine(crashed.ToString());
            exit = 99;
        }

        RunnerTranscript.Writer.Flush();
        RunnerTranscript.WriteLine($"{TallyPrefix}exit {exit}");
    }

    // xunit command-line arguments: the fixed ones below, then whatever
    // FLOWER_TEST_ARGS adds (a -class, a -method).
    private static string[] Arguments()
    {
        // Every test's start and finish, always, so the transcript of a run that
        // hangs names the test that never came back. A quiet reporter prints
        // nothing until the end, and a run that stopped reporting is then all
        // there is: this has hung once, and that transcript was three lines.
        // scripts/ios-tests.sh prints the failures and the summary of a
        // finished run, and the unfinished tests of a stuck one.
        string[] verbose = ["-verbose"];

        // MusicListViewGestureTests drive a real headless Avalonia window through
        // full layout and Skia render per gesture, and under the simulator's
        // interpreter that took 1116s of a 1312s run - one to three minutes a
        // test, against well under a second on the desktop. They pass here; they
        // are left out until they can run in a time a push can afford.
        string[] excluded = ["-class-", "Flower.Tests.MusicListViewGestureTests"];

        var extra = Environment.GetEnvironmentVariable("FLOWER_TEST_ARGS");
        return string.IsNullOrWhiteSpace(extra)
            ? [.. verbose, .. excluded]
            : [.. verbose, .. excluded, .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    // ConsoleRunner.EntryPoint - what xunit's generated Main calls - minus the
    // parts iOS refuses. It subscribes to Console.CancelKeyPress
    // unconditionally, which throws PlatformNotSupportedException here, and
    // reads Console.In unless handed a reader. Everything that actually
    // discovers and runs tests is these public pieces, called the same way.
    private static async Task<int> Run(string[] args)
    {
        var assembly = typeof(Flower.Tests.TestSupport.PlaybackWait).Assembly;
        var console = new ConsoleHelper(TextReader.Null, RunnerTranscript.Writer);
        var projectAssembly = new CommandLine(console, assembly, args).Parse();

        // What ConsoleRunner defaults it to, for the same reason: a console
        // runner reports theories as one result each, not one per data row.
        projectAssembly.Configuration.PreEnumerateTheories ??= false;

        var logger = new ConsoleRunnerLogger(useColors: false, useAnsiColor: false, console, waitForAcknowledgment: false);
        var diagnostics = ConsoleDiagnosticMessageSink.TryCreate(console, noColor: true, showDiagnosticMessages: false, showInternalDiagnosticMessages: false, assemblyDisplayName: "Flower.Tests");
        var startup = await ProjectAssemblyRunner.InvokePipelineStartup(assembly, diagnostics);
        var reporter = await projectAssembly.Project.RunnerReporter.CreateMessageHandler(logger, diagnostics);

        try
        {
            var runner = new ProjectAssemblyRunner(assembly, AutomatedMode.Off, new CancellationTokenSource());
            return await runner.Run(projectAssembly, reporter, diagnostics, logger, startup);
        }
        finally
        {
            if (startup is not null)
                await startup.StopAsync();

            await reporter.DisposeAsync();
        }
    }
}
