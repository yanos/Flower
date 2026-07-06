# Versioning Plan

**Status: Phases 1–2 implemented and verified (clean builds of all four
targets); Phase 3's CI wiring is still open — see that phase.**

Scope: **one version scheme, driven from git tags, across every shipped
target** — Desktop (Windows/macOS/Linux), Android, iOS, and the CLI — instead
of four independently-maintained numbers. This is a prerequisite of two other
plans, not a standalone feature: `AUTO-UPDATE-PLAN.md` needs a real version to
hand to `vpk pack -v`, and `STORE-DEPLOYMENT-PLAN.md` needs Android's
`versionCode` and iOS's `CFBundleVersion` to be real and strictly increasing
before either store will accept a submission.

## Research summary (why this is shaped the way it is)

- **[MinVer](https://www.nuget.org/packages/minver) computes the version from
  git tags alone** — no config file, works identically for local `dotnet
  build` and CI. Confirmed against this actual repo (via the `minver-cli`
  global tool, `minver --tag-prefix v`):

  | Situation | Computed version |
  |---|---|
  | No tags exist yet (repo's current state) | `0.0.0-alpha.0.121` — `121` is the commit count from the root commit |
  | Built exactly on a tagged commit `v1.0.0` | `1.0.0` — the tag, verbatim |
  | One commit past `v1.0.0`, untagged (ordinary local dev) | `1.0.1-alpha.0.1` — next patch, auto-marked `-alpha.0.<height>` |
  | Built exactly on a beta tag `v1.1.0-beta.1` | `1.1.0-beta.1` — used as-is |

  This is the single source of truth for the **marketing version** everywhere:
  `$(Version)` on Desktop/CLI, and — as established below — Android's
  `ApplicationDisplayVersion` / iOS's `ApplicationDisplayVersion` too.
- **Mobile needs a second, different kind of number that MinVer's `$(Version)`
  can't supply.** Checked both mobile csproj files directly:
  `Flower.Android.csproj` already hardcodes `<ApplicationVersion>1</ApplicationVersion>`
  and `<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>`;
  `Flower.iOS.csproj` sets neither (silently defaulting) today. Per
  [Microsoft's build-properties docs](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties)
  and [dotnet/android's `OneDotNetSingleProject` guide](https://github.com/dotnet/android/blob/main/Documentation/guides/OneDotNetSingleProject.md),
  the same two MSBuild properties exist on both platforms but map to
  different native fields:
  - `ApplicationDisplayVersion` → Android `versionName` / iOS
    `CFBundleShortVersionString` — a free-form, user-facing string. MinVer's
    `$(Version)` (including a pre-release suffix like `1.2.0-beta.1`) is
    fine here as-is.
  - `ApplicationVersion` → Android `versionCode` / iOS `CFBundleVersion` —
    **not** a marketing version. Android requires a plain positive integer
    (ceiling 2100000000 per Play Console); iOS requires "a period-separated
    list of at most three non-negative integers" (no letters, so a semver
    pre-release suffix is invalid). Both stores additionally require this
    number to **strictly increase over the previous accepted submission** —
    re-uploading the same or a lower number is a hard rejection on both
    Play Console and App Store Connect.
  - MinVer's own `$(Version)` therefore cannot be reused for
    `ApplicationVersion` directly: it isn't a plain integer, and a pre-release
    build (`1.0.1-alpha.0.1`) doesn't even parse as one.
- **Chose git tag count over commit height or a timestamp for the mobile
  build number.** Three options considered for a monotonically-increasing
  integer:
  1. MinVer's own commit-height suffix (the `.1` in `1.0.1-alpha.0.1`) —
     rejected: it resets relative to the nearest tag, not globally
     increasing, and isn't exposed as a separate MSBuild property to
     consume on its own.
  2. A build timestamp (`YYYYMMDDHH`-style) — rejected: a full timestamp
     overflows Android's 2100000000 `versionCode` ceiling within this
     decade unless truncated in a fragile way, and it encodes no more
     information than "when," which a git-committed release tag already
     records.
  3. **Git tag count at release time** (`git tag --list 'v*' | wc -l`) —
     chosen. It's a plain small integer, only advances on an actual tagged
     release (exactly the cadence a store build number needs to move at,
     unlike per-commit or per-CI-run counters), is trivial to compute in the
     tag-triggered release workflow `AUTO-UPDATE-PLAN.md` Phase 4 already
     adds, and is structurally incapable of going backwards as long as tags
     are never deleted.

---

## Phase 1: Desktop + CLI foundation (MinVer)

**Implemented.** No version existed anywhere in the repo before this, and
nothing stamped one into any built binary.

**Plan:**
1. Adopt semver git tags as the single source of truth for release versions:
   `v1.0.0`, `v1.1.0`, ... for stable, `v1.3.0-beta.1`, `v1.3.0-beta.2`, ...
   for betas.
2. Add [MinVer](https://www.nuget.org/packages/minver) once, at the root, so
   every shipped entry-point project picks it up automatically — a
   `Directory.Build.props` addition (the file already exists at the repo
   root for `Nullable`/`AvaloniaVersion`, so this is one more `PropertyGroup`/
   `ItemGroup`, not a new file), scoped by `$(MSBuildProjectName)` to the
   four entry-point projects specifically (not `Flower.csproj`, the shared
   library, or `Flower.Tests.csproj`):
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
   `AssemblyInformationalVersion` from the nearest git tag on every build —
   local `dotnet build` included — for `Flower.Desktop`, `Flower.CLI`,
   `Flower.Android`, and `Flower.iOS` alike (not `Flower.Tests`, no need to
   version a test assembly). `vpk pack` (`AUTO-UPDATE-PLAN.md` Phase 2) can
   then read `$(Version)` from the build output instead of taking it as a
   separate manual input.

**The rule that matters day to day:** exactly on a tag → that tag's version,
verbatim; anything else → the next patch, auto-suffixed
`-alpha.0.<commits since the tag>`. That auto-suffixed form is itself a valid
SemVer pre-release that always sorts below the release it's heading toward,
so an ordinary local dev build is never ambiguous with — or mistakeable for —
a real tagged release.

**Effort:** Small. **Risk:** Low.

---

## Phase 2: Mobile display version

**Problem:** `Flower.Android.csproj` hardcodes `ApplicationDisplayVersion`
to a literal `1.0` that nothing will ever update; `Flower.iOS.csproj` sets
nothing at all and is silently relying on tooling defaults.

**Implemented — two gotchas surfaced that the original plan didn't
anticipate, both confirmed by inspecting actual build output
(`AndroidManifest.xml`, `Info.plist`), not just assumed:**

1. **A plain `<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>`
   in the csproj doesn't work** — it's a static property, evaluated once
   during MSBuild's project-evaluation pass, which happens *before* MinVer's
   own target ever executes. It froze at whatever `$(Version)` was at parse
   time (the SDK's own default, `1.0.0`), permanently, regardless of what
   MinVer computed later during the actual build. Confirmed via
   `/p:MinVerVerbosity=diagnostic`: MinVer correctly computed
   `0.0.0-alpha.0.122`, but the property that had already captured a stale
   snapshot never saw it.
   - **Fix:** a small MSBuild target in `Directory.Build.targets`, scoped to
     `Flower.Android`/`Flower.iOS`, that assigns `ApplicationDisplayVersion`
     *dynamically* — `DependsOnTargets="MinVer"` (not `AfterTargets`, which
     turned out not to force ordering reliably here — Android calls
     `_GetAndroidPackageName` from an earlier, separate build pass that
     doesn't otherwise include MinVer at all) and
     `BeforeTargets="_GetAndroidPackageName;_CompileAppManifest"` (the actual
     Android/iOS targets that read the property).
2. **`Flower.iOS/Info.plist` had `CFBundleShortVersionString`/
   `CFBundleVersion` hardcoded to `1.0` already**, checked into the repo.
   An explicit Info.plist entry wins over the `ApplicationDisplayVersion`/
   `ApplicationVersion` MSBuild properties — both keys had to be deleted from
   the file before the properties could take effect at all. (Android's
   `AndroidManifest.xml` had no such hardcoded entry, so it needed no
   equivalent fix.)

Verified end-to-end with clean builds (`rm -rf obj bin` first, to rule out
incremental-build staleness): Android's generated `AndroidManifest.xml` shows
`versionName="0.0.0-alpha.0.122"`; iOS's generated `Info.plist` shows
`CFBundleShortVersionString => "0.0.0-alpha.0.122"`.

**Effort:** Small (turned out Medium once the two gotchas above surfaced).
**Risk:** Low.

---

## Phase 3: Mobile build number

**Problem:** Android's `ApplicationVersion` (`versionCode`) and iOS's
`ApplicationVersion` (`CFBundleVersion`) both need a plain, strictly-
increasing integer that MinVer's semver string cannot supply, and both
Play Console and App Store Connect reject a submission that reuses or
lowers the previous build's number.

**Plan:**
1. ~~Keep a hardcoded placeholder (`1`) in both mobile csproj files for
   local/dev builds~~ — **done**: both `Flower.Android.csproj` and
   `Flower.iOS.csproj` now have `<ApplicationVersion>1</ApplicationVersion>`
   (confirmed unaffected by the Phase 2 dynamic-target fix — Android's
   `versionCode="1"` and iOS's `CFBundleVersion => "1"` both verified in a
   clean build). Local/dev builds are never submitted to a store, so the
   exact value is irrelevant there.
2. **Still open:** in a tag-triggered CI release workflow (`AUTO-UPDATE-PLAN.md`
   Phase 4 adds this for Desktop; extend it here rather than building a
   second workflow — no such workflow exists yet, only `.github/workflows/tests.yml`
   does today), compute the release build number as the count of release
   tags at that point — `git tag --list 'v*' | wc -l` — and pass it to
   the mobile build as `dotnet build ... /p:ApplicationVersion=<n>`,
   overriding the csproj placeholder for that CI-produced artifact only.
   Deliberately not built preemptively: there's no mobile CI build at all
   yet to attach it to, and standing up an empty pipeline with nothing real
   to publish would just be scaffolding to revisit later anyway.
3. Confirm at the first real mobile submission (this belongs to
   `STORE-DEPLOYMENT-PLAN.md`'s scope, not this plan's) that the resulting
   `versionCode`/`CFBundleVersion` is in fact higher than whatever was last
   accepted — trivially true the first time, and true forever after as long
   as tags are never deleted.

**Effort:** Small. **Risk:** Low — the one failure mode (deleting/re-tagging
a past release) is already something git history hygiene should avoid
regardless of this plan.

---

## Suggested execution order

1. **Phase 1** — needed before `AUTO-UPDATE-PLAN.md` Phase 2 (packaging) can
   run `vpk pack -v $(Version)` against a real number.
2. **Phase 2** — cosmetic, can land whenever convenient once Phase 1's
   MinVer reference exists on the mobile projects.
3. **Phase 3** — only actually needed once a real store submission is being
   prepared (`STORE-DEPLOYMENT-PLAN.md`), but cheap enough to wire into the
   CI workflow at the same time as `AUTO-UPDATE-PLAN.md` Phase 4 rather than
   coming back to it later.

Sources consulted: [MinVer](https://github.com/adamralph/minver),
[.NET for iOS build properties](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties),
[dotnet/android `OneDotNetSingleProject` guide](https://github.com/dotnet/android/blob/main/Documentation/guides/OneDotNetSingleProject.md).
