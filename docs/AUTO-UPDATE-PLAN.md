# Auto-Update Plan

Scope: automatic updates for **desktop** (Windows/macOS/Linux) via GitHub Releases. Mobile is out of scope — App Store/Play Store already own updates there.

## Key decisions

- **Velopack**, not a hand-rolled updater: solves safely replacing a running executable on three OSes, already proven in production by another Avalonia app (Symphex). Chosen over **NetSparkle** (mature, but appcast-XML-based — would need self-hosting a feed instead of Velopack's built-in `GithubSource`). Ruled out: ClickOnce/Squirrel.Windows (Windows-only), store update mechanisms (fragments distribution away from GitHub).
- GitHub Releases is a first-class Velopack target: `vpk upload github --repoUrl ... --publish`, client reads via `GithubSource(repoUrl, token, prerelease: false)` for the stable channel.
- Beta vs. stable are genuine separate tracks via Velopack's own **channel** system (`vpk pack --channel beta` → separate `releases.beta.json`), not just `GithubSource`'s `prerelease` flag — a stable user can never land on a beta build.
- Versioning is broken out to `VERSIONING-PLAN.md` (MinVer, git-tag-based) — this plan just consumes `$(Version)`.
- `VelopackApp.Build().Run()` must be the literal first statement in `Program.cs`'s `Main()`, before `StartWithClassicDesktopLifetime` — Velopack re-invokes the binary with special args during install/update/uninstall inside `Run()`.
- macOS needs a paid Apple Developer ID + notarization (`--signAppIdentity`, `--notaryProfile`) for updated builds to pass Gatekeeper — not optional, since Gatekeeper re-checks the signature on every on-disk change. Windows signing avoids SmartScreen warnings but isn't required. Linux ships as a self-updating `.AppImage`, no signing needed.

## Phases

1. **Versioning foundation** — superseded by `VERSIONING-PLAN.md` Phase 1 (MinVer). Small/Low risk.
2. **Packaging + manual first release** — add Velopack NuGet + the `Main()` hook; `dotnet publish` self-contained, `vpk pack -v $(Version)`, manual `vpk upload github --publish` as a one-time dry run before automating in CI. Decide macOS signing now vs. ship unsigned first (users right-click → Open). Medium effort; macOS signing is the one real external dependency.
3. **Client-side update check** — `UpdateManager(new GithubSource(repoUrl, null, false))`, background task same pattern as the startup rescan, throttled via a persisted `LastUpdateCheck` on `AppSettings`. `CheckForUpdatesAsync` → `DownloadUpdatesAsync` → `ApplyUpdatesAndRestart`, with a simple non-intrusive "restart to install" prompt (not silent/forced). Small–Medium effort.
4. **CI publish automation** — workflow triggered on `v*` tag push, matrixed over `windows-latest`/`macos-latest`/`ubuntu-latest` (Velopack packaging is OS-native, can't cross-compile), each running publish + `vpk pack` + `vpk upload github --publish` into the same tagged release. Also the natural home for `VERSIONING-PLAN.md`'s mobile build-number step once needed. Medium effort.

## Suggested order

Versioning → Phase 2 on macOS only (dev machine) → Phase 3 against that manual release → Phase 4 CI automation → extend Phase 2/4 to Windows/Linux.

Not yet started.
