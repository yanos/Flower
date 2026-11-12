# Crash Reporting Plan

## Status

**Sentry crash reporting is built** (November 2026), on the app and the
server, verified end to end from a running app and a running server.
What a report contains, for users: [PRIVACY.md](PRIVACY.md). Sentry
org `none-k0o`, projects `flower-app` and `flower-server`; spike protection,
IP scrubbing and server-side data scrubbing on in both. Reaches users with the
next `v*` tag. The GitHub banner (Option A, below) remains for anyone who
switches reporting off.

What is left:

- **Usage stats.** Sentry's .NET SDK now has Metrics (counters, gauges,
  distributions with attributes), which may cover the events below without
  Aptabase - to decide before building.
- **Readable stacks on iOS and Android.** Method names arrive everywhere;
  file and line only where PDBs ship beside the app (desktop, server). A
  NativeAOT or AOT phone build needs its debug files uploaded to Sentry from
  CI, which needs a Sentry auth token as a CI secret.
- **A first-run notice** that reporting is on (the switch exists; the notice
  does not).
- iOS native crashes, which nothing records (no crash-report file, no exit
  history to read).
- **Calls to `CrashReporting.ReportCaught`.** The API exists; no catch site
  uses it yet. Each one is a judgement: a catch that means Flower is wrong,
  not one that means the world is (a server down, a file gone).
- **iOS native crashes arrive late:** MetricKit delivers them up to a day
  after the crash, real devices only (never the simulator), frames as binary
  plus offset; nothing has yet been seen arriving that way.
- **The iOS Debug build (Mono) hangs on a native crash** instead of dying:
  Mono's own SIGSEGV handler takes it and spins in `mono_dump_mem`. Release
  is NativeAOT, which crashes and reports normally; a `--mono` device build
  would hang the same way.
- Windows' event-log path is unchanged and untested by a deliberate crash;
  Linux's uses the same .NET report as macOS's fallback, fixed below.

By design: a crash in a run that was not reporting (a pre-release, the
switch off) is dropped for good, never sent by a later run that is.

Native crashes on the desktop (`CrashReportScanner`, read at the next
launch, sent by `CrashReporting.CaptureNative`), verified on macOS by a
deliberate SIGSEGV on a thread .NET never saw - the situation of a fault in
the audio render thread:

- **macOS:** macOS's own report (`~/Library/Logs/DiagnosticReports/<process>-*.ips`,
  newer than the last scan, never deleted) - the fault, its address, and the
  faulting thread's frames as `library!symbol`. Preferred.
- **macOS and Linux:** .NET's crash report, written by createdump through
  `CrashReportRelaunch`. Its summary skips .NET's own signal-handler frames,
  down to the signal trampoline. The relaunch used to set a
  `DOTNET_CrashReportDirectory` the runtime does not have, so no report was
  ever written; it now enables the dump, `EnableCrashReportOnly` (the report
  without the dump) and points `DbgMiniDumpName` at `logs/crash-reports/`.
- **Windows:** the Application event log, as before.

Phones (verified by deliberate managed and native crashes on an iPhone
simulator running the NativeAOT Release build, and an Android emulator
running the Release build):

- The phone heads get Sentry's platform builds of the SDK, which bring
  native SDKs of their own (Sentry Cocoa, the Android SDK with its NDK).
  `CrashReporting.ConfigurePlatform` lets a head set their options.
- **Android:** `Native.EnableBeforeSend` routes the Android SDK's events -
  Java crashes and native (NDK) ones - through `BeforeSend`, so they are
  scrubbed, throttled and switched like the rest; its automatic breadcrumbs
  are off. Both kinds arrived clean.
