# Versioning Plan

**Status: Phases 1-2 implemented and verified. Phase 3's CI wiring is still open.**

One version scheme, driven from git tags, across every shipped target (Desktop, Android, iOS, CLI) instead of four independently-maintained numbers. Prerequisite for `AUTO-UPDATE-PLAN.md` (`vpk pack -v`) and `STORE-DEPLOYMENT-PLAN.md` (Android `versionCode`/iOS `CFBundleVersion` must be real and strictly increasing).

## Key decisions

- **MinVer**, chosen over Nerdbank.GitVersioning: computes version from git tags alone, no config file, works identically locally and in CI. Verified against this repo: exactly on a tag → that tag verbatim; otherwise → next patch auto-suffixed `-alpha.0.<commits since tag>` (always sorts below the release it's heading toward).
- MinVer's semver string covers the marketing version (`ApplicationDisplayVersion` → Android `versionName`/iOS `CFBundleShortVersionString`) but **cannot** supply Android's `versionCode`/iOS's `CFBundleVersion` (`ApplicationVersion`) — both require a plain, strictly-increasing integer; a pre-release suffix doesn't even parse as one, and both stores reject a submission that reuses or lowers the previous number.
- For that mobile build number, **chose git tag count** (`git tag --list 'v*' | wc -l`) over MinVer's commit-height suffix (resets per-tag, not globally increasing) or a timestamp (overflows Android's `versionCode` ceiling within this decade). Tag count is a small integer that only advances on a real tagged release and can't go backwards as long as tags aren't deleted.

## Phase 1: Desktop + CLI foundation (MinVer) — Done

Adopted semver git tags (`v1.0.0`, betas as `v1.3.0-beta.1`) as the single source of truth. Added MinVer via `Directory.Build.props`, scoped to the four entry-point projects (not `Flower.csproj`/`Flower.Tests.csproj`), with `MinVerTagPrefix=v`. Computes `Version`/`AssemblyVersion`/etc. on every build, local included.

## Phase 2: Mobile display version — Done

Two gotchas surfaced during implementation (confirmed via inspecting real build output, not assumed):
1. A plain `<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>` doesn't work — it's evaluated during MSBuild's static project-evaluation pass, before MinVer's target runs, so it froze at the SDK default. Fixed with a `Directory.Build.targets` target using `DependsOnTargets="MinVer"` + `BeforeTargets="_GetAndroidPackageName;_CompileAppManifest"` (`AfterTargets` didn't reliably force ordering here).
2. `Flower.iOS/Info.plist` had `CFBundleShortVersionString`/`CFBundleVersion` hardcoded to `1.0` — an explicit Info.plist entry wins over the MSBuild properties, so both keys had to be deleted. Android's manifest had no equivalent hardcoding.

Verified end-to-end with clean builds on both platforms.

## Phase 3: Mobile build number — Partially done

- Done: both mobile csproj files have `<ApplicationVersion>1</ApplicationVersion>` as a dev-build placeholder (irrelevant since local builds are never submitted).
- **Still open:** a tag-triggered CI release workflow (extend `AUTO-UPDATE-PLAN.md` Phase 4's, rather than building a second one — no mobile CI exists yet) should compute `git tag --list 'v*' | wc -l` and pass it as `/p:ApplicationVersion=<n>`, overriding the placeholder for that artifact only. Deliberately not built preemptively — no mobile CI pipeline exists yet to attach it to.
- Confirming the resulting number is actually higher than the last accepted submission belongs to `STORE-DEPLOYMENT-PLAN.md`.

## Suggested order

1. Phase 1 — needed before `AUTO-UPDATE-PLAN.md` Phase 2 can run `vpk pack -v $(Version)`.
2. Phase 2 — cosmetic, land whenever convenient.
3. Phase 3 — only strictly needed once a real store submission is being prepared, but cheap enough to wire in alongside `AUTO-UPDATE-PLAN.md` Phase 4.
