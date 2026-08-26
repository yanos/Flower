# Versioning Plan

**Status: Phases 1-2 implemented and verified — every assembly is versioned from git tags, on local builds as much as in CI. Phase 3 (resolving the version *outside* a build, for a release workflow to name artifacts with) is designed but deliberately not built, because nothing consumes it yet. No `v*` tag has been cut yet.**

One version scheme, driven from git tags, across every shipped target (Desktop, Android, iOS) instead of independently-maintained numbers. Prerequisite for `AUTO-UPDATE-PLAN.md` (`vpk pack -v`) and `STORE-DEPLOYMENT-PLAN.md` (Android `versionCode`/iOS `CFBundleVersion` must be real and strictly increasing).

## Key decisions

- **MinVer**, chosen over Nerdbank.GitVersioning: computes version from git tags alone, no config file, works identically locally and in CI. Verified against this repo: exactly on a tag → that tag verbatim; otherwise → next patch auto-suffixed `-alpha.0.<commits since tag>` (always sorts below the release it's heading toward).
- MinVer's semver string covers the marketing version (`ApplicationDisplayVersion` → Android `versionName`/iOS `CFBundleShortVersionString`) but **cannot** supply Android's `versionCode`/iOS's `CFBundleVersion` (`ApplicationVersion`) — both require a plain, strictly-increasing integer; a pre-release suffix doesn't even parse as one, and both stores reject a submission that reuses or lowers the previous number.
- **The build is the only thing that resolves a version.** MinVer runs as part of it and stamps the assemblies; there is no script, no generated file and no second tag-walk to drift from it. Anything needing the version *before* a build (Phase 3) asks MinVer rather than reimplementing it.
- **Pre-release vs. release is `MinVerPreRelease` being empty**, not a tag-name pattern or a `github.ref` check. MinVer leaves it empty only when HEAD sits exactly on a version tag; every other commit carries identifiers and sorts below the release it heads toward.
- No commit sha is passed via `MinVerBuildMetadata`: the .NET SDK's own source-control integration already appends `SourceRevisionId` to `InformationalVersion`, so setting it just yields a doubled `+<short>.<full>` suffix. (Confirmed by inspecting the built `.dll`.) Worth knowing that build metadata never reaches `$(Version)` anyway — MinVer derives `Version` from `PackageVersion`, which excludes it — so a `+` can't leak into Android's `versionName` or iOS's `CFBundleShortVersionString`.
- For that mobile build number, **chose git tag count** (`git tag --list 'v*' | wc -l`) over MinVer's commit-height suffix (resets per-tag, not globally increasing) or a timestamp (overflows Android's `versionCode` ceiling within this decade). Tag count is a small integer that only advances on a real tagged release and can't go backwards as long as tags aren't deleted.

## Phase 1: Desktop + CLI foundation (MinVer) — Done

Adopted semver git tags (`v1.0.0`, betas as `v1.3.0-beta.1`) as the single source of truth. Added MinVer via `Directory.Build.props` with `MinVerTagPrefix=v`. Computes `Version`/`AssemblyVersion`/`InformationalVersion` on every build, local included — no CI required, no generated file checked in.

MinVer originally applied to an allow-list of the shipped entry points. That was reverted to **every project**: the list was a maintenance trap that each new project had to remember to join, and `Flower.Core` — extracted after the list was written — never did. The result, confirmed by reading the assembly metadata rather than the build log, was `Flower.dll` and `Flower.Core.dll` shipping the SDK default `1.0.0` while `Flower.Desktop.dll` beside them carried the real version — the two libraries holding nearly all the code being the two that couldn't say which build they came from.

Note `AssemblyVersion` is `<major>.0.0.0` by MinVer's design (binding stability), so it reads `0.0.0.0` for the whole of `0.x`. The precise version lives in `InformationalVersion`.

That distinction is a trap, and the codebase had already fallen into it twice: the server's settings screen and the desktop settings screen both read `Assembly.GetName().Version` and so displayed `0.0.0.0`, while `AboutWindow` read the right attribute and owned a private copy of the strip-the-`+sha` logic. All three now go through `Flower.Core`'s `AppVersion` (`Flower.Core/Services/AppVersion.cs`), which exposes `Full` (with the commit sha, for logs and bug reports) and `Display` (without it, for anywhere a person reads it). It reads the assembly it is compiled into rather than `Assembly.GetEntryAssembly()` — safe precisely because every assembly now carries the same version, and never null the way the entry assembly can be.

