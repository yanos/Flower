# Cross-Platform Gaps — Remediation Plan

Source: cross-platform audit of `Flower/` (shared library) against the five
targets (macOS, Windows, Linux, iOS, Android). Each section below is one
finding from that audit, with a concrete plan to close it. Ordered by
severity/blast-radius, not necessarily execution order — dependencies are
called out where they exist.

---

## 1. Secondary windows break on mobile (`TrackInfoWindow`) — done, via a different design than proposed

**What was proposed here:** extract `TrackInfoWindow`'s content into a shared
`TrackInfoView` `UserControl`, hosted either in a real `Window` (desktop) or
an in-`MainView` overlay panel (mobile), behind one `OpenTrackInfo()` entry
point.

**What shipped instead:** two independent implementations rather than one
shared control behind a presentation branch — desktop kept
`Flower/Views/TrackInfoWindow.axaml(.cs)` as a real `Window` (via
`MainView.OpenTrackInfo()`, unchanged), and mobile got its own
`Flower/Views/Mobile/TrackInfoView.axaml(.cs)` (a `UserControl`, opened as a
full-screen sheet from `MobileMainView`/`MobileMainViewModel`) with the same
TagLib-backed fields duplicated rather than shared. Simpler to land given the
two UIs had already diverged by the time mobile's UI work happened, at the
cost of the two views needing to be kept in sync by hand if the field set
changes.

**Status:** done — see `MOBILE-PLAN.md`'s Phase 3 status ("Track Info as a
sheet").

**Effort:** Medium (mostly mechanical extraction). **Risk:** Low — isolated
to one feature.

---

## 2. `Process.Start` calls fail on iOS, wrong on Android/Linux

**Problem:** `MainView.LocateFile()` and `MainViewModel.OpenDatabaseLocation()`
branch only Mac / Windows / else-as-Linux (`open -R`, `explorer.exe`,
`xdg-open`). `Process.Start` is unusable on iOS (sandboxed) and unsupported on
Android. `LocateFile` is reachable from every platform via the track context
menu; `OpenDatabaseLocation` is desktop-only already (behind `NativeMenu`).

**Plan:**
1. Introduce a small `IPlatformShell` service with one method,
   `bool TryRevealInFileManager(string path)`, registered per-platform in
   `App.axaml.cs`'s DI container.
   - macOS/Windows/Linux implementation: today's `Process.Start` logic,
     unchanged, just moved behind the interface and gated to
     `IClassicDesktopStyleApplicationLifetime`.
   - Android/iOS implementation: return `false` immediately (no-op).
2. In `MainView.LocateFile()`, resolve `IPlatformShell` and call
   `TryRevealInFileManager`; if it returns `false`, hide/disable the "Locate
   File" menu item instead of showing it and silently failing. Compute
   `IsDesktop` once (via `ApplicationLifetime` type) and use it to build the
   context menu, mirroring how `OpenDatabaseLocationCommand` is already only
   exposed via the desktop-only `NativeMenu`.
3. No change needed for `OpenDatabaseLocation` beyond confirming it stays
   behind the desktop `NativeMenu` (it already is) — optionally route it
   through the same `IPlatformShell` for consistency.

**Effort:** Small. **Risk:** Low.

---

## 3. Importer has no mobile-appropriate media access path — **done, superseded by a different design**

**This item's original proposal is stale — see `SYNC-PLAN.md`'s
"Architecture prerequisite" and `MOBILE-PLAN.md`'s Phase 2 status for what
actually shipped.** Kept here (rather than deleted) as a record of the path
*not* taken and why, since both alternatives below were live options at the
time.

**What was proposed here:** an `IMusicSource` abstraction with an iOS
backend built on `MPMediaQuery`/`MPMediaLibrary` (Apple Music integration).

