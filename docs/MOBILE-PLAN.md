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
- **Phase 3** (mobile UI) — **done**, including everything originally listed
  under "What's left" below (now folded into this status block; see git
  history for that section if the original wording is ever needed).
  `Flower/Views/Mobile/MobileMainView` + `Flower/ViewModels/Mobile/MobileMainViewModel`,
  wired into `App.axaml.cs`'s single-view branch. Tab/bottom-nav (Recently
  Added/Songs/Albums/Artists/Playlists), touch-sized rows, mini-player bar,
  plus: a full-screen now-playing sheet (`NowPlayingView`, seek slider +
  large art); Track Info as a sheet (`TrackInfoView`, the mobile counterpart
  to desktop's `TrackInfoWindow`); touch drag-to-reorder for playlist tracks
  (`MobileMainView.axaml.cs`'s `DragHandle_Pointer*` handlers); search/filter
  (a toggleable filter box bound to the same `MainViewModel.FilterText` desktop
  uses); playlist management (`AddToPlaylistView`, `TrackActionsView`'s "..."
  row menu, create/add-to-playlist commands); mobile Settings access
  (`SettingsView` — rescan, Android permission retry via
  `OpenAppSettingsCommand`, and by now also Appearance/Device Name, tabbed the
  same way desktop's Settings window is — see `SettingsWindow.axaml`); and
  empty-state messaging (`IsContentEmpty`/`EmptyStateTitle`/`EmptyStateMessage`)
  instead of a blank screen. Verified at runtime on an Android emulator and
  both the iOS simulator and a real iPhone.
- Artist drill-down was reworked mid-session too: tapping an artist now shows
  that artist's own album-tile grid (reusing the Albums tab's presentation)
  before the flat song list, instead of dumping every song by that artist
  into one list straight away.

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

## What's left

Phase 3 itself is done in full (see "## Status" above). What's still open
for mobile overall:
- Real-device verification of album art rendering on Android (confirmed
  working on iOS simulator/device and Android emulator; Android album art on
  a physical device is untested — see "Not yet tested" note above).
- Swapping the placeholder 5-album test set (pulled from a real Apple Music
  library, not committed) for a small royalty-free fixture set that can
  actually be committed for repeatable testing.
- Store-submission-level polish (permission-retry UX beyond the basic
  Settings entry point, background playback on Android) is tracked in
  `STORE-DEPLOYMENT-PLAN.md`, not here.
