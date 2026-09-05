# Auto-Update Plan

Scope: automatic updates for **desktop** (Windows/macOS/Linux) via GitHub Releases. Mobile is out of scope — App Store/Play Store already own updates there.

## Key decisions

- **Velopack**, not a hand-rolled updater: solves safely replacing a running executable on three OSes, already proven in production by another Avalonia app (Symphex). Chosen over **NetSparkle** (mature, but appcast-XML-based — would need self-hosting a feed instead of Velopack's built-in `GithubSource`). Ruled out: ClickOnce/Squirrel.Windows (Windows-only), store update mechanisms (fragments distribution away from GitHub).
- GitHub Releases is a first-class Velopack target: `vpk upload github --repoUrl ... --publish`, client reads via `GithubSource(repoUrl, token, prerelease: false)` for the stable channel.
- Beta vs. stable are genuine separate tracks via Velopack's own **channel** system (`vpk pack --channel beta` → separate `releases.beta.json`), not just `GithubSource`'s `prerelease` flag — a stable user can never land on a beta build.
- Versioning is **built and this plan just consumes `$(Version)`.** MinVer runs in every build from `Directory.Build.props` and stamps every assembly, desktop and mobile alike, from git tags with prefix `v`; `Flower.Core`'s `AppVersion` is what anything user-facing reads. There is no separate versioning plan any more — it finished. What is left of it is one command and lives in Phase 2 below.
- `VelopackApp.Build().Run()` must be the literal first statement in `Program.cs`'s `Main()`, before `StartWithClassicDesktopLifetime` — Velopack re-invokes the binary with special args during install/update/uninstall inside `Run()`.
- macOS needs a paid Apple Developer ID + notarization (`--signAppIdentity`, `--notaryProfile`) for updated builds to pass Gatekeeper — not optional, since Gatekeeper re-checks the signature on every on-disk change. Windows signing avoids SmartScreen warnings but isn't required. Linux ships as a self-updating `.AppImage`, no signing needed.

## Phases

1. **Versioning foundation** — done (MinVer, every project, `fetch-depth: 0` on every CI checkout so a shallow clone cannot silently stamp `0.0.0-alpha.0`). **One thing is still owed and it gates everything below: no `v*` tag has ever been cut**, so every build is `0.1.0-alpha.0.<height>`. `git tag v0.1.0 && git push --tags` is the whole task, and until it happens there is no release for Velopack to publish or for a client to compare itself against.
2. **Packaging + manual first release** — add Velopack NuGet + the `Main()` hook; `dotnet publish` self-contained, `vpk pack -v $(Version)`, manual `vpk upload github --publish` as a one-time dry run before automating in CI. Decide macOS signing now vs. ship unsigned first (users right-click → Open). Medium effort; macOS signing is the one real external dependency.
3. **Client-side update check** — `UpdateManager(new GithubSource(repoUrl, null, false))`, background task same pattern as the startup rescan, throttled via a persisted `LastUpdateCheck` on `AppSettings`. `CheckForUpdatesAsync` → `DownloadUpdatesAsync` → `ApplyUpdatesAndRestart`, with a simple non-intrusive "restart to install" prompt (not silent/forced). Small–Medium effort.
4. **CI publish automation** — workflow triggered on `v*` tag push. This is where the release job needs the version *before* it builds, to name a tag or pass a store build number. Do not re-walk the tags: ask MinVer, `dotnet msbuild Flower.Desktop.csproj -target:MinVer -getProperty:Version` (~0.5s, needs a prior `dotnet restore` or it fails `MSB4057`), because that *is* the computation the build uses and so cannot disagree with it. Release vs. pre-release is `MinVerPreRelease` being empty, not a tag-name pattern or a `github.ref` check. The mobile build number — which MinVer cannot supply, since Android `versionCode`/iOS `CFBundleVersion` need a plain increasing integer a pre-release suffix does not even parse as — is `git tag --list 'v*' | wc -l`, passed as `/p:ApplicationVersion=<n>` over the `1` both csproj files carry as a dev placeholder. All four were verified against a real tagged HEAD, an untagged commit and a shallow clone; a script doing them existed briefly and was deleted for having no consumer, which is this phase.

   The job itself is matrixed over `windows-latest`/`macos-latest`/`ubuntu-latest` (Velopack packaging is OS-native, can't cross-compile), each running publish + `vpk pack` + `vpk upload github --publish` into the same tagged release. Medium effort.

## Suggested order

Cut `v0.1.0` → Phase 2 on macOS only (dev machine) → Phase 3 against that manual release → Phase 4 CI automation → extend Phase 2/4 to Windows/Linux.

**Phase 1 is done; 2 through 4 are not started.** This document also absorbed
what was left of `VERSIONING-PLAN.md` when that finished, which is why the
MinVer detail above is more specific than the rest: those recipes were verified
against a real repo once and are cheap to lose.