**What shipped instead:** `Flower.Importer.IMusicImporter` (not
`IMusicSource` — same idea, different name, predates this doc being
updated), with iOS reading its own sandboxed Documents folder via TagLib —
exactly like desktop's `Importer` scans `~/Music` — rather than integrating
with Apple's Music app at all. Android got `AndroidMediaStoreImporter`
(`Flower.Android/`), matching this item's Android proposal closely (`MediaStore`
+ TagLib# where the content URI can be opened as a stream), with one fix
found in practice: the `is_music != 0` filter had to be dropped entirely,
since MediaStore's `is_music` column is unreliably `NULL` rather than `1`
even for genuine music files.

**Why Apple Music integration was rejected:** `MPMediaLibrary` gives no way
to write files into it from outside the Music app (no write access,
DRM-restricted) — that would make `SYNC-PLAN.md`'s WiFi sync unable to ever
push a file *onto* an iPhone. Owning files directly unblocks both that and
plain `UIFileSharingEnabled` USB drag-and-drop; `MPMediaLibrary` would have
blocked both permanently.

**Consequence for `Track.Path`:** unaffected by the rejected proposal's
concern (an `MPMediaItem`/`NSUrl` special case) — since iOS owns its files
directly, `Track.Path` stays a plain filesystem path on every platform, no
nullable-with-fallback model change needed after all.

**Status:** done and validated — see `MOBILE-PLAN.md` ("Validated against
real music": 33 real tracks across 5 albums, imported and played back on
both an Android emulator and iOS simulator).

---

## 4. LibVLC bootstrap is hardcoded to a macOS desktop install

**Problem:** `VlcNativeSetup.Initialize()` hardcodes `/Applications/VLC.app`,
loads `libvlccore.dylib` by name, and P/Invokes `setenv` from `libc`. Every
other platform falls through to `Core.Initialize(null)` with no verification
that the bundled `VideoLAN.LibVLC.{Windows,Linux,Android,iOS}` native
packages actually get found this way.

**Plan:**
1. Split `VlcNativeSetup.Initialize()` into per-platform methods:
   - `InitializeMacOS()` — today's logic (kept as-is; it's a known-working
     workaround, not to be touched without reason).
   - `InitializeWindows()` / `InitializeLinux()` — call
     `Core.Initialize()` with no path (let LibVLCSharp resolve the
     `VideoLAN.LibVLC.*` NuGet-deployed native binaries), but add a
     post-check: if `Core.Initialize()` throws or the resulting `LibVLC`
     instance fails to construct, surface a clear "VLC not found" error to
     the UI instead of the current silent failure mode.
   - `InitializeMobile()` (Android/iOS) — call `Core.Initialize()` per
     LibVLCSharp's documented mobile setup (no plugin path override needed;
     the mobile native packages self-register), and confirm
     `LibVLCSharp.Avalonia`'s `VideoView`/audio playback actually initializes
     under `Avalonia.Android`/`Avalonia.iOS` — this hasn't been exercised at
     all yet per finding #7.
2. Add a smoke check on startup (each platform's entry point) that
   audio playback is actually available, and surface a non-fatal banner in
   the UI ("Playback unavailable on this device") rather than the app
   silently doing nothing when `_mediaPlayer.Play()` is called against an
   uninitialized `LibVLC`.
3. Defer Android/iOS verification until #3 (importer) has a working mobile
   data source — no point validating mobile playback before there are mobile
   tracks to play.

**Still open, but the biggest risk it flagged is resolved in practice:**
`VlcNativeSetup.Initialize()` hasn't been split into per-platform methods or
gained the "VLC not found" smoke check/banner — the non-macOS branch is still
one unconditional `Core.Initialize()` call with no post-check. But step 1's
actual concern (does `Core.Initialize()` without a path even work on
Android/iOS?) is now answered empirically: `MOBILE-PLAN.md`'s validation
confirms real audio playback on an Android emulator, iOS simulator, and a
real iPhone — so mobile LibVLC init does work through the generic branch as-is.
What's left is the hardening (Windows/Linux "not found" UX, mobile-specific
`InitializeMobile()` split for clarity) rather than an unresolved risk to
mobile playback.

