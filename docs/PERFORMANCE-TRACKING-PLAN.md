# Performance Tracking Plan

Two complementary halves: (A) CI benchmark regression tracking, catching a PR that silently slows a hot path, and (B) runtime/production performance signal for real user libraries.

## Key decisions

- **BenchmarkDotNet** for (A) — the standard .NET microbenchmarking library.
- **`benchmark-action/github-action-benchmark`** as the CI mechanism — has native `tool: 'benchmarkdotnet'` support, reading the JSON exporter's report. Two output modes: a GitHub Pages historical dashboard (`auto-push`) or simpler PR/commit comments (`comment-on-alert`) — start with comments, no Pages setup needed for v1.
- Known caveat: GitHub-hosted runners are noisy/shared — treat results as relative trend/regression detection, not precise absolute numbers.
- (B) already exists in embryonic form: the startup rescan is wrapped in a `Stopwatch` + logged in `App.axaml.cs`. This plan extends that pattern rather than introducing a new one.
- If `CRASH-REPORTING-PLAN.md`'s Sentry option is adopted, its SDK also does performance tracing (`SentrySdk.StartTransaction`) — same package, not a second tool, so (B) shouldn't stand up separate telemetry infrastructure.

## Phase A: CI benchmark regression tracking

Targets `TrackListBuilder.Sort`/`Filter`/row-building, `Library.UpdateTracks`'s merge, and `ITunesPlayCountImporter.ApplyFromXmlFile` — all hot paths exercised on every rescan/sort/filter against a real ~16k-track library.

- New `Flower.Benchmarks` project, benchmarks against a synthetic ~16k-track library.
- JSON exporter (`[JsonExporterAttribute.FullCompressed]`), new CI job using `github-action-benchmark`, comment-only mode with a conservative alert threshold (150–200%) to start.
- Effort: Medium. Risk: Low (additive).

## Phase B: Runtime/production performance signal

- Extend the existing `Stopwatch`+`ILogger` pattern to iTunes play-count sync and track-list rebuild time (the thing a user actually feels as "sluggish").
- If Sentry gets adopted, wrap the same operations in `SentrySdk.StartTransaction` instead of/alongside log timings — don't stand up telemetry infra just for this.
- Effort: Small now, free later if Sentry already exists. Risk: Low.

## Suggested order

Phase A (self-contained) → Phase B step 1 (cheap, anytime) → Phase B step 2 (only once Sentry is adopted for crash reporting).

Not yet started beyond the existing rescan-logging pattern.