- **iOS:** Sentry Cocoa is never started (`AppDelegate.KeepSentryCocoaOff`,
  through Sentry's internal `InitNativeSdks` by reflection, failing closed):
  its events cannot be routed through `BeforeSend`, and a deliberate crash
  arrived with Cocoa's own install ID, a city, a permanent device hash and
  container paths - and would have gone with the switch off. Native crashes
  come from MetricKit through `CrashReportScanner.DescribeMetricKitCrash`
  and `CaptureNative` instead.
- A managed exception on iOS's UI thread leaves through Objective-C and never
  raises `AppDomain.UnhandledException`; the head records it from
  `Runtime.MarshalManagedException` (`App.RecordFatal`).
- `CrashScrubber` also drops the phone SDKs' permanent identifiers:
  `device_app_hash` (iOS) and `device_unique_identifier` (Android).

How Sentry is wired (`Flower.Core/Logging/CrashReporting.cs`, shared by both
hosts):

- **Started** first thing after the log (`App.axaml.cs`, `Program.cs`), so a
  startup crash is caught - **in a Release build of a release version
  only** (`CrashReporting.ShouldRun`): a Debug build never starts the SDK,
  and nor does a pre-release (every untagged build, a tagged beta) unless
  `FLOWER_SEND_CRASH_REPORTS=1` forces it in a Release build, to check the
  wiring before a tag. Never in the browser, never in a test
  (`CrashReporting.DisabledForTests`, set by every test assembly).
- **Sent:** unhandled and unobserved task exceptions (the SDK's own hooks;
  see "Unhandled exceptions, every way out" below), a
  native crash found at the next launch (`CrashReporting.CaptureNative`),
  caught exceptions a call site chooses to send (`CrashReporting.ReportCaught`,
  tagged `caught_in`), and
  one trace per launch, `app.startup` (1% of launches in a release, all in a
  forced pre-release); the server's request traces at the same rate.
- **Not sent:** log lines (the server's Sentry logger has every level above
  it), breadcrumbs, sessions.
- **Anonymous:** `CrashScrubber` on every event and trace - messages lose
  paths (extension kept), URLs and quoted names; frames keep method and file
  name only; no machine or device name, boot time, location (the event names
  0.0.0.0, so Sentry infers none), request query, headers or cookies.
  Checked on the received events, not just in tests.
- **Installs counted, not identified:** `CrashReportingId`, a random GUID in
  the data directory, is the event's user, so Sentry's user count is how
  many installs a problem reaches. Deleted when the switch goes off.
- **How often, within the quota:** `CrashThrottle` (`logs/crash-throttle.txt`)
  sends each problem at most once an hour per install, keyed by release,
  exception types and innermost frames; repeats are counted and the next
  report carries them as `occurrences`. Event count is then device-hours,
  user count installs, `occurrences` the rest. (It replaced "once per device
  per version", which hid how frequent a crash was.)
- **Switch:** `AppSettings.SendCrashReports` (Settings on both heads, live;
  peeked before the full settings load) and `Flower:SendCrashReports` on the
  server (read at startup). Both on by default.
- `production` for a release, `prerelease` for a forced pre-release; the
  release is `AppVersion.Full`.
- **Module versions** are each assembly's informational version
  (`ReportAssembliesMode.InformationalVersion`: Flower's
  `0.9.1-alpha.0.22+<commit>`, FFAudio.NET's `1.0.3+<commit>`), with the
  pre-release label and commit the numeric assembly version lacks. That
  assembly version used to read `0.0.0.0` for every Flower assembly and
  `1.0.0.0` for FFAudio.NET, MinVer's `major.0.0.0`; both repos now stamp
  `major.minor.patch.0` (`Directory.Build.props`).

Unhandled exceptions, every way out (each exit point thrown at deliberately,
on the macOS head and an Android emulator; iOS's two paths on a simulator):

| Thrown from | Reaches | Our hook | Sentry |
|---|---|---|---|
| UI thread: `Dispatcher.Post`, an event handler, `async void` resuming there | Avalonia's `Dispatcher.UnhandledException`, unhandled, then `AppDomain.UnhandledException` | `App.RecordFatal` | SDK's `AppDomain` hook |
| A thread, the thread pool, `async void` resuming off the UI thread | `AppDomain.UnhandledException` | `App.RecordFatal` | SDK's `AppDomain` hook |
| A faulted task nobody awaited | `TaskScheduler.UnobservedTaskException` when the GC collects it; Flower carries on | logged as an error | SDK's `UnobservedTaskException` hook |
| Android: a Java callback (a `Handler`, a listener) | `AppDomain.UnhandledException` (the runtime raises it before handing Java a `JavaProxyThrowable`) | `App.RecordFatal` | SDK's `AndroidEnvironment` hook, and the Android SDK's own (routed through `BeforeSend`) |
| iOS: an Objective-C callback, the UI thread | `Runtime.MarshalManagedException` only; the process dies | `App.RecordFatal` (`AppDelegate`) | SDK's `MarshalManagedException` hook |
| macOS head: an AppKit callback (a menu action, an `NSTimer`, a delegate) | `Runtime.MarshalManagedException` only; AppKit swallows it and Flower carries on | `App.RecordSurvived`: logged, and `CrashReporting.ReportUnhandled` sends it unhandled, at error level | none of its own - the SDK's .NET build has no macOS hook |
| Server: a request | ASP.NET Core | logged | SDK's middleware |
| Server: a hosted service | each catches its own (`PublicReachability`, `MdnsAdvertiser`) | logged | - |

A `DispatcherTimer.RunOnce` callback that throws counts as an unobserved
task, not a crash, in Avalonia 12. Not exercised yet: the Windows and Linux
desktops' message loops, and a background thread on iOS.

How it works:

- **Capture.** A terminating managed exception is written by `App.axaml.cs`'s
  `UnhandledException` hook as a `PendingCrash` (`Flower.Core/Logging/`,
  `logs/pending-crash.json`) as the process dies. A native crash is found at
  the next launch - `CrashReportScanner` (the .NET crash report on Linux and
  macOS, the event log on Windows) or Android's exit history - and recorded
  only if no managed exception already was, since that says more.
- **Offer.** The next launch shows a banner (`CrashReportOfferViewModel`,
  `MainViewModel.CrashReport`): across the page on a desktop, atop Home on a
  phone. Its "Report on GitHub" link is a `HyperlinkButton`, so it opens the
  browser through Avalonia's launcher on every head, phones included.
  Reporting or closing forgets the crash; ignoring it asks again next launch.
- **The issue.** `IssueLink.ForCrash` prefills title (the exception), body
  (stack, build, OS, when, where the log is) and the `bug` label. The home
  folder is written as `~`; the log is not attached (a link cannot carry it),
  and the body warns it names songs and folders. GitHub's new-issue page is
  the human review step. The link is held under 5,000 characters, the top of
  the stack kept: signed out, GitHub sends the person to log in with the link
  in `return_to`, and drops `return_to` past about 5.7 KB (measured November
  2026), which would lose the report.
- **Anytime.** "Report an Issue" in the About box and at the foot of the
  phone's Settings opens `IssueLink.General` (version and OS only).

Reporting needs a GitHub account, which a listener may not have; they can
tell the owner instead, whose server already receives their device logs.

## Next: crashes and usage from every install

The goal is the developer's: every Flower install, owner's or listener's,
reports into one place, so bugs get fixed and features prioritised from
what people actually hit and use. Not a per-owner feature - nothing goes to
anyone's Flower.Server.

### Decisions (November 2026)

- **Crashes → Sentry, hosted** (a project for the app and one for the
  server, one developer seat; every install sends into them). Free plan: about 5,000 errors a month shared by all
  installs.
- **Spike protection on** in the Sentry project (a project setting, not code).
- **What is sent:** terminating managed exceptions, native crashes found at
  the next launch (`PendingCrash`, as today), **and unobserved task
  exceptions** - real bugs that surface nowhere else. Not `LogError`/
  `LogWarning` lines: Sentry is not wired as a logging provider (the
  `Sentry.Extensions.Logging` route in Option B below would send every one),
  captures are explicit.
- **At most once an hour per install** for each distinct exception (type plus
  top frames), repeats counted into the next report, and a random install ID
  so Sentry counts installs: frequency and reach both matter for what to fix
  first, and one crash loop still cannot spend the month's quota. (First
  decided as once per device per version, which hid frequency.) Measured
  before deciding: zero unobserved task exceptions in the last ten runs on the
  developer's Mac.
- **Release versions only;** pre-releases never report.
- **Crashes + light tracing**: one startup trace per launch, sampled.
- **Usage → Aptabase** (no IP, no device ID by design) - or Sentry Metrics,
  see "What is left". Its .NET SDK is
  MAUI-only; Flower gets its own small client over Aptabase's HTTP API.
- **On by default, with a notice and a switch.** One line where it is first
  seen ("Flower sends anonymous crash reports and usage stats") and a switch
  in Settings on every head. Default-on rests on the data being anonymous
  (below); a listener's phone is still asked nothing. Declared in the App
  Store privacy label and Play data safety form as diagnostics and usage, not
  linked to identity.
- The GitHub banner stays, as the manual route for anyone who switches
  reporting off.

### Anonymous, and how

- No account, server or device ID - only a random install ID that counts
  installs and is deleted with the switch; Sentry with
  `SendDefaultPii = false` and IP storage off in the project.
- **Crash contents stripped:** exception type and each frame's method name.
  No file paths, no source paths, and no exception message unless its type is
  on an allow-list - a message is where a song title or folder name leaks
  ("could not open …/Secret Album/01.flac"). `IssueLink.Redact`'s home-folder
  rule as a backstop.
- **Usage as flags and ranges only**, never a name or a title.

### Usage events (proposed - to review before any code)

- **Per launch:** version, OS, head (desktop, macOS, iOS, Android, browser),
  AOT or Mono, role (unpaired, owner, listener).
- **Library:** song count in ranges (<1k, 1k-10k, 10k-50k, more), playlist and
  smart-playlist counts in ranges, share lossless and hi-res.
- **Settings snapshot:** EQ on, theme, update checks, vibe playlists, output
  sample format the device settled on.
- **Feature use:** vibe playlist created, download, AirPlay, search, pairing,
  Music.app import answer.
- **Health:** render watchdog trips, decode failures by codec, startup and
  scan time in ranges.

Survey of options for surfacing crashes, layered on top of the existing file logging (`Flower/Logging/AppLogging.cs`, `App.axaml.cs`'s unhandled-exception hooks) — these options are about getting that data out, not duplicating capture that already exists.

## Key findings

- App Center is confirmed dead (retired March 2025) — ruled out.
- **Sentry** has no Avalonia integration of its own, but its core .NET package works with one: initialise it early and hand it exceptions (its maintainers' advice). NativeAOT on iOS and Android since SDK 4; SDK 6 targets .NET 10. Its `Sentry.Extensions.Logging` package could plug into the `ILoggingBuilder` call in `App.axaml.cs`, but that sends every error log line, which "Next" above rules out.
- **GlitchTip** is a real self-hosted alternative: speaks the same Sentry wire protocol, so identical NuGet packages work unchanged — just a different DSN, and a smaller self-hosted footprint (4 components vs. self-hosted Sentry's dozen-plus).
- A zero-infrastructure option: GitHub supports pre-filling a new issue via URL query params — no token/server/account needed, and directly satisfies `docs/todo.txt`'s "make an issue link" item.
- Privacy note: stack traces/logs can contain real filesystem paths — keep a human-review step (issue draft, or a confirm dialog) regardless of which option is chosen.

## Option A: GitHub issue link (recommended starting point)

On an unhandled exception, show a "Flower crashed" dialog with a "Report on GitHub" button that opens a prefilled `.../issues/new?title=...&body=...&labels=crash` URL (through `PlatformShell.OpenUrl`, hidden where `PlatformShell.IsAvailable` is false), including the current log file path. Effort: Small. Risk: Low. Limitation: manual per-crash, no aggregation across users.

## Option B: Sentry (once aggregation is worth it)

Add `Sentry`/`Sentry.Extensions.Logging`, extend `.AddLogging(...)` with `.AddSentry(o => o.Dsn = "...")` (public DSNs are meant to be embedded client-side). Everything already flowing through `ILogger` is automatically captured, no extra instrumentation. Effort: Small. Risk: Low technically; trade-off is third-party SaaS + free-tier quota.

## Option C: GlitchTip, self-hosted (privacy middle ground)

Identical code to Option B, pointed at a self-hosted GlitchTip DSN instead. Only worth the hosting cost once keeping crash data off a third party is an actual requirement. Effort: Small code-wise, Medium operationally.

## Mobile note

Sentry's .NET SDK (and GlitchTip) already has mobile-capable support, so the same backend covers `Flower.iOS`/`Flower.Android` once there's a real mobile UI to crash — no second native-per-store tool needed.

## Recommendation

Start with Option A (zero-cost, satisfies `docs/todo.txt`). Layer in Option B once crash volume makes manual log-reading too slow. Reach for Option C only if third-party data handling becomes a real constraint.