**Linux update (July 2026):** the problem statement above had one wrong
premise — there is no `VideoLAN.LibVLC.Linux` NuGet package (VideoLAN has
never published one; official LibVLCSharp guidance is to install VLC from the
distro's package manager). So on Linux `Core.Initialize()` P/Invokes the
*system* `libvlc` — and that crashed the app on startup
(`DllNotFoundException` → SIGABRT), because .NET's probing looks for the
unversioned `libvlc.so`, which only the `libvlc-dev` package ships as a
symlink; the runtime packages (`libvlc5`) install only the versioned soname
`libvlc.so.5`. Fixed in `VlcNativeSetup.Initialize()`: on Linux a
`NativeLibrary.SetDllImportResolver` on the LibVLCSharp assembly resolves
`libvlc` → `libvlc.so.5` (soname stable across all of VLC 3.x), so the plain
distro `vlc` package is sufficient. Linux users still need VLC installed —
bundling libvlc+plugins is deliberately deferred to the packaging phase
(`AUTO-UPDATE-PLAN.md`'s AppImage/Velopack work, or a `.deb` with
`Depends: libvlc5, vlc-plugin-base`), since doing it outside a container
format means hand-collecting the plugin tree's dependency tail. With that
fix in, a missing/undiscoverable VLC is the only remaining Linux failure
mode, which is exactly what the still-open "VLC not found" UX (step 1's
post-check) should catch. Windows needs none of this: the maintained
`VideoLAN.LibVLC.Windows` package deploys `libvlc.dll` + the full plugin
tree into the app output and `Core.Initialize()` finds them — though a
one-time smoke test on a real Windows machine is still pending (x86/x64
only; Windows-on-ARM runs under x64 emulation).

**Effort:** Medium for Windows/Linux hardening; Large for verified mobile
support (depends on device/emulator access) — the "verified" half is now
done. **Risk:** Medium — desktop behavior must not regress while doing this.

---

## 5. Persistence paths assume desktop OS conventions — done, matching the plan closely

**What shipped:** `Flower/Persistence/AppDataDirectory.cs` is exactly the
proposed shared resolver (`AppDataPath.GetDirectory()` here as
`AppDataDirectory.Path`) — every store
(`LibraryStore`/`PlaylistStore`/`AppSettingsStore`/`DeviceNicknameStore`/
`DeviceIdentityStore`/`TrustedPeerStore`/`PlaylistSyncStateStore`, i.e. more
stores than existed when this item was written) routes through it. iOS gets
its sandboxed Documents-folder branch (`SpecialFolder.Personal`, confirmed
correct on current .NET-for-iOS in practice, not just in theory). Android
gets its `FilesDir`-equivalent via `PlatformDataDirectory.Current`, a
settable override injected at startup from `Flower.Android` (the "small
platform-specific accessor" the plan called for, named differently than
proposed). Validated on both a real device and emulator per
`MOBILE-PLAN.md`.

**Effort:** Small–Medium. **Risk:** Low on desktop (pure refactor, same
paths); needed a real device/emulator check for the mobile branch — done.

---

## 6. Drag-to-reorder uses a mouse-drag model, no touch handling — done, via a different mechanism than proposed

**What was proposed here:** brand the existing desktop drag code with a
`PointerType.Mouse` vs. `PointerType.Touch` branch, requiring a long-press
before entering drag mode on touch.

**What shipped instead:** mobile didn't extend desktop's
`MusicListView.Panel_PointerPressed`/`Panel_PointerMoved` at all — it got its
own separate implementation, `Flower/Views/Mobile/MobileMainView.axaml.cs`'s
`DragHandle_Pointer*` handlers. Rather than branching on pointer type, a drag
only starts from a dedicated drag-handle icon on each row (not "anywhere on
the row" like desktop), with a 10px movement threshold before it visually
kicks in — so normal list scrolling (which doesn't touch the handle) is never
hijacked, without needing to distinguish mouse from touch input at all. Gated
behind the same reorder-eligible context as desktop.

**Status:** done — see `MOBILE-PLAN.md`'s Phase 3 status ("touch drag-to-
reorder"). Verified via drag handles in the shipped code; not confirmed
whether it's been exercised on an actual touchscreen device vs. only a
pointer-emulated one.

**Effort:** Small. **Risk:** Low.

---

## 7. Android target is on `net7.0`, out of step with the rest of the solution — done, and gone further than proposed

**Problem (as originally found):** `Flower.Android.csproj` targeted
`net7.0-android` while the rest of the solution was on `net8.0`/`net8.0-ios`,
plus a still-default `ApplicationId` and iOS's `RuntimeIdentifier` hardcoded
to `iossimulator-x64` with device codesigning commented out — both mobile
targets looked like untouched scaffolding.

**Status:** done, and superseded upward — `Flower.Android.csproj` is now on
`net10.0-android` (not just `net8.0-android`, matching the rest of the
solution's later net10 move — see `VERSIONING-PLAN.md`), with a real
`ApplicationId` (`com.yanos.flower`) and an `ApplicationVersion` wired for a
store-release override. `Flower.iOS.csproj` now defaults `RuntimeIdentifier`
to `ios-arm64` (a real device build) with the simulator RID commented out as
the opt-in alternative — the inverse of the original scaffolding, and further
than this item asked for. Both platforms have had a first real build+launch
(Android emulator, iOS simulator *and* a real iPhone) captured — see
`MOBILE-PLAN.md`'s "Validated against real music" section — closing this
item's step 4 as well.

Remaining open thread, tracked in `STORE-DEPLOYMENT-PLAN.md` rather than
here: `Flower.iOS.csproj`'s `net10.0-ios18.0`-era TFM still needs a further
bump to `net10.0-ios26.2` for App Store submission, and
`Flower.Android.csproj` still produces an `apk` rather than the `.aab` Play
Console requires — both are store-submission blockers, not cross-platform
runtime gaps, so they're scoped there instead.

**Effort:** Small for the version/config bump; the "real build+launch"
verification step depends on everything above being in place first.
**Risk:** Low for the bump itself; net7→net8 could theoretically surface
Android-specific API breaks, but nothing in the current shared code looked
net7-specific, and none surfaced in practice.

---

## 8. Bundle libvlc natives ourselves — drop the "VLC must be installed" requirement (macOS first)

**Problem:** Windows/Android/iOS are self-contained (maintained
`VideoLAN.LibVLC.*` NuGets deploy the native libs), but macOS and Linux
require a user-installed VLC. On macOS that's because the official
`VideoLAN.LibVLC.Mac` NuGet is abandoned (3.1.3.1, ~2019) — hence the
`/Applications/VLC.app` + `VLC_PLUGIN_PATH` workaround in `VlcNativeSetup`,
our ugliest platform hack. On Linux no `VideoLAN.LibVLC.Linux` package has
ever existed; we depend on the distro `vlc` package (workable since item
#4's soname resolver, but still an install prerequisite).

**Decision (July 2026):** package the natives ourselves **only where the
official binaries already exist to extract** — i.e. macOS. Explicitly do
NOT touch Windows/Android/iOS (their packages are maintained; a homemade
replacement is pure maintenance burden for zero gain), and do NOT build a
Linux package (VideoLAN ships no portable Linux build; a genuinely
distro-agnostic libvlc means compiling VLC + its contribs against an old
glibc baseline and re-owning that build for every VLC security release —
disproportionate when the eventual Linux distribution formats already solve
this: AppImage/Flatpak bundle at packaging time with tooling that collects
the dependency tail, and a `.deb` just declares
`Depends: libvlc5, vlc-plugin-base`. Tracked as part of
`AUTO-UPDATE-PLAN.md`'s packaging phase, not here).

**Plan (macOS):**
1. Extract `lib/` (`libvlc.dylib`, `libvlccore.dylib`) + `plugins/` from the
   official VLC 3.0.x dmgs. Two RIDs — VideoLAN ships separate Intel and
   Apple Silicon dmgs — laid out as `runtimes/osx-x64/native` and
   `runtimes/osx-arm64/native` (or `lipo`'d into universal binaries).
2. **Prune the plugin tree** to what an audio player needs: demux, audio
   codecs (incl. the FFmpeg plugin), audio output (`auhal`), meta/art
   readers, and the `http`/`access` modules (the OpenSubsonic sync client
   streams over HTTP). Full tree is hundreds of files / ~100 MB per arch;
   pruned should land ~30–50 MB.
3. **License audit on the pruned set**: libvlc + most plugins are LGPL 2.1
   (already covered by the repo-root `NOTICE`), but a few stock plugins are
   GPL — verify none survive pruning, or accept them knowingly.
4. Delivery mechanism, simplest first: an MSBuild copy target in
   `Flower.Desktop.csproj` over a checked-in or CI-downloaded native folder
   is enough — a real NuGet (GitHub Packages) only pays off if
   `Flower.Server`/CLI later need the same artifacts. Decide when building.
5. Simplify `VlcNativeSetup`'s macOS branch to load from the app directory
   (same shape as Windows), keeping `/Applications/VLC.app` as a fallback
   during transition.
6. Re-sign the bundled dylibs under our own identity at app-signing time —
   folds into the notarization work `AUTO-UPDATE-PLAN.md` already commits
   to for auto-updated macOS builds.

**Interaction with item #4:** once macOS is self-contained, Linux is the
only platform left where "VLC not found" is a reachable user-facing state,
which shrinks the still-open not-found-UX work to one platform (plus a
sanity check that the bundle itself loaded).

**Effort:** Medium (macOS extraction/pruning/signing; no compilation from
source anywhere). **Risk:** Low-Medium — desktop macOS playback must not
regress; keeping the VLC.app fallback during transition mitigates.

Not yet started.

---

## Suggested execution order

1. **#7** (Android version/config bump) — **done**, and gone further than
   originally scoped (net10, real `ApplicationId`, real-device iOS RID).
2. **#5** (persistence paths) — small, low-risk, removes duplication
   regardless of mobile timeline. Still open — worth checking against
   `AppSettingsStore`/`LibraryStore`/`PlaylistStore`'s current state before
   assuming it's untouched, since mobile persistence clearly works in
   practice (`MOBILE-PLAN.md`'s validation) even if this exact refactor
   wasn't the mechanism.
3. **#2** (`Process.Start` gating) — small, self-contained. Still open —
   `MainView.LocateFile()`/`MainViewModel.OpenDatabaseLocation()` still call
   `Process.Start` directly with no `IPlatformShell` abstraction or
   Android/iOS branch.
4. **#1** (`TrackInfoWindow` → mobile) — **done**, via two separate views
   rather than the proposed shared-overlay design; see the item itself.
5. **#4** (LibVLC bootstrap hardening) — mobile playback risk is resolved in
   practice (validated real device/emulator/simulator playback); the
   Windows/Linux "not found" hardening itself is still open and can be done
   independently of anything else here.
6. **#3** (mobile importers) — **done**, see the item itself for what
   actually shipped instead of the original proposal.
7. **#6** (touch-aware drag) — **done**, via a dedicated drag-handle +
   threshold design rather than the proposed `PointerType` branch — see the
   item itself.
8. **#8** (self-bundled libvlc natives, macOS first) — added July 2026, not
   yet started. Independent of everything above; its signing step naturally
   pairs with `AUTO-UPDATE-PLAN.md`'s macOS notarization work, so
   sequencing it alongside that is sensible.

**Summary: of the original 7 items, only #2 (`Process.Start`/`IPlatformShell`
gating) is still genuinely unbuilt** — everything else is done, several
(#5, #7) closely matching the original proposal and others (#1, #3, #6) done
via a different design than what was originally sketched here. Item #8
(bundling libvlc natives to drop the macOS installed-VLC requirement) was
added later and is not yet started.
