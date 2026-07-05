# Running the Mobile Apps (iOS + Android) — Plan

## Goal

Get `Flower.iOS` and `Flower.Android` from "untouched scaffolding" to
actually runnable, usable apps. Most of the real work — especially Phase 3,
the mobile UI — is **shared** between the two, since both go through the
same `ISingleViewApplicationLifetime` branch in `App.axaml.cs`. Only
tooling, native package wiring, and the data-import model are genuinely
platform-specific.

## Status

- **Phase 0** (tooling) — done. Both `ios` and `android` dotnet workloads
  installed; Android SDK installed at `~/Library/Android/sdk`.
- **Phase 1** (mechanical app-shell fixes) — done. Both platforms have real
  bundle identity, explicit `LibVLCSharp.Avalonia`/`VideoLAN.LibVLC.*`
  references, `Flower.Android.csproj` is on `net10.0-android`.
- **Phase 2** (getting music onto each platform) — done. iOS's `Importer`
  points at the sandboxed Documents folder (`UIFileSharingEnabled` +
  `LSSupportsOpeningDocumentsInPlace` for Finder drag-and-drop); Android has
  its own `AndroidMediaStoreImporter` (`Flower.Android/`) with the runtime
  permission flow wired through `MainActivity`; persistence paths
  (`AppSettingsStore`/`LibraryStore`/`PlaylistStore`/`ColumnVisibilityStore`)
  resolve correctly on both via a shared `AppDataDirectory` resolver.
- **Phase 3** (mobile UI) — first pass done: `Flower/Views/Mobile/MobileMainView`
  + `Flower/ViewModels/Mobile/MobileMainViewModel`, wired into
  `App.axaml.cs`'s single-view branch. Tab/bottom-nav (Songs/Albums/Artists/
  Playlists), touch-sized rows, mini-player bar. Verified at runtime on both
  an Android emulator and iOS simulator.

**Validated against real music.** 5 albums (33 tracks) from different
artists copied onto both an Android emulator (`adb push` to `/sdcard/Music`
+ a MediaStore rescan) and an iOS simulator (directly into the app
container's Documents folder) — Songs and Albums screens both correctly
show real titles/artists/durations/grouping on both platforms.

This also caught a real bug: `AndroidMediaStoreImporter`'s
`WHERE is_music != 0` filter silently excluded every track, since
MediaStore's `is_music` column is unreliably `NULL` rather than `1` even
for genuine music files, and SQL `NULL != 0` is neither true nor false.
Fixed by dropping the filter entirely (`audio/media` is already
audio-only). Playback confirmed working (tapping a track plays it) on
device. Not yet tested: album art rendering.

The 5 albums used for this (pulled from a real Apple Music library, not
committed) are placeholders — swap for royalty-free tracks once picked, to
commit a small fixture set for repeatable testing.

## What's left in Phase 3

- **Full-screen now-playing sheet** — tapping the mini-player does nothing
  beyond play/pause right now; no expanded view with seek slider/larger art.
- **Track Info as a page/sheet** — `TrackInfoWindow` is still a desktop-only
  `Window`; no way to view track details on mobile.
- **Touch-aware drag-to-reorder for playlists** — the desktop mouse-drag-
  threshold model isn't touch-friendly and isn't implemented on mobile.
- **Search/filter UI** — `MainViewModel.FilterText` exists but nothing in
  `MobileMainView` exposes it.
- **Playlist management** — no way to create a playlist or add a track to
  one from mobile (desktop does this via right-click context menus).
- **Settings access** — no way to see/retry the Android MediaStore
  permission if denied, or trigger a rescan, from mobile.
- **Empty-state messaging** — an empty library currently just renders a
  blank white screen.
