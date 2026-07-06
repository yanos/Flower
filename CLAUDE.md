# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Flower is a cross-platform music player built with Avalonia UI (.NET 8, C#). It runs on **Windows, macOS, and Linux** (desktop) and **iOS and Android** (mobile). Every feature must work across all these platforms. It uses LibVLC for audio playback and TagLib# for reading audio metadata. The architecture targets all platforms through a shared `Flower` library project with platform-specific entry points.

**Planning docs** (repo root, not yet implemented unless noted) hold investigation/design decisions too long-lived for this file:
- `CROSS-PLATFORM-PLAN.md` — audit + remediation plan for the seven biggest gaps blocking real iOS/Android support (secondary windows, `Process.Start` platform branching, mobile import, LibVLC bootstrap, persistence paths, touch drag, Android `net8.0` bump). **Item #3 is superseded** — see next bullet.
- `SYNC-PLAN.md` — desktop↔phone sync investigation. Key decision: **iOS owns its files directly (its own Documents-folder library via TagLib), no `MPMediaLibrary`/Apple Music integration** — this supersedes `CROSS-PLATFORM-PLAN.md` item #3's `MPMediaLibrary` proposal. WiFi/LAN sync (LocalSend-protocol-style) is the recommended transport; Bluetooth is out; USB is a free manual Finder/Explorer drag-and-drop fallback, not a library to build. Phase 1 (mDNS discovery) and Phase 2 (playlist metadata sync) are done; Phase 3 (full library metadata sync + on-demand audio download, incl. a trust/pairing gate and the mobile download-button UI) is planned but not yet built.
- `SERVER-PLAN.md` — self-hosted server sync investigation (server holds canonical library; distinct feature from the P2P sync above). Recommendation: build an OpenSubsonic API client first (talks to Navidrome etc., no server code needed), Jellyfin client second, first-party `Flower.Server` only as an optional Phase 3.
- `MOBILE-PLAN.md` — phased plan to get `Flower.iOS`/`Flower.Android` from scaffolding to actually runnable. Phases 0–2 (tooling, app-shell fixes, per-platform data import — iOS's own sandboxed Documents folder, Android's `AndroidMediaStoreImporter`) are done. **Phase 3 (the shared mobile UI, `MobileMainView`/`MobileMainViewModel`) has a first pass done and validated against real music on both an Android emulator and iOS simulator** — playback confirmed working. What's left there: a full-screen now-playing sheet, track info as a page, touch drag-to-reorder, search/filter UI, playlist management, mobile settings/rescan access, and empty-state messaging (currently a blank screen).
- `CLI-PLAN.md` — plan for growing `Flower.CLI` past its current one-shot player script.
- `MusicListView-plan.md` — design doc for the current track list control; see "Track List (`MusicListView`)" below for what actually shipped.
- `AIRPLAY-BLUETOOTH-PLAN.md` — output-routing plan: in-app Bluetooth device picker on Windows/macOS/Linux (Phase 1, via LibVLC's existing device-enum API), real AirPlay + Bluetooth on macOS/iOS via `AVAudioEngine`/`AVRoutePickerView` (Phase 2 — keeps LibVLC for decode, redirects only the output stage using `MediaPlayer.SetAudioCallbacks`, confirmed present in LibVLCSharp 3.10.0 on every target), Android needs no work (Phase 3, OS already routes Bluetooth audio transparently). AirPlay on Windows/Linux is explicitly out of scope — no viable sender protocol exists there.
- `AUDIOPHILE-PLAN.md` — plan for EQ with a true bypass mode (confirmed available via LibVLCSharp's `UnsetEqualizer`), low-latency/minimal-overhead LibVLC init, DSD (`.dsf`)/Monkey's Audio (`.ape`) import+tagging support (cheap — TagLib# already supports both), a pragmatic near-gapless preload step, and multi-channel/hi-res (24-bit/192kHz+) sample-rate passthrough (the last item depends on `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's device picker landing first). **DSD/APE *playback* is confirmed NOT natively supported** by the installed VLC's plugin set (no `ape`/`dsd` demux/decode modules found) — scoped as its own larger effort (third-party plugins or a custom decode path), not a smoke test. True sample-accurate gapless is scoped as a stretch goal sharing `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's custom PCM pipeline rather than being built twice.
- `AUTO-UPDATE-PLAN.md` — desktop-only (Windows/macOS/Linux; mobile is out of scope, App Store/Play Store already own updates there) auto-update plan built on **Velopack**, chosen over a hand-rolled updater or NetSparkle specifically because its `GithubSource`/`vpk upload github` make GitHub Releases a first-class, built-in target rather than something to wire up manually. Needs a versioning foundation (no git tags/`<Version>` exist yet) before packaging, and macOS specifically needs a paid Apple Developer ID + notarization setup for auto-updated builds to pass Gatekeeper cleanly.
- `CRASH-REPORTING-PLAN.md` — survey of options for surfacing crashes, layered on top of the file logging in `Flower/Logging/AppLogging.cs`. App Center is confirmed dead (retired). Recommends starting with a zero-infrastructure prefilled-GitHub-issue-URL flow (no token/server/account, satisfies `docs/todo.txt`'s "make an issue link"), with **Sentry** (confirmed Avalonia-compatible, its `Sentry.Extensions.Logging` package plugs directly into the same `ILoggingBuilder` call already in `App.axaml.cs`) as the next step if/when aggregation across crashes is worth the third-party dependency, and self-hosted **GlitchTip** (same SDK, same code, different DSN) as the privacy-conscious alternative to Sentry's cloud.
- `STORE-DEPLOYMENT-PLAN.md` — the phase after `MOBILE-PLAN.md`: getting `Flower.iOS`/`Flower.Android` from runnable to actually submitted and approved. **Two build-config blockers found already sitting in the repo**: `Flower.Android.csproj` still produces an `apk` (Play Console requires `.aab`), and `Flower.iOS.csproj` targets `net9.0-ios18.0` (Apple requires the iOS 26 SDK — i.e. `net10.0-ios26.2` — for any submission as of April 2026). Also covers the required iOS Privacy Manifest, Google Play's 12-tester/14-day closed-testing gate for new personal accounts, LibVLC's LGPL dynamic-linking requirement (why `LICENSE`/`NOTICE` exist, still uncommitted), and promotes several of `MOBILE-PLAN.md`'s open Phase 3 items (blank empty-state, no permission-retry path) from polish to submission blockers.
- `PERFORMANCE-TRACKING-PLAN.md` — two complementary halves: CI benchmark regression tracking via **BenchmarkDotNet** + the **`github-action-benchmark`** GitHub Action (native `benchmarkdotnet` tool support, PR/commit-comment alerts, no GitHub Pages required for a v1), targeting `TrackListBuilder`'s sort/filter and `Library.UpdateTracks`'s merge as the highest-value hot paths; and runtime/production timing, extending the `Stopwatch`+`ILogger` pattern the rescan logging (`App.axaml.cs`) already established, escalating to Sentry performance transactions only if `CRASH-REPORTING-PLAN.md`'s Sentry option is actually adopted (same SDK, not a second tool).

## Build & Run

```bash
# Build the desktop app
dotnet build Flower.Desktop/Flower.Desktop.csproj

# Run with hot reload
dotnet watch run --project Flower.Desktop/Flower.Desktop.csproj

# Publish
dotnet publish Flower.Desktop/Flower.Desktop.csproj
```

```bash
# Run unit tests
dotnet test Flower.Tests/Flower.Tests.csproj
```

`Flower.Tests/` is an xUnit project covering pure logic in the shared library: `TrackListBuilder` (filter/sort including `DateAdded`/album-grouping/playlist-order), `Playlist` (insert/append/remove/next/previous), `Library` (track/playlist mutation and events, including `DateAdded` preservation across a rescan), `PlaylistControlViewModel` (repeat/shuffle toggling and their effect on `Next()`, via a minimal in-memory `IAudioManager` fake), and `LibraryStore`/`PlaylistStore`/`AppSettingsStore` round-trip serialization. The store tests redirect `HOME` to a temp directory for their duration so they never touch the real `library.json`/`playlists.json`/`settings.json`. `PlaylistControlViewModelTests` deliberately never raises the fake's `EndReached` event, since that handler's `Dispatcher.UIThread.Post` callback needs a running Avalonia dispatcher this headless test host doesn't have — so repeat/shuffle are exercised through `Next()` (same underlying `GetNextTrack` logic) instead.

## Git Workflow

Prefer `rebase` over `merge` when bringing a branch up to date (e.g. reconciling a feature branch with `master`).

Never run `git commit` without first showing the user the change and getting their explicit approval, even mid-task or between otherwise-approved steps. Approval only covers the change shown at that moment — it does not carry over to later, separate changes in the same session, even ones requested right after a "commit this." Ask again each time.

Do not use git worktrees (e.g. `.claude/worktrees/...`) when working in this repo — they conflict with Rider's project indexing/build system on this machine. Edit `master` directly instead. If the tooling forces isolation for a background session and a worktree gets created anyway, keep its lifetime as short as possible and remove it immediately after merging into `master`.

## Code Style

Do not use single-line `if` statements. The body always goes on its own line below the `if`, even when it's a single statement:

```csharp
// Not this:
if (track == null) return;

// This:
if (track == null)
    return;
```

## Project Layout

| Project | Purpose |
|---|---|
| `Flower/` | Shared library: all UI, ViewModels, Models, business logic |
| `Flower.Desktop/` | Entry point for macOS/Windows/Linux desktop |
| `Flower.Android/` | Android entry point |
| `Flower.iOS/` | iOS entry point |
| `Flower.CLI/` | Standalone CLI (minimal, mostly unused) |
| `Flower.Tests/` | xUnit tests for the shared library |

All meaningful code lives in `Flower/`.

## Architecture

The app uses MVVM with Avalonia compiled bindings and `CommunityToolkit.Mvvm` source generators. Dependency injection is done via `Microsoft.Extensions.DependencyInjection` with `CommunityToolkit.Mvvm.DependencyInjection.Ioc` as the service locator.

**Startup flow** (`App.axaml.cs`):
1. Load cached library synchronously from JSON so UI appears immediately with data.
2. Register all services as singletons in `Ioc.Default`.
3. Show `MainWindow` → `MainView` with `MainViewModel`.
4. Fire a background `Task.Run` to rescan the configured library paths, update the library, and persist the result.

**Key classes:**

- `Track` — immutable `record` holding all metadata (title, artist, album, duration, file path, etc.) plus audio-technical fields (bitrate, sample rate, codec) and `DateAdded` (when the track first appeared in the library — see `Library.UpdateTracks` below).
- `Library` — holds the canonical `List<Track>` and fires `TracksUpdated` when the background import finishes. `UpdateTracks` carries each track's original `DateAdded` forward by matching on `Path` against the previous `Tracks` list — a rescan (`Importer`) builds brand-new `Track` instances from file tags every time, each defaulting `DateAdded` to "now", so without this every track would look freshly added after every relaunch.
- `MainPlaylist : Playlist` — the ordered play queue; drives next/previous track navigation.
- `IAudioManager` / `VlcAudioManager` — thin wrapper over LibVLC's `MediaPlayer`. Raises events (`Playing`, `Paused`, `EndReached`, etc.) that ViewModels subscribe to.
- `MainViewModel` — owns the track list displayed in the UI, sidebar navigation (Recently Added / Songs / Albums / Artists / Playlists), live text filtering (250 ms debounce), column visibility, and the status bar text. Also drives "go to currently playing track" (Cmd/Ctrl+L): switches sidebar/sub-list scope and clears an active search only if needed so the track is guaranteed visible, then asks `MainView` to scroll to it. Recently Added keeps its own independent sort column/direction (`_recentlyAddedSortColumn`/`_recentlyAddedSortAscending`, default `DateAdded` descending) rather than sharing Songs/Albums/Artists' single sort state, so clicking a column header there doesn't change what Songs is sorted by and vice versa — see `SortColumn`/`SortAscending`/`SortByColumn`.
- `PlaylistControlViewModel` — play/pause/next/previous logic, plus `IsRepeatEnabled`/`IsShuffleEnabled` toggles (persisted to `settings.json`, restored on next launch). Receives `EndReached` and auto-advances to the next track on the UI thread via `Dispatcher.UIThread.Post`; repeat replays the same track on natural end-of-track, shuffle (via `GetNextTrack`) picks a random different track instead of the next one in playlist order. Both only affect auto-advance/`Next()` — manual `Previous()` always stays sequential.
- `CurrentlyPlayingControlViewModel` — seek slider and elapsed/total time, updated from `IAudioManager.PositionChanged` events. Also forwards `IsRepeatEnabled`/`IsShuffleEnabled` from `PlaylistControlViewModel` for the repeat/shuffle icon buttons in `CurrentlyPlayingControl`.
- `Importer.Importer` — recursively scans the paths configured in Settings (`AppSettings.LibraryPaths`) for `.mp3`, `.m4a`, `.wav`, `.flac`, `.alac` files and reads tags with TagLib#. Falls back to `~/Music` if no paths are configured. `AppSettingsStore.Load()` auto-registers Apple Music's configured media folder on first load if one is found and not already listed.
- `PlatformShortcuts.Primary` (`Flower/Services/`) — `KeyModifiers.Meta` on macOS, `KeyModifiers.Control` elsewhere. Every keyboard shortcut should be defined against this, not a hardcoded modifier, so it stays correct cross-platform; this is also the intended seam for making shortcuts user-configurable later.

**Persistence** (macOS: `~/Library/Application Support/Flower/`):
- `library.json` — full track list serialized with `System.Text.Json`; `TimeSpan` stored as ticks.
- `playlists.json` — playlists (track references only — `PlaylistStore` resolves them against the loaded library so playlist data never duplicates track metadata).
- `config.json` — per-column state (width, visibility, order) for the track list, managed by `ColumnManager`.
- `settings.json` — `AppSettings` (`LibraryPaths`, main window geometry, and the `IsRepeatEnabled`/`IsShuffleEnabled` toggle state), managed by `AppSettingsStore`.

**VLC on macOS**: `VlcNativeSetup.Initialize()` looks for VLC at `/Applications/VLC.app`. It sets `VLC_PLUGIN_PATH` and loads `libvlccore.dylib` from the app bundle before calling `Core.Initialize()`. VLC must be installed for audio playback to work.

## UI Structure

`MainView.axaml` is the root layout (three rows: top bar, content, status bar):

- **Top bar**: `PlaylistControls` (prev/play/next buttons), `VolumeControl`, `CurrentlyPlayingControl` (seek + track info, center — also has borderless repeat/shuffle icon buttons flanking the seek bar, greyed out by default and solid black while enabled), `FilterControl` (search box, right).
- **Content**: Sidebar `ListBox` (Recently Added / Songs / Albums / Artists / Playlists), resizable via a `GridSplitter` between it and the main content column, + optional sub-list for album/artist drill-down + `MusicListView` track list (see "Track List" below) with resizable/sortable/hideable columns.
- **Status bar**: activity spinner (`IsBusy` / `BusyMessage`) and song/album count + total duration.

**Keyboard shortcuts** — all defined against `PlatformShortcuts.Primary` (Cmd on macOS, Ctrl on Windows/Linux), not a hardcoded modifier:
- Track list (tunnel-routed in `MainView.axaml.cs` so `MusicListView`'s own key handling doesn't swallow them first): `Space` — play/pause; `Enter` — play selected track; `Cmd/Ctrl+I` — open Track Info window.
- Global (tunnel handler at the `MainView` root, works regardless of focused control): `Cmd/Ctrl+,` — open Settings; `Cmd/Ctrl+L` — scroll to the currently playing track, switching sidebar view/clearing the search first if that's what's hiding it.

Column visibility/width/order is changed via right-click context menu on the column header (visibility) or drag-resize (width); all three are persisted automatically via `ColumnManager` → `config.json`.

## Binding Notes

- Compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`) are used by default. `MusicListView.axaml` opts out with `x:CompileBindings="False"` (its content is mostly assembled in code-behind — header bar, rows); `TrackRowControl.axaml` opts back in (`x:CompileBindings="True"`, `x:DataType="vm:TrackRowViewModel"`) since each row is a normal templated control.
- The `Duration` column uses `Mode=OneWay` explicitly to avoid a `ConvertBack` error from `DurationConverter`.
- All audio callbacks from LibVLC arrive on background threads; any UI update must be marshalled with `Dispatcher.UIThread.Post(...)`.

## Track List (`MusicListView`)

The track list is a hand-rolled control (`Flower/Controls/MusicListView.axaml(.cs)`, `MusicListPanel.cs`, `TrackRowControl.axaml(.cs)`), not a stock grid control. Design/history is in `MusicListView-plan.md`; it replaced two earlier attempts:

- **ListBox** — abandoned; manual column widths and sort arrows, no built-in resize.
- **TreeDataGrid / `FlatTreeDataGridSource<T>`** — abandoned; requires a paid Avalonia license (`AVLIC0001` build error with no free workaround).
- **`DataGrid`** — abandoned in favor of the current custom control (see below) for album-art spanning and full control over virtualization; the `Avalonia.Controls.DataGrid` package reference is still in `Flower.csproj` but is otherwise unused.

**Current architecture:**
- **Flat row list, uniform height** — every item is a `TrackRowViewModel` (`Flower/ViewModels/TrackRowViewModel.cs`) at `TrackRowViewModel.RowHeight`. Album grouping (`IsFirstInAlbumGroup`, `AlbumGroupSize`) is a computed property of each row, not a structural header row — `TrackListBuilder` (`Flower/Services/`) computes it whenever sort/filter/grouping changes.
- **Album art spanning** — the first row of an album group renders art `AlbumGroupSize × RowHeight` tall with `ClipToBounds="False"`, so it visually bleeds down over the following rows in that group, which leave their art cell empty.
- **Virtualization** (`MusicListPanel : Panel`) — simple uniform-height math (`firstVisible = floor(scrollOffset / RowHeight)`, no prefix-sum), with a small `TrackRowControl` pool that's grown but never shrunk (hidden instead) as the viewport changes. An album-group's leader row is always kept in the active set even if scrolled just above the viewport, so its spanning art doesn't pop in/out.
- **Columns** — `ColumnManager` owns the list of `MusicColumnDefinition`s (width/visibility/order — including `DateAdded`, header "Added", showing `Track.DateAdded` as `MMM d, yyyy`), fires `ColumnsChanged` when any of those change, and persists to `config.json`. Resize is a pointer-drag on the header's right edge; hide/show is the header's right-click context menu. `TrackListBuilder.Sort` treats `DateAdded` as a normal toggleable column like any other (ascending/descending via the header arrow); the Recently Added sidebar view separately forces its own default of `DateAdded` descending — see `MainViewModel` above.
- **Keyboard**: `Enter`/`Space` are intercepted in `MainView.axaml.cs` via `RoutingStrategies.Tunnel` so `MusicListView`'s own `OnKeyDown` (arrow-key navigation) doesn't run first and swallow them.
- **Scrolling to a specific track** — `MusicListView.ScrollToTrack(track)` selects the row and centers it in the viewport; used by the Cmd/Ctrl+L "go to currently playing" shortcut.

The Track Info window (`TrackInfoWindow`/mobile `TrackInfoView`, opened via Cmd/Ctrl+I) shows `DateAdded` as a read-only field in its Technical section, alongside Duration/Codec/File.
