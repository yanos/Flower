# Cross-Platform Gaps — Remediation Plan

Cross-platform audit of `Flower/` against the five targets (macOS, Windows, Linux, iOS, Android). One section per finding.

**Summary: of the original 7 items, only #2 (`Process.Start`/`IPlatformShell` gating) is still unbuilt.** Everything else is done — #5 and #7 closely match the original proposal; #1, #3, #6 shipped via a different design than sketched. Item #8 (bundle libvlc natives, macOS) was added later and is not started.

---

## 1. Secondary windows break on mobile (`TrackInfoWindow`) — done, different design

Proposed a shared `TrackInfoView` behind one `OpenTrackInfo()` entry point. Shipped instead: two independent implementations — desktop kept `TrackInfoWindow` (a real `Window`), mobile got its own `Flower/Views/Mobile/TrackInfoView.axaml(.cs)` (a full-screen sheet), fields duplicated rather than shared. Simpler given the UIs had already diverged; cost is manual sync if fields change. See `MOBILE-PLAN.md` Phase 3.

---

## 2. `Process.Start` calls fail on iOS, wrong on Android/Linux — open

`MainView.LocateFile()`/`MainViewModel.OpenDatabaseLocation()` only branch Mac/Windows/else-as-Linux. `Process.Start` is unusable on iOS (sandboxed) and unsupported on Android.

**Plan:** introduce `IPlatformShell.TryRevealInFileManager(path)`, DI-registered per platform. Desktop implementation = today's `Process.Start` logic moved behind the interface. Android/iOS implementation returns `false`. `MainView.LocateFile()` hides the menu item when it returns `false` instead of silently failing.

Effort: small. Risk: low.

---

## 3. Importer had no mobile-appropriate media access path — done, superseded design

Proposed an `IMusicSource` abstraction with iOS reading via `MPMediaLibrary` (Apple Music integration). **Rejected**: `MPMediaLibrary` has no write access (DRM-restricted), which would permanently block `SYNC-PLAN.md`'s WiFi push-to-phone and USB drag-and-drop.

Shipped instead: `Flower.Importer.IMusicImporter`, with iOS reading its own sandboxed Documents folder via TagLib (like desktop scans `~/Music`). Android got `AndroidMediaStoreImporter` via `MediaStore` + TagLib#, dropping the `is_music != 0` filter since that column is unreliably `NULL` even for real music files. `Track.Path` stays a plain filesystem path on every platform.

Status: done and validated (33 tracks/5 albums on Android emulator + iOS simulator) — see `MOBILE-PLAN.md`.

---

## 4. LibVLC bootstrap hardcoded to macOS desktop install — mostly resolved

`VlcNativeSetup.Initialize()` hardcodes `/Applications/VLC.app` for macOS; other platforms fall through to a bare `Core.Initialize(null)` with no "not found" verification.

**Biggest risk is resolved in practice**: `MOBILE-PLAN.md`'s validation confirms real audio playback on Android emulator, iOS simulator, and a real iPhone via the generic `Core.Initialize()` path. What's still open: splitting per-platform init methods and adding a "VLC not found" UX banner for Windows/Linux instead of a silent failure.

**Linux (fixed, July 2026):** no `VideoLAN.LibVLC.Linux` NuGet ever existed; the distro's `libvlc.so.5` soname isn't matched by .NET's unversioned `libvlc.so` probing, causing a startup crash. Fixed via `NativeLibrary.SetDllImportResolver` mapping `libvlc` → `libvlc.so.5` in `VlcNativeSetup`. Linux users still need VLC installed; bundling is deferred to `AUTO-UPDATE-PLAN.md`'s packaging phase (AppImage/`.deb`).

Windows needs no fix — the maintained `VideoLAN.LibVLC.Windows` NuGet deploys `libvlc.dll` + plugins and `Core.Initialize()` finds them; a real-machine smoke test is still pending.

---

## 5. Persistence paths assumed desktop OS conventions — done, matches plan

`Flower/Persistence/AppDataDirectory.cs` is the proposed shared resolver; every store routes through it. iOS uses its sandboxed Documents folder (`SpecialFolder.Personal`); Android uses `PlatformDataDirectory.Current`, a settable override injected from `Flower.Android`. Validated on real device + emulator.

---

## 6. Drag-to-reorder used a mouse-drag model, no touch handling — done, different mechanism

Proposed branching `PointerType.Mouse` vs. `Touch` on desktop's existing drag code. Shipped instead: mobile's own implementation (`MobileMainView.axaml.cs`'s `DragHandle_Pointer*`) — drag only starts from a dedicated per-row handle icon with a 10px movement threshold, so normal scrolling is never hijacked and no pointer-type branch is needed. See `MOBILE-PLAN.md` Phase 3. Not confirmed whether exercised on a real touchscreen vs. pointer-emulated only.

---

## 7. Android target was on `net7.0`, out of step with the rest of the solution — done, gone further

Originally: `net7.0-android` vs. the rest of the solution's `net8.0`, default `ApplicationId`, iOS RID hardcoded to simulator with device codesigning commented out.

Now: `Flower.Android.csproj` on `net10.0-android` with a real `ApplicationId` (`com.yanos.flower`) and store-release `ApplicationVersion` override. `Flower.iOS.csproj` defaults to `ios-arm64` (device build), simulator RID as opt-in. Both validated with real build+launch (Android emulator, iOS simulator, real iPhone) — see `MOBILE-PLAN.md`.

Remaining thread (tracked in `STORE-DEPLOYMENT-PLAN.md`, not here): iOS TFM needs a further bump for App Store submission, and Android still produces `.apk` not the `.aab` Play Console requires — both are store-submission blockers, not runtime gaps.

---

## 8. Bundle libvlc natives ourselves (macOS first) — not started

Windows/Android/iOS are self-contained via maintained NuGets; macOS and Linux require a user-installed VLC. macOS's official NuGet is abandoned (3.1.3.1, ~2019), hence the `/Applications/VLC.app` workaround.

**Decision (July 2026):** package natives ourselves **only for macOS**. Explicitly skip Windows/Android/iOS (already maintained) and Linux (no portable official build exists; AppImage/Flatpak/`.deb` solve this at packaging time instead — tracked in `AUTO-UPDATE-PLAN.md`).

**Plan:** extract `lib/`+`plugins/` from the official VLC dmgs (Intel + Apple Silicon RIDs) → prune to what an audio player needs (demux, audio codecs/output, meta readers, http/access modules — full tree is ~100MB/arch, pruned should land ~30-50MB) → audit for GPL plugins surviving the prune (rest is LGPL, already covered by repo-root `NOTICE`) → ship via an MSBuild copy target in `Flower.Desktop.csproj` → simplify `VlcNativeSetup`'s macOS branch to load from the app directory, keeping `/Applications/VLC.app` as a fallback during transition → re-sign bundled dylibs at app-signing time (folds into `AUTO-UPDATE-PLAN.md`'s notarization work).

Once done, Linux is the only platform where "VLC not found" is a reachable user-facing state.
