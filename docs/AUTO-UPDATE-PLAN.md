# Auto-Update Plan

Scope: automatic updates for the **desktop** build (Windows/macOS/Linux),
distributed via GitHub Releases on this repo. **Mobile (iOS/Android) is
explicitly out of scope** — those ship through the App Store/Play Store,
which already own the update mechanism; a self-updater has no role there and
would likely violate store policy.

## Research summary (why this is shaped the way it is)

- **Chose [Velopack](https://github.com/velopack/velopack) over rolling a
  custom updater.** A hand-rolled "poll the GitHub API, download the new
  build, replace files" approach is exactly the kind of thing this task asked
  to avoid reinventing — it also has to solve safely replacing a running
  executable on three different OSes, which Velopack has already solved.
  Confirmed via its docs and a real example (an Avalonia music app, Symphex,
  already ships with Velopack in production) rather than assumed.
- **GitHub Releases is a first-class Velopack target, not a workaround.**
  The `vpk` CLI has a dedicated `vpk upload github --repoUrl
  https://github.com/OWNER/REPO --publish` command, and the client ships a
  built-in `GithubSource` for `UpdateManager` that reads releases directly
  from the repo (`new GithubSource(repoUrl, accessToken, prerelease)` — pass
  `prerelease: false` to only ever pick up full releases, never drafts/pre-releases,
  as Flower's stable channel).
- **Beta releases: Velopack's own *channel* system, not just `GithubSource`'s
  `prerelease` flag.** `vpk pack --channel beta` produces its own
  `releases.beta.json` manifest, fully separate from the default channel's
  (which defaults to the OS name — `win`/`osx`/`linux` — unless overridden).
  The client is channel-aware automatically: an app packaged with `--channel
  beta` only ever checks `releases.beta.json`, no channel argument needed on
  `UpdateManager` itself. This is more precise than `GithubSource`'s
  `prerelease: bool`, which just filters GitHub's own prerelease flag on one
  shared release list — channels give two genuinely independent update
  tracks, so a stable user can never silently land on a beta build.
- **Versioning/build stamping: [MinVer](https://www.nuget.org/packages/minver),
  not `Nerdbank.GitVersioning`** (correcting this doc's earlier suggestion).
  MinVer computes the assembly version purely from git tags — no config file
  to maintain, unlike NBGV's `version.json` + git-height model, which is
  actually the *more* complex option here, not an upgrade path. Setup is a
  `PackageReference` (`PrivateAssets="all"`, so it's build-time only, never a
  runtime dependency) plus `<MinVerTagPrefix>v</MinVerTagPrefix>` to match
  the `v1.2.0` tag convention already adopted above. It automatically sets
  `AssemblyVersion`/`AssemblyFileVersion`/`AssemblyInformationalVersion` —
  exactly "binaries stamped with the current version at build time," and it
  works identically for local `dotnet build` and CI, not just CI. A
  SemVer pre-release tag (`v1.3.0-beta.1`) is used as-is for the version
  string; a commit that isn't exactly on a tag gets an automatic
  height-based pre-release version (e.g. `1.0.1-alpha.0.1`), so local/dev
  builds are never silently unversioned or numerically ambiguous with a
  real release.
- **Client integration is a single required line, but ordering matters.**
  `VelopackApp.Build().Run()` must be the literal first statement in
  `Main()` — before `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`
  in `Flower.Desktop/Program.cs:13-14` today. Velopack re-invokes the app's own
  binary with special arguments during install/update/uninstall; that hook
  runs and exits inside `Run()`, so anything placed *before* it would
  otherwise re-execute during those operations.
- **macOS needs a real signing/notarization setup, which Flower doesn't have
  today.** Velopack can auto-sign and notarize (`--signAppIdentity`,
  `--notaryProfile` on `vpk pack`), but that requires an Apple Developer
  Program membership (paid, $99/yr) and a notarization profile configured on
  the build machine. Without it, Gatekeeper will flag or block the
  auto-replaced binary — this isn't optional polish for auto-update
  specifically, since Gatekeeper re-checks the signature every time the app
  on disk changes. Windows code signing is recommended (avoids SmartScreen
  warnings) but not required for the mechanism to function. Linux ships as a
  self-updating `.AppImage`, no signing required.
- **No packaging/versioning pipeline exists yet.** There's no
  `dotnet publish` profile producing an installable artifact, no git tags,
  and no `<Version>` stamped anywhere — `docs/todo.txt` already tracks
  "versioning" as its own item, and it's a hard prerequisite here: `vpk pack
  -v <version>` needs a real, monotonically increasing version per release.
- **Alternative considered: [NetSparkle](https://github.com/NetSparkleUpdater/NetSparkle).**
  Mature, cross-platform, has a `NetSparkleUpdater.UI.Avalonia` package. Ruled
  out as the primary pick because it's appcast-XML-based — GitHub Releases
  integration means generating and hosting that feed yourself, rather than
  Velopack's built-in `GithubSource`. Worth revisiting only if Velopack's
  GitHub flow turns out to be a poor fit in practice.
- **Ruled out:** ClickOnce (Windows-only), Squirrel.Windows (Windows-only,
  effectively superseded by Velopack, which grew out of the same lineage),
  and platform app-store update mechanisms (would fragment distribution away
  from GitHub, which is explicitly the point of this plan).

---

## Phase 1: Versioning foundation

**Problem:** no version exists anywhere in the repo to hand to `vpk pack -v`,
and nothing stamps a version into the built binaries themselves.

**Plan:**
1. Adopt semver git tags as the single source of truth for release
   versions: `v1.0.0`, `v1.1.0`, ... for stable, `v1.3.0-beta.1`,
   `v1.3.0-beta.2`, ... for betas (SemVer's own pre-release syntax —
   universally recognized, including by GitHub's own "mark as prerelease"
   default and Velopack's channel/version handling).
2. Add [MinVer](https://www.nuget.org/packages/minver) to every shipped
   entry-point project (`Flower.Desktop.csproj`, `Flower.Android.csproj`,
   `Flower.iOS.csproj`, `Flower.CLI.csproj` — not `Flower.Tests.csproj`, no
   need to version a test assembly). Simplest way to apply it once rather
   than repeating the block in each: a `Directory.Build.props` at the repo
   root with
   ```xml
   <ItemGroup>
     <PackageReference Include="MinVer" Version="7.*">
       <PrivateAssets>all</PrivateAssets>
       <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
     </PackageReference>
   </ItemGroup>
   <PropertyGroup>
     <MinVerTagPrefix>v</MinVerTagPrefix>
   </PropertyGroup>
   ```
   This computes `Version`/`AssemblyVersion`/`AssemblyFileVersion`/
   `AssemblyInformationalVersion` from the nearest git tag automatically, on
   every build — local `dotnet build` included, not just CI — and needs no
   custom version-extraction script in the GitHub Actions workflow at all.
   `vpk pack` can then just read `$(Version)` from the build output instead
   of needing it passed in separately.

**Verified against this actual repo** (via the `minver-cli` global tool,
`minver --tag-prefix v`, no changes left behind):

| Situation | Computed version |
|---|---|
| No tags exist yet (repo's current state) | `0.0.0-alpha.0.121` — `121` is the commit count from the root commit |
| Built exactly on a tagged commit `v1.0.0` | `1.0.0` — the tag, verbatim |
| One commit past `v1.0.0`, untagged (ordinary local dev) | `1.0.1-alpha.0.1` — next patch, auto-marked `-alpha.0.<height>` |
| Built exactly on a beta tag `v1.1.0-beta.1` | `1.1.0-beta.1` — used as-is |

The rule that matters day to day: **exactly on a tag → that tag's version,
verbatim; anything else → the next patch, auto-suffixed
`-alpha.0.<commits since the tag>`.** That auto-suffixed form is itself a
valid SemVer pre-release that always sorts below the release it's heading
toward, so an ordinary local dev build is never ambiguous with — or
mistakeable for — a real tagged release.

**Effort:** Small. **Risk:** Low.

---

## Phase 2: Packaging + a manual first release

**Problem:** `dotnet publish` today produces a raw output folder, not
something a user installs or Velopack can manage updates for.

**Plan:**
1. Add the `Velopack` NuGet package to `Flower.Desktop`.
2. Add `VelopackApp.Build().Run()` as the first line of `Main()` in
   `Flower.Desktop/Program.cs`, before the existing
   `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` call.
3. Locally: `dotnet publish` self-contained for the current RID (MinVer from
   Phase 1 stamps `$(Version)` automatically, so `vpk pack -u Flower -v
   $(Version) -p <publish-dir>` needs no separate manual version input),
   install the `vpk` CLI (`dotnet tool install -g vpk`), then a manual
   `vpk upload github --repoUrl https://github.com/yanos/Flower --publish`
   as a one-time dry run to confirm the whole flow before automating it in
   CI. For a beta build, add `--channel beta` to both `vpk pack` and the
   corresponding tag (`v1.0.0-beta.1`) — see the channel note above.
4. macOS: decide up front whether to invest in Developer ID signing +
   notarization now (needed for a clean auto-update UX) or ship unsigned
   initially and accept that users must right-click → Open the first time,
   revisiting signing once/if this is used by more than the developer.
   Windows/Linux have no equivalent blocker.

**Effort:** Medium (mostly first-time packaging friction). **Risk:**
Low–Medium — macOS signing is the one piece with a real dependency (a paid
Apple account) outside the codebase itself.

---

## Phase 3: Client-side update check

**Problem:** Flower has no code that ever checks for a new version.

**Plan:**
1. `var mgr = new UpdateManager(new GithubSource("https://github.com/yanos/Flower", null, false));`
   — the `false` here just controls whether GitHub-flagged prereleases are
   considered at all; which *channel* (`stable` vs `beta`) this check runs
   against is determined automatically by which channel the running app
   itself was packaged with (Phase 2's `--channel` flag), not by anything
   passed here. Reuse the exact background-task pattern already established
   for the startup rescan in `App.axaml.cs` (`_ = Task.Run(async () => { ...
   })`), and log through the same `ILogger` infrastructure just added
   (`AppLogging.CreateLogger(...)`) so a failed/skipped check shows up in the
   run's log file exactly like the iTunes sync does today.
2. `CheckForUpdatesAsync()` → if non-null, `DownloadUpdatesAsync(...)` →
   `ApplyUpdatesAndRestart(...)`. Throttle checks (e.g. persist a
   `LastUpdateCheck` timestamp on `AppSettings`, matching its existing
   persistence conventions) rather than hitting GitHub on every single
   launch.
3. UI decision: a simple non-intrusive "Update available — restart to
   install" prompt is enough for a v1, given this is a personal-scale app,
   not silent/forced auto-apply.

**Effort:** Small–Medium. **Risk:** Low.

---

## Phase 4: CI publish automation

**Problem:** a manual `vpk upload` per release doesn't scale and is easy to
get wrong by hand.

**Plan:**
1. New workflow (or extend `.github/workflows/tests.yml`'s pattern),
   triggered on version-tag push (`on: push: tags: ['v*']`), `permissions:
   contents: write`.
2. Velopack packaging is OS-native and can't be cross-compiled from one
   runner — matrix over `windows-latest` / `macos-latest` / `ubuntu-latest`,
   each publishing + `vpk pack`-ing + `vpk upload github --publish`-ing its
   own platform asset into the same tagged release.

**Effort:** Medium (mostly a straightforward, well-documented CI recipe).
**Risk:** Low.

---

## Suggested execution order

1. **Versioning** (Phase 1) — everything else needs a real version number.
2. **Phase 2, macOS only first** (it's the dev machine) — get one platform's
   packaging and a manual GitHub release working end-to-end before touching CI.
3. **Phase 3** client check-in, against that manual release.
4. **Phase 4** CI automation, once the manual flow is proven.
5. Extend Phase 2/4 to Windows and Linux.

Sources consulted: [Velopack GitHub](https://github.com/velopack/velopack),
[Velopack docs](https://docs.velopack.io/), Velopack's GitHub Actions and
macOS packaging documentation, and a confirmed real-world Avalonia adopter
(Symphex releases).
