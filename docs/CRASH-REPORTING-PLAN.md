# Crash Reporting Plan

Survey of the realistic options for getting crash/error reports out of a
user's machine and in front of the developer, from zero-infrastructure to
full SaaS, plus a recommendation. Builds directly on the file logging just
added (`Flower/Logging/AppLogging.cs`, `App.axaml.cs`'s
`AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`
hooks) — every option below is about *getting that data out*, not
duplicating the capture that already exists.

## Research summary

- **Visual Studio App Center is dead — ruled out immediately.** Retired
  March 31, 2025; even its Analytics/Diagnostics extension's support window
  ended June 30, 2026. Not a viable option at all today, despite historically
  being a common answer for exactly this.
- **[Sentry](https://docs.sentry.io/platforms/dotnet/guides/extensions-logging/)
  has confirmed, current Avalonia support** (including with `PublishAot`
  enabled — an early `DllNotFoundException` issue with `sentry-native` on
  WPF/WinForms/Avalonia has been fixed). Its `Sentry.Extensions.Logging`
  package adds an `.AddSentry()` extension directly onto
  `Microsoft.Extensions.Logging`'s `ILoggingBuilder` — meaning it drops
  straight into the exact line already in `App.axaml.cs`
  (`.AddLogging(builder => builder.AddSerilog())`) as a second provider
  alongside the file sink, with zero changes to any of the `ILogger` call
  sites added this session. Sentry's SDK also auto-hooks unhandled/unobserved
  exceptions itself, redundant with (not conflicting with) the hooks already
  in `App.axaml.cs`.
- **[GlitchTip](https://glitchtip.com/) is a real self-hosted middle
  ground**, not just a toy alternative: it speaks the same Sentry wire
  protocol, so the identical `Sentry`/`Sentry.Extensions.Logging` NuGet
  packages work unchanged against it — the only difference is which DSN
  they point at. Needs 4 self-hosted components (vs. self-hosted Sentry's
  dozen-plus, including Kafka/Clickhouse), a meaningfully smaller
  operational lift if avoiding a third-party SaaS matters.
- **A GitHub-native, zero-infrastructure option exists and directly matches
  this project's workflow**: GitHub supports pre-filling a new issue via URL
  query params (`https://github.com/OWNER/REPO/issues/new?title=...&body=...&labels=...`).
  On a crash, open that URL (with the exception + relevant log excerpt
  pre-filled into `body`) in the user's default browser. No token, no
  server, no third-party account, and the user reviews/edits before
  actually submitting — built-in consent. This is also exactly what
  `docs/todo.txt`'s "make an issue link" item is already asking for.
- **Privacy note specific to Flower:** stack traces and log lines can
  contain real filesystem paths (e.g. `/Users/yanos/Music/...`), which is
  mildly personal data. Worth keeping a human-reviewed step (the GitHub
  issue draft, or a "review before sending" dialog for Sentry/GlitchTip)
  regardless of which option is chosen, rather than fully silent
  auto-submission.

---

## Option A: GitHub issue link (recommended starting point)

**Plan:**
1. On an unhandled exception (the existing `App.axaml.cs` hooks already log
   it via `ILogger`), show a small "Flower crashed" dialog with the
   exception summary and a "Report on GitHub" button.
2. That button opens
   `https://github.com/yanos/Flower/issues/new?title=Crash%3A+{exception
   type}&body={exception message + stack trace, URL-encoded}&labels=crash`
   in the default browser via `Process.Start` (same per-OS pattern
   `MainViewModel.OpenAppDataLocation` already uses).
3. Also surface (or directly append to the prefilled body) the path to the
   current run's log file, so the user can attach it manually if they choose.

**Effort:** Small. **Risk:** Low — no dependency, no account, no ongoing cost.
**Limitation:** manual per-crash, no aggregation/deduplication across users,
requires the user to be online and willing to click through.

---

## Option B: Sentry (recommended once automatic aggregation is worth it)

**Plan:**
1. Add `Sentry` + `Sentry.Extensions.Logging`, extend the existing
   `.AddLogging(builder => builder.AddSerilog())` call in `App.axaml.cs` to
   `.AddLogging(builder => builder.AddSerilog().AddSentry(o => o.Dsn =
   "..."))`.
2. Public DSNs are meant to be embedded client-side (that's Sentry's design
   — it's not a secret), so this can just live in source.
3. Everything already flowing through `ILogger.LogWarning`/`LogError`/
   `LogCritical` this session (the persistence-store failures,
   `ITunesPlayCountImporter` parse failures, the two unhandled-exception
   hooks) is automatically also captured by Sentry — no additional
   instrumentation needed at the call sites.

**Effort:** Small (given the `ILogger` plumbing already exists). **Risk:**
Low technically; the trade-off is sending data to a third-party SaaS and
Sentry's free-tier event quota.

---

## Option C: GlitchTip, self-hosted (privacy middle ground)

**Plan:** identical code to Option B — same NuGet packages, same
`AddSentry()` call — pointed at a self-hosted GlitchTip instance's DSN
instead of Sentry's cloud. Only worth the operational cost (running and
maintaining the 4-component stack) if keeping crash data off a third-party
service becomes an actual requirement, e.g. once Flower has users beyond
the developer.

**Effort:** Small code-wise, Medium operationally (hosting). **Risk:** Low.

---

## Mobile note

If/when `MOBILE-PLAN.md` Phase 3 (real mobile UI) ships, Sentry's .NET SDK
(and therefore GlitchTip, same protocol) already has mobile-capable support
— the same backend chosen above would just work there too, rather than
needing a second, native-per-store tool (Xcode Organizer crash logs, Android
vitals) that wouldn't unify with desktop reporting anyway. Not worth setting
up before there's a real mobile UI to crash.

---

## Recommendation

Start with **Option A** — it's genuinely zero-cost, ships fast, and directly
satisfies the existing `docs/todo.txt` "make an issue link" item, on top of
the log file infrastructure that already exists. Layer in **Option B**
(Sentry's free tier) if/when crash volume or frequency makes "read the log
file by hand every time" too slow. Only reach for **Option C** if
third-party data handling becomes a real constraint.

Sources consulted: [Sentry .NET/Extensions.Logging docs](https://docs.sentry.io/platforms/dotnet/guides/extensions-logging/),
[GlitchTip](https://glitchtip.com/) and its SDK docs, and Microsoft's
[App Center retirement notice](https://learn.microsoft.com/en-us/appcenter/retirement).
