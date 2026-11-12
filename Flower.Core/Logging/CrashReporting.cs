using System;
using System.IO;

using Flower.Services;

using Sentry;

namespace Flower.Logging
{
    // Crashes from every Flower install, into the developer's Sentry - see
    // docs/CRASH-REPORTING-PLAN.md for how, docs/PRIVACY.md for what. One
    // setup shared by the app and the server, each with its own project (DSN).
    //
    // Anonymous: no personal data from the SDK, no breadcrumbs, and every
    // event scrubbed by CrashScrubber before it leaves. The only identity is
    // CrashReportingId's random one, which lets Sentry count installs. How
    // often a problem happens is counted by CrashThrottle, which sends it at
    // most once an hour per device and carries the rest as a count.
    //
    // Runs only in a release: a Debug build never starts the SDK, and nor
    // does a pre-release version - every untagged build, and a tagged beta -
    // unless FLOWER_SEND_CRASH_REPORTS=1 forces it in a Release build, to
    // check the wiring before a tag (its events are filed under the
    // "prerelease" environment). Never in the browser, never in a test.
    //
    // The switch (Settings, or Flower:SendCrashReports on the server) is read
    // on every event rather than at startup, so turning reporting off drops
    // everything from that moment on, with no restart.
    public static class CrashReporting
    {
        public const string AppDsn = "https://be3a13e25a158b08ff277ce9f1bc90fe@o4512418960834560.ingest.us.sentry.io/4512418976038912";
        public const string ServerDsn = "https://f442cf89158896fab7dc205e8a827a54@o4512418960834560.ingest.us.sentry.io/4512418976104448";

        public const string ForceVariable = "FLOWER_SEND_CRASH_REPORTS";

        // One in a hundred operations traced in a release, every one in a
        // pre-release forced on, where a trace is wanted to check the wiring.
        public const double ReleaseTraceSampleRate = 0.01;

        private static volatile bool _enabled = true;

        // Set by every test assembly's module initializer. A test that boots
        // App or the server would otherwise report its deliberate failures.
        public static bool DisabledForTests { get; set; }

        // A phone head's own options, which need the platform build of the
        // SDK to name: Android routes its native SDK's events through
        // BeforeSend (MainActivity), iOS does not start its native SDK at all
        // (AppDelegate). Set before App starts.
        public static Action<SentryOptions>? ConfigurePlatform { get; set; }

        // Whether this process started the SDK at all - see ShouldRun.
        public static bool Running { get; private set; }

        public static bool Enabled
        {
            get => _enabled && !DisabledForTests;
            set
            {
                _enabled = value;
                if (!value)
                    CrashReportingId.Forget();
            }
        }

        public static bool IsDevelopmentBuild =>
#if DEBUG
            true;
#else
            false;
#endif

        // Where an event caught as the process dies waits to be sent by the
        // next launch.
        public static string CacheDirectory => Path.Combine(AppLogging.LogsDirectory, "sentry");

        // A Release build of a release, or of a pre-release when forced;
        // never a Debug build, a test or the browser.
        public static bool ShouldRun(string version) =>
            !IsDevelopmentBuild
            && !DisabledForTests
            && !System.OperatingSystem.IsBrowser()
            && (!AppVersion.IsPrerelease(version) || Environment.GetEnvironmentVariable(ForceVariable) == "1");

        // The options both hosts share; the server adds its ASP.NET Core ones
        // on top. Never a logging provider: a log line is not a crash, and
        // log lines are where song and folder names live.
        public static void Configure(SentryOptions options, string dsn, string release)
        {
            options.Dsn = dsn;
            options.Release = release;
            var prerelease = AppVersion.IsPrerelease(release);
            options.Environment = prerelease ? "prerelease" : "production";
            options.SendDefaultPii = false;
            options.AutoSessionTracking = false;
            options.MaxBreadcrumbs = 0;
            options.TracesSampleRate = prerelease ? 1.0 : ReleaseTraceSampleRate;
            // Each loaded assembly's informational version - Flower's own
            // "0.9.1-alpha.0.22+<commit>", FFAudio.NET's "1.0.3+<commit>" -
            // rather than its assembly version, which has only the numbers.
            options.ReportAssembliesMode = ReportAssembliesMode.InformationalVersion;
            options.CacheDirectoryPath = CacheDirectory;
            options.SetBeforeSend(BeforeSend);
            options.SetBeforeSendTransaction(BeforeSendTransaction);
            ConfigurePlatform?.Invoke(options);
            Running = true;
        }

        public static IDisposable? StartForApp(string release)
        {
            if (!ShouldRun(release))
                return null;

            try
            {
                return SentrySdk.Init(options =>
                {
                    Configure(options, AppDsn, release);
                    options.IsGlobalModeEnabled = true;
                });
            }
            catch
            {
                // Crash reporting failing to start must never stop the app.
                Running = false;
                return null;
            }
        }

        // An exception caught and handled, sent because it is a bug all the
        // same - the place to use it is where a catch logs an error that
        // means Flower did something wrong, not where it means the world did
        // (a server that is down, a file that is gone). `where` names the
        // place in a word or two ("library scan"), and becomes a tag to
        // filter by. Scrubbed and throttled like a crash, and nothing at all
        // where the SDK is not running.
        public static void ReportCaught(Exception exception, string where)
        {
            if (!Running || !Enabled)
                return;

            SentrySdk.CaptureException(exception, scope =>
            {
                scope.Level = SentryLevel.Error;
                scope.SetTag("caught_in", where);
            });
        }

        // A crash only found at the next launch - native, so no exception
        // object to send - as an event of its own. Its frames are text.
        public static void CaptureNative(PendingCrash crash)
        {
            if (!Running || !Enabled)
                return;

            var sentryEvent = new SentryEvent
            {
                Level = SentryLevel.Fatal,
                Message = new SentryMessage { Formatted = crash.Summary },
            };
            sentryEvent.SetExtra("crashed_at", crash.When.ToString("O"));
            sentryEvent.SetTag("crash.kind", "native");
            sentryEvent.SetExtra("frames", crash.Details);
            sentryEvent.SetFingerprint(new[] { "native", crash.Summary });
            SentrySdk.CaptureEvent(sentryEvent);
        }

        private static SentryEvent? BeforeSend(SentryEvent sentryEvent, SentryHint hint)
        {
            if (!Enabled)
                return null;

            CrashScrubber.Scrub(sentryEvent, HomeDirectory, CrashReportingId.Current);
            var occurrences = CrashThrottle.Admit(sentryEvent, DateTimeOffset.UtcNow);
            if (occurrences == 0)
                return null;

            sentryEvent.SetExtra("occurrences", occurrences);
            return sentryEvent;
        }

        private static SentryTransaction? BeforeSendTransaction(SentryTransaction transaction, SentryHint hint)
        {
            if (!Enabled)
                return null;

            CrashScrubber.Scrub(transaction, HomeDirectory, CrashReportingId.Current);
            return transaction;
        }

        private static string? HomeDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home) ? null : home;
            }
        }
    }
}
