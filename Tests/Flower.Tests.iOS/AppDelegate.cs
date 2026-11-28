using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Flower.IosRunner;

using Foundation;

using UIKit;

using Xunit.Runner.Common;
using Xunit.Runner.InProc.SystemConsole;
using Xunit.v3;

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

        // BrowserFontFilesTests read Flower.Web's fonts out of the source tree,
        // which this app does not carry.
        string[] excluded =
        [
            "-class-", "Flower.Tests.Browser.BrowserFontFilesTests",
        ];

        var extra = Environment.GetEnvironmentVariable("FLOWER_TEST_ARGS");
        return string.IsNullOrWhiteSpace(extra)
            ? [.. verbose, .. excluded, .. ShardArguments()]
            : [.. verbose, .. excluded, .. ShardArguments(), .. extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    // FLOWER_TEST_SHARD="2/3" runs the test classes whose names hash to the
    // second of three shards, so CI can split the suite across simulators. By
    // hash rather than in turn, so a class added moves no other class.
    private static string[] ShardArguments()
    {
        var shard = Environment.GetEnvironmentVariable("FLOWER_TEST_SHARD");
        if (string.IsNullOrWhiteSpace(shard))
            return [];

        var parts = shard.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || !int.TryParse(parts[1], out var count)
            || count < 1 || index < 1 || index > count)
            throw new ArgumentException($"FLOWER_TEST_SHARD is \"{shard}\", not \"<shard>/<count>\"");

        return [.. TestClasses()
            .Where(name => Fnv1a(name) % (uint)count == index - 1)
            .SelectMany(name => new[] { "-class", name })];
    }

    private static IEnumerable<string> TestClasses() =>
        typeof(Flower.Tests.TestSupport.PlaybackWait).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(method => method.GetCustomAttributes(inherit: true).OfType<IFactAttribute>().Any()))
            .Select(type => type.FullName!);

    // Stable across runs and runtimes, which string.GetHashCode is not.
    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
            hash = (hash ^ b) * 16777619u;
        return hash;
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
