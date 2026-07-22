# Running the Mobile Apps (iOS + Android) — Plan

## Goal

Get `Flower.iOS`/`Flower.Android` from untouched scaffolding to actually runnable. Most of the work — especially the mobile UI — is shared between the two via the same `ISingleViewApplicationLifetime` branch in `App.axaml.cs`; only tooling, native package wiring, and data-import are platform-specific.

## Status — all phases done

- **Phase 0/1** (tooling, app-shell): both `ios`/`android` workloads installed; real bundle identity; explicit `LibVLCSharp.Avalonia`/`VideoLAN.LibVLC.*` refs; `Flower.Android.csproj` on `net10.0-android`.
- **Phase 2** (getting music onto the device): iOS's `Importer` points at the sandboxed Documents folder (Finder drag-and-drop enabled); Android has its own `AndroidMediaStoreImporter` with a runtime permission flow; persistence paths resolve via a shared `AppDataDirectory` resolver.
- **Phase 3** (mobile UI): `MobileMainView`/`MobileMainViewModel` wired into the single-view branch — tab nav, touch-sized rows, mini-player, full-screen now-playing sheet, `TrackInfoView` sheet, touch drag-to-reorder, search/filter, playlist management, mobile Settings (rescan, Android permission retry, Appearance/Device Name), and empty-state messaging. Artist drill-down shows that artist's own album-tile grid before the flat song list.

Validated against 5 real albums (33 tracks) on an Android emulator, iOS simulator, and a real iPhone — Songs/Albums correctly show titles/artists/durations/grouping on both platforms. Caught and fixed a real bug: `AndroidMediaStoreImporter`'s `is_music != 0` filter silently excluded every track (MediaStore's `is_music` is unreliably `NULL`, and `NULL != 0` is neither true nor false) — fixed by dropping the filter (`audio/media` is already audio-only).

## What's left

- Real-device verification of Android album art rendering (confirmed on iOS + Android emulator; untested on a physical Android device).
- Swapping the placeholder 5-album test set (pulled from a real Apple Music library, not committed) for a small committable royalty-free fixture set.
- Store-submission-level polish (deeper permission-retry UX, Android background playback) is tracked in `STORE-DEPLOYMENT-PLAN.md`, not here.
