# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Flower is a cross-platform music player built with Avalonia UI (.NET 10, C#), running on Windows, macOS, Linux, iOS, and Android. Every feature must work across all platforms. Uses LibVLC for playback and TagLib# for metadata. Shared `Flower` library project + platform-specific entry points.

## Planning Docs

`docs/` holds long-lived design/investigation notes, one file per initiative. Check the relevant file before touching that area — each records its own current status and what's left; this index is intentionally just a pointer, not a summary.

- `CROSS-PLATFORM-PLAN.md` — iOS/Android platform-gap remediation.
- `SYNC-PLAN.md` — desktop↔phone sync + self-hosted server (same OpenSubsonic client protocol).
- `MOBILE-PLAN.md` — getting iOS/Android from scaffolding to runnable.
- `CLI-PLAN.md` — growing `Flower.CLI` past its current one-shot script.
- `AIRPLAY-BLUETOOTH-PLAN.md` — Bluetooth device picker + AirPlay output routing.
- `AUDIOPHILE-PLAN.md` — EQ, gapless playback, DSD/APE, hi-res passthrough.
- `MEDIA-KEYS-PLAN.md` — hardware media keys + OS now-playing integration.
- `AUTO-UPDATE-PLAN.md` — desktop auto-update via Velopack.
- `VERSIONING-PLAN.md` — git-tag-driven versioning via MinVer.
- `CRASH-REPORTING-PLAN.md` — crash reporting options.
- `STORE-DEPLOYMENT-PLAN.md` — submitting iOS/Android to app stores.
- `PERFORMANCE-TRACKING-PLAN.md` — CI benchmark regression tracking + runtime timing.
- `STREAMING-SERVICES-PLAN.md` — feasibility of streaming Spotify/Apple Music/YouTube Music.
- `ALBUM-GRID-PLAN.md` — design record for the Albums/Recently Added art-tile grid.

## Build & Run

```bash
dotnet build Flower.Desktop/Flower.Desktop.csproj
dotnet test Flower.Tests/Flower.Tests.csproj
```

`Flower.Tests/` covers `TrackListBuilder`, `Playlist`, `Library`, `PlaylistControlViewModel`, and the JSON stores — xUnit tests against pure logic in the shared library.

## Git Workflow

- Prefer `rebase` over `merge`.
- Never commit before the user has personally tested the change — building and passing tests isn't enough.
- Don't use git worktrees in this repo — they conflict with Rider. Edit `master` directly.

## Code Style

`if` bodies always go on their own line, never on the same line as the `if`.

## Project Layout

| Project | Purpose |
|---|---|
| `Flower/` | Shared library: all UI, ViewModels, Models, business logic |
| `Flower.Desktop/` | macOS/Windows/Linux entry point |
| `Flower.Android/` | Android entry point |
| `Flower.iOS/` | iOS entry point |
| `Flower.CLI/` | Standalone CLI (minimal, mostly unused) |
| `Flower.Tests/` | xUnit tests for the shared library |

All meaningful code lives in `Flower/`.

## Architecture

MVVM via Avalonia compiled bindings + `CommunityToolkit.Mvvm` source generators. DI via `Microsoft.Extensions.DependencyInjection`, service-located through `Ioc.Default`.

**Startup** (`App.axaml.cs`): load cached `library.json` synchronously so the UI has data immediately → register services in `Ioc.Default` → show `MainWindow`/`MainView` → background rescan updates and persists the library.

**Key classes:**
- `Track` — immutable metadata record, plus `DateAdded` (first-seen date, carried forward across rescans by `Library.UpdateTracks` matching on `Path`).
- `Library` — canonical track list; fires `TracksUpdated` after each background rescan.
- `MainPlaylist : Playlist` — the play queue.
- `IAudioManager` / `VlcAudioManager` — LibVLC wrapper; raises playback events ViewModels subscribe to.
- `MainViewModel` — track list, sidebar navigation, search, columns, status bar, and the Cmd/Ctrl+L "scroll to now playing" flow. Recently Added has its own independent sort state from Songs/Albums/Artists.
- `PlaylistControlViewModel` — play/pause/next/previous, repeat/shuffle (persisted in `settings.json`). Shuffle/repeat only affect auto-advance and `Next()`, never manual `Previous()`.
- `CurrentlyPlayingControlViewModel` — seek bar + elapsed/total time.
- `Importer.Importer` — recursive scan of `AppSettings.LibraryPaths` (mp3/m4a/wav/flac/alac) via TagLib#, falling back to `~/Music`.
- `PlatformShortcuts.Primary` — Meta on macOS, Control elsewhere; all shortcuts should reference this, not a hardcoded modifier.

**Persistence** (macOS: `~/Library/Application Support/Flower/`): `library.json`, `playlists.json` (track references only, resolved against the library), `config.json` (column state), `settings.json` (`AppSettings`).

**VLC native libraries** (`VlcNativeSetup.Initialize()`): Windows and Android/iOS are self-contained via NuGet. macOS requires VLC.app installed (the official NuGet is abandoned) and Linux requires a system VLC install (no NuGet exists), with a `DllImportResolver` mapping `libvlc` → `libvlc.so.5`. A missing VLC on macOS/Linux still hard-crashes at startup — friendly UX for that is still open (`CROSS-PLATFORM-PLAN.md`).

## UI Structure

`MainView.axaml`: top bar (playlist controls, volume, seek/track info, search) → content (sidebar + optional drill-down sub-list + `MusicListView` track list) → status bar.

Keyboard shortcuts (all via `PlatformShortcuts.Primary`): `Space` play/pause, `Enter` play selected, `Cmd/Ctrl+I` track info, `Cmd/Ctrl+,` settings, `Cmd/Ctrl+L` scroll to now playing. Track-list shortcuts are tunnel-routed in `MainView.axaml.cs` so `MusicListView`'s own key handling doesn't swallow them.

Column visibility/width/order persist via `ColumnManager` → `config.json`.

## Binding Notes

- Compiled bindings are on by default; `MusicListView.axaml` opts out (code-behind assembled), `TrackRowControl.axaml` opts back in.
- `Duration` column binds `Mode=OneWay` to avoid a `ConvertBack` error.
- LibVLC callbacks arrive on background threads — marshal UI updates via `Dispatcher.UIThread.Post(...)`.

## Track List (`MusicListView`)

Hand-rolled control (`Flower/Controls/MusicListView.axaml(.cs)`, `MusicListPanel.cs`, `TrackRowControl.axaml(.cs)`) — replaced `ListBox` (no built-in resize), `TreeDataGrid` (needs a paid license), and `DataGrid` (couldn't do album-art spanning/virtualization the way this needs).

- Flat, uniform-height row list (`TrackRowViewModel.RowHeight`); album grouping is a computed property (`IsFirstInAlbumGroup`/`AlbumGroupSize`) from `TrackListBuilder`, not a structural header row.
- Album art spans down over grouped rows via `ClipToBounds="False"` on the group's first row.
- `MusicListPanel` virtualizes with simple uniform-height math and a grow-only `TrackRowControl` pool.
- `ColumnManager` owns column definitions and persists to `config.json`; `TrackListBuilder.Sort` treats `DateAdded` like any other sortable column.
- `MusicListView.ScrollToTrack(track)` selects and centers a row — used by Cmd/Ctrl+L.
