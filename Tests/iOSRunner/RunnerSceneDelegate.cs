using Foundation;

using UIKit;

namespace Flower.IosRunner;

// The runner's one screen: the transcript, as it grows.
//
// A scene delegate rather than a window made in FinishedLaunching because
// iOS 27 enforces the scene lifecycle: an app that does not adopt it is killed
// at launch with EXC_BREAKPOINT in
// UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption, before a line
// of the run is written - which reads exactly like a runner that hung. Each
// runner's Info.plist names this class in UIApplicationSceneManifest.
//
// The run itself does not wait for this: it starts from FinishedLaunching, so
// a scene that never connects costs the screen and nothing else.
[Register("RunnerSceneDelegate")]
public class RunnerSceneDelegate : UIWindowSceneDelegate
{
    private UITextView? _log;
    private int _refreshPending;

    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;

        Window = new UIWindow(windowScene);

        _log = new UITextView(Window.Bounds)
        {
            Editable = false,
            Font = UIFont.FromName("Menlo", 11) ?? UIFont.SystemFontOfSize(11),
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            Text = "Running...",
        };

        var root = new UIViewController();
        root.View!.AddSubview(_log);
        Window.RootViewController = root;
        Window.MakeKeyAndVisible();

        RunnerTranscript.Changed += _ => ScheduleRefresh();
        ScheduleRefresh();
    }

    // Coalesced: a verbose test run writes thousands of lines, and setting the
    // whole text once per line would spend the main thread on layout.
    private void ScheduleRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;

        _log?.BeginInvokeOnMainThread(() =>
        {
            System.Threading.Interlocked.Exchange(ref _refreshPending, 0);

            var text = RunnerTranscript.Snapshot;
            if (text.Length > 0)
                _log.Text = text;
        });
    }
}