## Phase 2: Mobile display version — Done

Two gotchas surfaced during implementation (confirmed via inspecting real build output, not assumed):
1. A plain `<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>` doesn't work — it's evaluated during MSBuild's static project-evaluation pass, before MinVer's target runs, so it froze at the SDK default. Fixed with a `Directory.Build.targets` target using `DependsOnTargets="MinVer"` + `BeforeTargets="_GetAndroidPackageName;_CompileAppManifest"` (`AfterTargets` didn't reliably force ordering here).
2. `Flower.iOS/Info.plist` had `CFBundleShortVersionString`/`CFBundleVersion` hardcoded to `1.0` — an explicit Info.plist entry wins over the MSBuild properties, so both keys had to be deleted. Android's manifest had no equivalent hardcoding.

Verified end-to-end with clean builds on both platforms.

## Phase 3: Pipeline mechanism + mobile build number — Designed, not built

A release workflow will need the version *before* it builds anything, to name a
tag, label an artifact or pass a store build number. This was built — a
`scripts/version.sh` emitting `version`/`is_release`/`build_number` into
`$GITHUB_OUTPUT`, plus a `version` job in `tests.yml` — and then removed, because
no release pipeline exists to consume any of it (that's `AUTO-UPDATE-PLAN.md`
Phase 4). A job computing three values nothing reads is a job that prints a
table. It is cheap to write again from the notes below; it is not cheap to keep
a wrong version of it alive unused for a year.

What was verified while it existed, so it doesn't have to be rediscovered:

- **Ask MinVer, don't re-walk the tags:** `dotnet msbuild <proj> -target:MinVer -getProperty:Version`. ~0.5s, no compile, no extra tool, and it cannot disagree with what the build stamps because it *is* that computation. Needs a `dotnet restore` first — MinVer's target ships inside its NuGet package, so unrestored the query fails `MSB4057`. `Flower.Desktop.csproj` is the entry point that needs no platform workload to evaluate.
- **Release vs. pre-release is `MinVerPreRelease` being empty**, not a tag-name pattern or a `github.ref` check — see Key decisions.
- **Mobile build number:** `git tag --list 'v*' | wc -l`.
- Behaviour confirmed against a tagged HEAD (`0.2.0`, release), an untagged commit five past a tag (`0.1.1-alpha.0.5`, pre-release) and a shallow clone.

Both mobile csproj files keep `<ApplicationVersion>1</ApplicationVersion>` as a dev-build placeholder; a store release overrides it with `/p:ApplicationVersion=<n>`. Confirming that number is actually higher than the last accepted submission belongs to `STORE-DEPLOYMENT-PLAN.md`.

Two real defects were found and fixed along the way, and those stay fixed
independently of any of the above:

- **Every `actions/checkout` in `tests.yml` used the default `fetch-depth: 1`.** MinVer walks tags and counts commits, so on a shallow clone it checks one commit, finds nothing, and falls back to `0.0.0-alpha.0` — CI was stamping unversioned binaries, and would have kept doing so after the first tag was cut. All checkouts now set `fetch-depth: 0`.
- **`MinVerMinimumMajorMinor` is now `0.1`.** With no tags at all the version read `0.0.0-alpha.0.<height>` — "unversioned" rather than "heading for 0.1.0". Any real tag at or above it wins outright, so it stops mattering once one is cut.

**Still open:** no `v*` tag exists, so every build is a `0.1.0-alpha.0.<height>` pre-release. Cutting `v0.1.0` is what turns the mechanism on.

## Suggested order

Everything that versions a build is done. What is left is not in this plan:

1. Cut the first `v*` tag — one command, and the only thing standing between the current `0.1.0-alpha.0.<height>` and a real version.
2. A tag-triggered release workflow, which is where Phase 3's recipes get written for real — `AUTO-UPDATE-PLAN.md` Phase 4, also home to `vpk pack -v` and the `/p:ApplicationVersion=` override.
