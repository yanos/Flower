# Crash Reporting Plan

## Status

**Option A is built** (November 2026). What is left: Option B, if crash
volume ever makes reading issues one at a time too slow; iOS native crashes,
which nothing records (no crash-report file, no exit history to read).

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

Survey of options for surfacing crashes, layered on top of the existing file logging (`Flower/Logging/AppLogging.cs`, `App.axaml.cs`'s unhandled-exception hooks) — these options are about getting that data out, not duplicating capture that already exists.

## Key findings

- App Center is confirmed dead (retired March 2025) — ruled out.
- **Sentry** has confirmed, current Avalonia support. Its `Sentry.Extensions.Logging` package plugs directly into the same `ILoggingBuilder` call already in `App.axaml.cs` as a second provider alongside the file sink — zero changes needed at existing `ILogger` call sites.
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

