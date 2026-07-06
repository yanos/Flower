# Performance Tracking Plan

Two genuinely different things both fall under "performance tracking," and
the plan below recommends both, since they're complementary rather than
competing:

- **(A) CI benchmark regression tracking** — catch a PR that silently makes
  a hot path slower, before it ships. This is the piece that "incorporates
  well with GitHub" most directly.
- **(B) Runtime/production performance signal** — how the app actually
  performs for a real user, on their real (possibly 16k-track) library.

## Research summary

- **[BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet)** is the
  standard .NET microbenchmarking library — adopted by 27,400+ GitHub
  projects including the .NET runtime itself. Not reinventing anything by
  using it.
- **[`benchmark-action/github-action-benchmark`](https://github.com/benchmark-action/github-action-benchmark)**
  is a purpose-built GitHub Action for exactly this, with native
  `tool: 'benchmarkdotnet'` support — this is the concrete "incorporates
  with GitHub" mechanism, not a custom script.
  - Requires BenchmarkDotNet's JSON exporter (`--exporters json`, or
    `[JsonExporterAttribute.Full]`/`[JsonExporterAttribute.FullCompressed]`
    on the benchmark class) — the action reads the resulting
    `*-report-full-compressed.json` from `BenchmarkDotNet.Artifacts/results/`.
  - Two independent output modes: a **GitHub Pages dashboard** (`auto-push:
    true`, needs `contents: write` + `deployments: write` permissions, a
    `gh-pages` branch, historical chart at a configurable path) or simpler
    **PR/commit comments** (`comment-on-alert`/`comment-always`, no GitHub
    Pages needed at all). `alert-threshold` (default 200%) and
    `fail-threshold` control what counts as a regression.
- **Known, accepted caveat**: GitHub-hosted runners are shared/virtualized,
  so absolute benchmark numbers are noisy — a real practitioner running this
  exact BenchmarkDotNet + github-action-benchmark combination on free GitHub
  Actions runners confirms the accepted framing: use it for **relative
  trend/regression detection, not precise absolute numbers**. That's the
  right expectation to set here too, not a flaw specific to this plan.
- **Flower already has a first, minimal version of (B) shipped.** This
  session's logging work already wraps the startup rescan in a `Stopwatch`
  and logs `"Startup rescan found {TrackCount} tracks in {ElapsedMs}ms"`
  (`App.axaml.cs`) — runtime performance tracking already exists in
  embryonic form; this plan extends the same pattern rather than
  introducing a new one.
- **Sentry (the `CRASH-REPORTING-PLAN.md` recommendation) also ships
  performance tracing** — `SentrySdk.StartTransaction`/child spans, plain
  .NET API usable in ordinary desktop apps, not just web frameworks. If
  Sentry gets adopted for crash reporting, real-world performance tracking
  is the *same package*, not a second tool to stand up.

---

## Phase A: CI benchmark regression tracking

**Problem:** nothing today would catch a PR that silently makes
`TrackListBuilder`'s sort/filter/row-building or `Library.UpdateTracks`'s
path-matching merge meaningfully slower — both are hot paths exercised on
every rescan and every sort/filter interaction against a real ~16k-track
library, and both were touched heavily by this session's play-count fixes.

**Plan:**
1. New `Flower.Benchmarks` project (added to `Flower.sln`), `BenchmarkDotNet`
   package reference.
2. Write benchmarks against a synthetic large track list (shaped like a
   real library — ~16k `Track` objects) for the highest-value targets:
   - `TrackListBuilder.Sort`/`Filter`/row-building — the actual UI-felt
     latency for the track list.
   - `Library.UpdateTracks`'s carry-forward merge — now lock-guarded and
     dictionary-based per this session's fixes; exactly the kind of thing
     that should never regress silently.
   - `ITunesPlayCountImporter.ApplyFromXmlFile`'s SyncKey matching/summing.
3. Mark benchmarks with `[JsonExporterAttribute.FullCompressed]` (or pass
   `--exporters json` when running in CI).
4. New GitHub Actions workflow (or a job in the existing
   `.github/workflows/tests.yml`) using `benchmark-action/github-action-benchmark`
   with `tool: 'benchmarkdotnet'`. **Start with comment-only mode** —
   `comment-on-alert: true`, a conservative `alert-threshold` (e.g. 150–200%,
   tuned generously at first given shared-runner noise) — no GitHub Pages
   setup needed for v1.
5. Optional later escalation: turn on `auto-push`/a `gh-pages` dashboard
   once there's enough history for a trend chart to be worth looking at.

**Effort:** Medium (new project + a handful of well-chosen benchmarks + one
CI job). **Risk:** Low — additive, doesn't touch shipping code; the only
real risk is over-trusting noisy absolute numbers, mitigated by treating
this as trend detection per the research above.

---

## Phase B: Runtime/production performance signal

**Problem:** Phase A only catches regressions the benchmark suite thinks to
cover in advance. It says nothing about how the app actually performs on a
real user's real, messy library and machine.

**Plan:**
1. Extend the `Stopwatch` + `ILogger` pattern already used for rescan
   timing (`App.axaml.cs`) to the other operations already logged by
   *count* but not yet by *duration*: the iTunes play-count sync
   (`ITunesPlayCountImporter`), and track-list rebuild time after a
   filter/sort/column change — the thing a user actually feels as
   "sluggish."
2. If/when `CRASH-REPORTING-PLAN.md`'s Sentry option is adopted: wrap the
   same operations in `SentrySdk.StartTransaction`/spans instead of (or
   alongside) log-line timings, getting aggregated, queryable performance
   data across real sessions for free — reusing the exact SDK/account
   already chosen there rather than forking off a second telemetry tool.
3. Deliberately **not** recommending standing up a dedicated
   performance-telemetry service just for this on its own — it should ride
   on whichever crash-reporting decision gets made, not fork the
   infrastructure.

**Effort:** Small (logging extension) now; effectively free later
(Sentry transactions) if that dependency already exists. **Risk:** Low.

---

## Suggested execution order

1. **Phase A** — self-contained, no dependency on any other plan, and the
   piece that most directly "incorporates with GitHub."
2. **Phase B step 1** (extra timing logs) — cheap, can happen any time,
   independent of everything else.
3. **Phase B step 2** (Sentry transactions) — only once/if
   `CRASH-REPORTING-PLAN.md`'s Sentry option is actually adopted; don't
   stand up Sentry solely for this.

Sources consulted: [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet),
[github-action-benchmark](https://github.com/benchmark-action/github-action-benchmark)
and its [BenchmarkDotNet example](https://github.com/benchmark-action/github-action-benchmark/blob/master/examples/benchmarkdotnet/README.md),
[a real-world GitHub-Actions-budget writeup of this exact combination](https://blog.martincostello.com/continuous-benchmarks-on-a-budget/),
and [Sentry's .NET custom instrumentation/tracing docs](https://docs.sentry.io/platforms/dotnet/tracing/instrumentation/custom-instrumentation/).
