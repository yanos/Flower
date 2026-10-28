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

**All four phases are built; none has been through a real release yet.** The
first tags (v0.1.0 to v0.4.0) predate the installers, so no GitHub release
exists, and the first tag cut after this is the first end-to-end run. What was
built, and where it went another way than the phases above:

- **Packaging and publishing are one CI path, not a manual run first.** A `v*`
  tag runs `scripts/package-desktop.sh` on one runner per OS (the
  `installers` job in `tests.yml`) and the `release` job attaches everything
  to the tag's release, gated on the suite. The files go up with `gh release`
  rather than `vpk upload github`, because each OS packs on its own runner and
  one job gathers them. Pre-release is decided from the tag name, as the image
  job does; for a tag build it cannot disagree with `MinVerPreRelease`.
- **Windows and Linux are Velopack** (`Flower.Desktop`'s `VelopackUpdater`):
  the release carries `releases.win.json`/`releases.linux.json` and the
  `.nupkg` each names, beside `Setup.exe` and the AppImage. vpk tells the two
  OSes' packages apart by channel (`…-full.nupkg`, `…-linux-full.nupkg`), and
  the release job refuses two files of one name rather than letting one
  replace the other. The pack id is `Flower.Desktop`, not `Flower`: it names
  the install folder, `%LocalAppData%\<id>`, which Velopack deletes on
  uninstall - and `%LocalAppData%\Flower` is where the library lives.
- **macOS is not Velopack.** It ships as a plain dmg of `Flower.MacOS`,
  unsigned, because Velopack's macOS output is a `.pkg` and updating one in
  place needs the signing above. Its update is notify-and-download
  (`GitHubReleaseUpdater`): it reads `releases/latest`, compares the tag with
  `AppVersion` (`ReleaseVersion`), and the banner's button opens the dmg.
  Only the dmg looks at all - `FlowerReleaseBuild`, set by the packaging
  script, so a run from the IDE is never offered a "newer" release that is
  older than the code it was built from. Signing it is what would let it join
  the other two.
- **The client half is shared** (`AppUpdatesViewModel`, the head's
  `IAppUpdater` in `PlatformAppUpdater.Current`): first look 30 seconds after
  launch, then at most once a day across launches (`AppSettings.
  LastUpdateCheck`, advanced only by a check that succeeded), off with
  `CheckForUpdatesAutomatically` in Settings > General, which also has Check
  Now. An update found is a dismissible banner across the page; Velopack's is
  already downloaded by then, so its button restarts straight into it, and
  one never pressed goes in at the next launch anyway (`VelopackApp.Run`).
  Stable releases only, on every OS; there is no beta channel yet.

This document also absorbed
what was left of `VERSIONING-PLAN.md` when that finished, which is why the
MinVer detail above is more specific than the rest: those recipes were verified
against a real repo once and are cheap to lose.
