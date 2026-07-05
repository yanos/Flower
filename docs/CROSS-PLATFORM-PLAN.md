# Cross-Platform Gaps — Remediation Plan

Source: cross-platform audit of `Flower/` (shared library) against the five
targets (macOS, Windows, Linux, iOS, Android). Each section below is one
finding from that audit, with a concrete plan to close it. Ordered by
severity/blast-radius, not necessarily execution order — dependencies are
called out where they exist.

---

## 1. Secondary windows break on mobile (`TrackInfoWindow`)

**Problem:** `TrackInfoWindow` is an Avalonia `Window`, opened via `.Show(owner)`
from `MainView.OpenTrackInfo()`. Avalonia's Android/iOS backends are
single-view only; a second top-level `Window` isn't supported there. Reachable
on every platform via the track context menu ("Get Info").

**Plan:**
1. Extract the current `TrackInfoWindow.axaml` content (tabs, fields, nav
   buttons) into a `UserControl` (`TrackInfoView`) with the same
   `TrackInfoWindow.axaml.cs` logic minus window chrome (`Close()`,
   `ShowInTaskbar`, etc.).
2. Add a lightweight overlay/modal host to `MainView` (a `Panel` layered over
   the existing content, toggled by an `IsTrackInfoOpen` bool) that hosts
   `TrackInfoView` when open — this works identically on desktop and mobile
   since it's just another control in the single view.
3. On desktop, keep the current windowed presentation by wrapping
   `TrackInfoView` in a real `Window` *only* when
   `Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime`;
   otherwise show the overlay. One `OpenTrackInfo()` entry point, branching
   once at the presentation layer.
4. Verify keyboard nav (prev/next track, Cmd+I) still works through the
   overlay path — no window-specific focus assumptions leak in.

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

**Effort:** Medium for Windows/Linux hardening; Large for verified mobile
support (depends on device/emulator access). **Risk:** Medium — desktop
behavior must not regress while doing this.

---

## 5. Persistence paths assume desktop OS conventions

**Problem:** `LibraryStore`, `PlaylistStore`, and `ColumnVisibilityStore` all
duplicate the same `IsOSPlatform(OSX) ? "~/Library/Application Support/Flower"
: Environment.SpecialFolder.LocalApplicationData` logic, with no branch for
Android/iOS, where that special folder is unreliable/sandboxed differently.

**Plan:**
1. Extract the duplicated path logic into one shared
   `Flower/Persistence/AppDataPath.cs`:
   ```csharp
   public static class AppDataPath
   {
       public static string GetDirectory() => OperatingSystem.IsMacOS()
           ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Flower")
           : OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
               ? FileSystem.AppDataDirectory-equivalent // see step 2
               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flower");
   }
   ```
2. For the mobile branch, use the platform-correct sandbox directory:
   - Android: `Android.App.Application.Context.FilesDir.AbsolutePath` (via a
     small platform-specific accessor injected at startup, since the shared
     project can't reference `Android.App` directly).
   - iOS: `NSFileManager` documents directory
     (`Environment.GetFolderPath(Environment.SpecialFolder.Personal)` is
     actually correct on iOS and maps to the app's Documents folder — verify
     this still holds on current .NET-for-iOS before relying on it).
3. Update `LibraryStore.StorePath`, `PlaylistStore.StorePath`, and
   `ColumnVisibilityStore.StorePath` to call `AppDataPath.GetDirectory()`
   instead of each having their own copy — this also fixes the existing
   code-duplication smell independent of the mobile question.

**Effort:** Small–Medium. **Risk:** Low on desktop (pure refactor, same
paths); needs a real device/emulator check for the mobile branch.

---

## 6. Drag-to-reorder uses a mouse-drag model, no touch handling

**Problem:** `MusicListView.Panel_PointerPressed`/`Panel_PointerMoved`
(playlist reorder feature) starts a drag on any pointer-down-and-move past a
4px threshold with no distinction between mouse and touch input. On a
touchscreen this would fight with normal list scrolling in the playlist view.

**Plan:**
1. Use `PointerPressedEventArgs.Pointer.Type` (`PointerType.Mouse` vs.
   `PointerType.Touch`) to branch behavior:
   - Mouse: keep current behavior (small movement threshold starts drag).
   - Touch: require a long-press (e.g. reuse `ContextRequested`'s
     press-and-hold recognizer, or a short `DispatcherTimer` started on
     `PointerPressed` and cancelled on early `PointerMoved`/`PointerReleased`)
     before entering drag mode, so a normal scroll gesture is not hijacked.
2. Gate this entirely behind `AllowReorder` as today (playlist view only) —
   no change to that gating.
3. Manually verify on an actual touch device/emulator once #3/#4 make mobile
   builds functional enough to reach the playlist view at all — this item is
   blocked on those in practice, even though the code fix itself is small and
   independent.

**Effort:** Small. **Risk:** Low, but **unverifiable** until mobile builds
work end-to-end (blocked by #3/#4).

---

## 7. Android target is on `net7.0`, out of step with the rest of the solution

**Problem:** `Flower.Android.csproj` targets `net7.0-android` while
`Flower.csproj`, `Flower.iOS.csproj`, `Flower.Desktop.csproj`, and
`Flower.CLI.csproj` all target `net8.0`/`net8.0-ios`. Combined with the
still-default `ApplicationId>com.CompanyName.AvaloniaTest<` and iOS's
`RuntimeIdentifier` hardcoded to `iossimulator-x64` (device codesigning
commented out), both mobile targets look like untouched scaffolding rather
than maintained targets.

**Plan:**
1. Bump `Flower.Android.csproj` to `net8.0-android` (matching current .NET
   for Android support) and do a clean `dotnet build` to catch any API-level
   breakage from the version bump before touching anything else.
2. Set a real `ApplicationId` (e.g. `com.<yourdomain>.flower`) and reasonable
   `ApplicationVersion`/`ApplicationDisplayVersion` in
   `Flower.Android.csproj`.
3. For iOS, leave `RuntimeIdentifier=iossimulator-x64` for local dev but
   document (in `CLAUDE.md`) the additional steps needed for a real-device
   build (`RuntimeIdentifier=ios-arm64`, `CodesignKey`, provisioning profile)
   rather than leaving them as silently-commented-out lines with no
   explanation.
4. Once #1–#6 land, do a first real build+launch on an Android emulator and
   iOS simulator and capture the result (crash log, blank screen, or working
   shell) as a baseline — this hasn't been done at all yet as far as this
   audit could tell.

**Effort:** Small for the version/config bump; the "real build+launch"
verification step depends on everything above being in place first.
**Risk:** Low for the bump itself; net7→net8 could theoretically surface
Android-specific API breaks, but nothing in the current shared code looks
net7-specific.

---

## Suggested execution order

1. **#7** (Android net8 bump + config) — cheap, unblocks meaningful testing
   of everything else on Android.
2. **#5** (persistence paths) — small, low-risk, removes duplication
   regardless of mobile timeline.
3. **#2** (`Process.Start` gating) — small, self-contained.
4. **#1** (`TrackInfoWindow` → overlay) — medium, no external dependencies.
5. **#4** (LibVLC bootstrap hardening) — do the Windows/Linux robustness part
   independently of mobile; the mobile half now has something to play
   against, since #3 is done.
6. **#3** (mobile importers) — **done**, see the item itself for what
   actually shipped instead of the original proposal.
7. **#6** (touch-aware drag) — small fix, verification no longer blocked on
   #3 (done); still blocked on #4's mobile half.
