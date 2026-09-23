using System;
using System.Linq;
using System.Threading.Tasks;

using Flower.IosRunner;

using Foundation;

using UIKit;

namespace Flower.DeviceChecks.iOS;

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    // One line per check, then a tally, which is the line
    // scripts/ios-device-checks.sh waits for. Where the lines go - a file in
    // Documents, stdout, the screen - is RunnerTranscript's contract, shared
    // with Flower.Tests.iOS.
    private const string ResultPrefix = "FLOWER-CHECK ";
    private const string TallyPrefix = "FLOWER-CHECKS ";

    public const string TranscriptName = "flower-checks.log";

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        RunnerTranscript.Start(TranscriptName);

        // Off the UI thread: the checks block on decoding for several seconds
        // each, and a watchdog kill halfway through would look like a failing
        // check rather than a hung main thread.
        Task.Run(RunChecks);

        return true;
    }

    private static void RunChecks()
    {
        try
        {
            var results = DecodeChecks.RunAll();

            foreach (var result in results)
                RunnerTranscript.WriteLine(ResultPrefix + result);

            var failed = results.Count(result => !result.Passed);
            RunnerTranscript.WriteLine($"{TallyPrefix}{results.Count - failed} passed, {failed} failed");
        }
        catch (Exception crashed)
        {
            // A throw out here is not a failed check, it is the checks being
            // unable to run at all - a missing native library, most likely -
            // and that has to read differently from six honest failures.
            RunnerTranscript.WriteLine(crashed.ToString());
            RunnerTranscript.WriteLine($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
        }
    }
}
