using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace Flower.DeviceChecks.Android;

// Name is pinned rather than left to the generated crc64 one: the driver
// script has to name this activity to `am start` it with the extra below,
// and a generated name changes whenever the namespace does.
[Activity(Name = "com.yanos.flower.devicechecks.MainActivity",
          Label = "Flower checks", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    // The output contract, in three places at once because each is the only
    // one that works somewhere:
    //
    //  - a file in the app's private files directory, which is what
    //    scripts/android-device-checks.sh reads back through `run-as`. It is
    //    the reliable one: logcat is a ring buffer shared with the whole
    //    system, so a long transcript competing with a chatty emulator can
    //    lose lines, and a run that decodes correctly but reports half its
    //    tally is indistinguishable from a failing one.
    //  - logcat, which is what a run watched live shows.
    //  - the screen, so a run on a phone with no cable attached is readable
    //    by the person holding it.
    //
    // Deliberately the same three, under the same prefixes, as
    // Flower.DeviceChecks.iOS's AppDelegate: the two runs are only comparable
    // if a reader can compare them line for line.
    private const string ResultPrefix = "FLOWER-CHECK ";
    private const string TallyPrefix = "FLOWER-CHECKS ";
    private const string LogTag = "FlowerChecks";

    public const string TranscriptName = "flower-checks.log";

    private TextView? _log;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _log = new TextView(this)
        {
            Text = "Running...",
            Typeface = global::Android.Graphics.Typeface.Monospace,
            TextSize = 9f,
        };

        var scroller = new ScrollView(this);
        scroller.AddView(_log);
        SetContentView(scroller, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        // Android launches an activity, not a process with an environment, so
        // the one knob the checks take arrives as an intent extra and is put
        // back where DecodeChecks looks for it. The driver script passes it
        // with `--es`; a run started by tapping the icon simply has none, and
        // then a missing façade reports what this phone can decode rather
        // than failing for what nobody built.
        if (Intent?.GetStringExtra("FLOWER_REQUIRE_DECODERS") is { Length: > 0 } required)
            System.Environment.SetEnvironmentVariable("FLOWER_REQUIRE_DECODERS", required);

        // Off the UI thread: the checks block on decoding for several seconds
        // each, and an ANR kill halfway through would look like a failing
        // check rather than a blocked main thread.
        Task.Run(RunChecks);
    }

    private string TranscriptPath => Path.Combine(FilesDir!.AbsolutePath, TranscriptName);

    private void RunChecks()
    {
        var transcript = new StringBuilder();

        void Say(string line)
        {
            global::Android.Util.Log.Info(LogTag, line);
            transcript.AppendLine(line);

            var snapshot = transcript.ToString();

            // Rewritten whole each time rather than appended: the script may
            // read it at any moment, and a partial line would be read as a
            // missing tally.
            try
            {
                File.WriteAllText(TranscriptPath, snapshot);
            }
            catch (Exception unwritable)
            {
                global::Android.Util.Log.Warn(LogTag, $"could not write the transcript: {unwritable.Message}");
            }

            RunOnUiThread(() => _log!.Text = snapshot);
        }

        try
        {
            var results = DecodeChecks.RunAll();

            foreach (var result in results)
                Say(ResultPrefix + result);

            var failed = results.Count(result => !result.Passed);
            Say($"{TallyPrefix}{results.Count - failed} passed, {failed} failed");
        }
        catch (Exception crashed)
        {
            // A throw out here is not a failed check, it is the checks being
            // unable to run at all - a missing native library, most likely -
            // and that has to read differently from six honest failures.
            Say(crashed.ToString());
            Say($"{TallyPrefix}0 passed, 1 failed (the run itself threw)");
        }
    }
}
