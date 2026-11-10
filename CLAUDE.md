# CLAUDE.md

Flower is a cross-platform music player built with Avalonia UI (.NET 10, C#)
for Windows, macOS, Linux, iOS and Android; every feature must work on all of
them. It decodes with a small FFmpeg façade (`ffaudio`, the FFAudio.NET NuGet),
renders with miniaudio and reads tags with TagLib#. All meaningful code lives in
the shared `Flower/` library; the heads are thin entry points.

## How It Gets Used

**Plex, but only for music, and smaller.** One owner runs one server, with a
handful of friends or family also listening.

- **No scale work.** Single-digit concurrent listeners, one library: no
  sharding, job queues, horizontal scaling or per-tenant isolation.
- **Not single-user either.** Listeners have accounts and connect from outside
  the house, so auth, per-device pairing, rate limiting and the trust boundary
  (`PeerSignatureAuth` plus `LanGuard`) are load-bearing. "It's just for me"
  reasoning never applies there.
- **The owner is technical; listeners are not.** Server setup may ask for a
  config file or a terminal command; a listener's phone asks for nothing beyond
  a link or a pairing screen. Trade server-side complexity for client-side
  simplicity (`docs/REMOTE-TRANSPORT-PLAN.md`).
- **`Flower.Server` is the only server.** A client browses mDNS without
  advertising, pairs by redeeming an admin-issued code, and accepts no incoming
  connections (`docs/SYNC-PLAN.md`, "Peer-to-peer, built and removed").

## No Users Yet

No released users and no data anyone depends on. **Backward compatibility is
not a constraint**: change the sync protocol, on-disk JSON, the server DB
schema, config keys and defaults outright, and delete the old path rather than
keeping a fallback. Flower speaks only its own protocol (OpenSubsonic was
removed; `docs/SYNC-PLAN.md`, "OpenSubsonic, built and removed").

## Docs

`docs/` holds one file per initiative, each recording its own status and what
is left. Read the relevant one before touching that area.

- `SELF-HOSTING.md` — user-facing: running a server and reaching it remotely.
- `MANAGING-SONGS.md` — user-facing: adding, removing, editing and replacing songs per setup (unpaired, owner, listener), and how a song is recognised.
- `API.md` — HTTP reference for `Flower.Server`; update it when a route in `Flower.Server/Endpoints/` changes.
- `SECURITY.md` — the server trust boundary: per-listener playlists, per-device budgets, `FlowerProblem` refusals, `ServerResponseSignature`.
- `REMOTE-ACCESS.md` — client side of remote access: candidate addresses, LAN↔tailnet handover, `CleartextOrigins`.
- `REMOTE-TRANSPORT-PLAN.md` — Tailscale vs Cloudflare Tunnel vs `tsnet`.
- `CITED-DECISIONS.md` — resolves source comments citing a retired review's tier or finding number, or a fixed finding of the two code reviews.
- `CODE-REVIEW.md` — the standing backlog: the September and October 2026 reviews' open findings, under their review and number, in a suggested order (fixed ones are indexed in `CITED-DECISIONS.md`).
- `SYNC-PLAN.md` — device↔server sync, ingest (admin devices mirroring files, edits, moves and removals to the server), Music.app history, Flower's own folder.
- `AUDIO-QUALITY-PLAN.md` — render-path defects and PCM-level tests, the float pipeline and output stage, and the audiophile features (EQ, gapless, hi-res, DSD, low latency); what is left is at the top.
- `VIBE-PLAYLIST-PLAN.md` — Vibe Playlists and Endless Play (CLAP via ONNX Runtime in `Flower.SemanticAudio.Onnx`, engine in `Flower.Core/SemanticAudio/`).
- `ONLINE-METADATA-PLAN.md` — lyrics, artist pictures, popularity (`OnlineMetadataEngine`).
- `AUTO-UPDATE-PLAN.md` — Velopack updates and MinVer versioning from git tags.
- `AIRPLAY-BLUETOOTH-PLAN.md`, `MEDIA-KEYS-PLAN.md`, `CRASH-REPORTING-PLAN.md`, `STORE-DEPLOYMENT-PLAN.md`, `PERFORMANCE-TRACKING-PLAN.md`, `STREAMING-SERVICES-PLAN.md`.

Issues: GitHub Issues on `yanos/Flower` via `gh`. Domain docs: `CONTEXT.md` and
`docs/adr/` don't exist yet, which is fine.

## Build & Run

```bash
dotnet build Flower.Desktop/Flower.Desktop.csproj                               # Windows/Linux head
dotnet run --project Flower.MacOS/Flower.MacOS.csproj                           # macOS head (needs the macos workload)
dotnet run --project Flower.Server                                              # server + browser UI
dotnet test Tests/Flower.Tests/Flower.Tests.csproj                                # fast, day-to-day (no FFmpeg needed)
dotnet test Tests/Flower.Tests.UI/Flower.Tests.UI.csproj                          # headless views and view models
dotnet test Tests/Flower.Desktop.Tests.slnf -m:1 -p:IncludeWebUi=false             # all desktop suites
scripts/ios-tests.sh                                                                  # the suite on an iOS Simulator
scripts/deploy-ios-device.sh                                                          # physical iPhone, whichever is plugged in
```

- **Every head runs from a clean clone with no setup** (`dotnet run` or the IDE
  Run button). Keep it that way: fix the project file, never add an IDE run
  config (`.idea/`, `.vscode/` are gitignored). `Flower.MacOS.csproj` pins
  `_AppBundleName` for this; its comment says why.
- **Browser UI** (`Flower.Web`) is published by building `Flower.Server`
  (needs the `wasm-tools` workload); `-p:IncludeWebUi=false` skips it.
- **iOS device deploy** takes no device id and runs
  `scripts/register-ios-device.sh` first; it publishes NativeAOT (`--mono` for
  Mono). The script headers explain the signing and runtime details.
- **Throwaway servers:** pass `--Flower:DataDirectory=<scratch>`, and kill every
  `Flower.Server` you start before finishing — a leftover one advertises over
  mDNS and collides with the real server in every client's sidebar:

  ```bash
  pkill -f 'Flower\.Server'; sleep 3; pkill -9 -f 'Flower\.Server'
  lsof -nP -iTCP -sTCP:LISTEN | grep -i flower   # expect no output
  ```

## Tests

Layout, commands, CI, and why the iOS/Android/decoding runners are built as they
are: [Tests/README.md](Tests/README.md).

- **Test behaviour, never looks.** Nothing asserts colours, brushes, pixels,
  fonts, sizes, margins, padding, bounds, relative placement, or a custom
  panel's layout arithmetic. Skip trivial tests too (pass-throughs, getters,
  "forwards the call"). A test breaking these rules gets deleted, not fixed.
- **Where.** UI behaviour and navigation go on the view model; a headless
  window (`Views/`) only for what lives in the view alone (pointer capture,
  focus, a binding's echo). A custom control's logic goes in a class it
  delegates to (`TrackRowSelection`, `NameSelectionDragGesture`).
- **Which project.** Logic with fakes → `Flower.Tests`; anything needing
  Avalonia's dispatcher → `Flower.Tests.UI`; real FFmpeg decoding →
  `Flower.Tests.Decoding` (tagged `RequiresFfmpeg`, the only place that tag
  lives); server → `Flower.Server.Tests`; one desktop OS's native calls →
  `Flower.Tests.macOS`/`.Windows`. Helpers shared by client suites go in
  `Tests/TestSupport/` (linked into each by `ClientTests.props`).
- **App data.** A test whose subject writes app data derives from
  `PinnedDataDirectory` and carries `[Collection("PlatformDataDirectory")]`;
  `TestDataDirectoryIsolationTests` fails a subclass missing the collection.
  An unpinned test still lands in a temp directory (`AssemblySetup`), never
  the real Application Support folder, trash or `~/Music/Flower`.
- **Folders by area, namespace following folder**, one class per file:
  `Audio/`, `Decoding/`, `Playback/`, `MusicLibrary/`, `Playlists/`,
  `Persistence/`, `Sync/`, `Network/`, `Browser/`, `Logging/`,
  `SemanticAudio/`, `ViewModels/`, `Controls/`, `Views/`, `AppShell/`; the
  server suite adds `Endpoints/`, `Security/`, `Configuration/`.
  (`MusicLibrary`/`AppShell`, because a namespace named after a type shadows
  it.)
- Decoding fixtures and `SyntheticWav` live in `Flower.Tests.Decoding.Scenarios`;
  `FLOWER_REQUIRE_DECODERS=FFmpeg` makes a missing decoder fail rather than skip.

## Releasing

Everything is `.github/workflows/tests.yml`. A `v*` tag also publishes the
server image to GHCR and the desktop installers to a GitHub release, gated on
every other job; keep the publish jobs in that file with every job in their
`needs`. A tag is what every installed desktop is offered as an update.
Details: Tests/README.md, "CI".

## Git & Style

- Commit only when the user asks; passing tests is not enough.
- Prefer `rebase` over `merge`. Work on the checked-out branch; no git
  worktrees (they conflict with Rider).
- `if` bodies always go on their own line.

## Project Layout

| Project | Purpose |
|---|---|
| `Flower/` | Shared library: UI, view models, models, logic |
| `Flower.Core/` | Code shared with the server |
| `Flower.Desktop/` | Windows/Linux head (runs on macOS without Apple frameworks) |
| `Flower.MacOS/` | macOS head, `net10.0-macos` for AppKit/AVKit |
| `Flower.Android/`, `Flower.iOS/` | Phone heads |
| `Flower.Server/`, `Flower.Web/` | The server and its browser UI |
| `Flower.SemanticAudio.Onnx/` | CLAP encoders on ONNX Runtime, referenced only by heads that host the engine |
| `Tests/` | See Tests/README.md |

## Architecture

MVVM with Avalonia compiled bindings and `CommunityToolkit.Mvvm` source
generators; DI via `Microsoft.Extensions.DependencyInjection`, service-located
through `Ioc.Default`.

**Startup** (`App.axaml.cs`): load the cached library from `flower.db`
synchronously → register services → show the window → background rescan.
A first desktop launch that finds a Music.app library asks before importing it
(`ITunesLibraryOffer`).

**Persistence** (macOS: `~/Library/Application Support/Flower/`): `flower.db`
(SQLite via `FlowerDb`: library and playlists), `settings.json` (`AppSettings`,
column state included), `queue.json` (`PlayQueueStore`).

**Key classes:**
- `Track` — immutable metadata record; `DateAdded` is carried across rescans by `Library.UpdateTracks` matching on `Path`.
- `Library` — canonical track list; `LibraryChanged` after a rescan or sync merge, `TrackChanged` for in-place changes. Keeps departed songs as `RemovedTrack`s so a returning file gets its identity back.
- `MainPlaylist : Playlist` — the play queue. `QueueViewModel` shows it as a page on both heads.
- `IAudioManager` / `GaplessAudioManager` — playback; `GaplessCoordinator` runs decode-ahead.
- `MainViewModel` — track list, sidebar, search, columns, status bar, Cmd/Ctrl+L.
- `HomeViewModel` — Home shelves (Continue Playing via `AlbumProgressTracker`/`AlbumProgressSyncService`, Recently Played, Recently Added).
- `PlaylistControlViewModel` — transport, repeat/shuffle; `CurrentlyPlayingControlViewModel` — seek bar.
- `Importer.Importer` — recursive TagLib# scan of the library folders.
- `LibraryFolderWatcher` (`Flower.Core`) — live folder watching plus periodic listing; anything else falls back to a full rescan.
- `DeviceLibraryFolders.ScanPaths` — what a device scans. Everything that scans, watches or names a file asks it, never `AppSettings.LibraryPaths` directly.
- `PlatformShortcuts.Primary` — Meta on macOS, Control elsewhere; every shortcut uses it.

**Server protocol:** one surface, `/api/flower/v1/*` (`MediaEndpoints`,
`SyncEndpoints`). Every route takes a device signature, or a stream ticket for
the two media routes. Bulk, art and media are budgeted separately, and art is
fetched in batches (`CoverArtBatch`) — both because a cover-art burst once
starved playback (`docs/CITED-DECISIONS.md` #2b). Admin-device ingest and
deletion semantics (a deletion is a removal, never a delete) are in
`docs/SYNC-PLAN.md`, "Ingest".

**Audio pipeline:**
- `FfmpegTrackDecoder` is the only decoder. FFAudio.NET's version is pinned by
  `FFAudioPackageVersion` in `Directory.Build.props`; the library lives in
  `../FFAudio.NET`, and `scripts/use-latest-main-ffaudio-package.sh` builds
  against an unpublished change.
- The pipeline carries float32 from the decoder to `OutputStage`; only its
  sample rate is negotiated (once, at startup, then frozen). `OutputStage`
  requantises to the device's format (`DeviceSampleFormat`, S24 unless a device
  refuses it, settled again at every open), with its dither and clamp following
  that format. On every platform the stage runs as C in the render callback of
  `libflowerbridge` (`native/miniaudio/flower_audio_bridge.c`), a port held to
  `OutputStage` sample for sample: change one, change both.
  `NativeOutputStageChecks` compares the two byte for byte on the phone
  runners and in `Flower.Tests` on each desktop. The managed callback is only
  a fallback, for a platform with no bridge build or `NativeBufferMs` 0.
- **Native libraries** (`native/miniaudio/`), all vendored as binaries. The
  NuGet ships only desktop miniaudio, so Android and iOS miniaudio is built
  here, pinned to the commit `Miniaudio-CS`'s bindings were generated against
  (0.11.22). The bridge is its own library everywhere (it includes no
  miniaudio), built by `native/miniaudio/{desktop,android,ios}/build.sh`;
  desktop's six RIDs reach the heads and tests through `FlowerBridge.props`.
  Changing the bridge's C means rerunning all three, and CI fails a tree whose
  source stamps (`flowerbridge-source.sha256`) disagree. iOS on Mono also
  needs the `DllImportResolver` in `MiniaudioSink`'s static constructor. See
  `native/miniaudio/README.md`.
- Playback position comes from `GaplessCoordinator.CurrentTrackBytesProduced`
  (ring bytes *read*), never a decoder's own counter, which freezes for a track
  decoded ahead (`_currentTrackReadSplit`'s remarks).

## UI

- **Phones:** use `TextInput`, never a bare `TextBox`, for anything a phone can
  reach — it swaps in the platform's native field while focused.
- **Context menus:** open with `PlatformContextMenu.Open(menu, target)`, never
  `menu.Open(...)` (macOS draws them as `NSMenu`s). XAML `Control.ContextMenu`
  needs nothing.
- **Shortcuts:** `Space` play/pause, `Enter` play selected, Cmd/Ctrl+`I` track
  info, `,` settings, `L` scroll to now playing. Track-list shortcuts are
  tunnel-routed in `MainView.axaml.cs` so `MusicListView` doesn't swallow them.
- `PlaybackControlsPlacement`'s row is set by a style on `ContentGrid`, because
  a local value outranks a style.
- Columns persist through `ColumnManager` → `AppSettings.ColumnStates`.
- Compiled bindings are on by default; `MusicListView.axaml` opts out,
  `TrackRowControl.axaml` opts back in. `Duration` binds `Mode=OneWay`.
- Decode callbacks arrive on background threads; marshal UI updates with
  `Dispatcher.UIThread.Post(...)`.

**Track list** (`MusicListView`, `MusicListPanel`, `TrackRowControl`) is
hand-rolled: `ListBox`, `TreeDataGrid` (paid) and `DataGrid` could not do
column resize, art spanning grouped rows and virtualization together. Rows are
flat and uniform-height; album grouping is computed (`TrackListBuilder`), and
art spans down via `ClipToBounds="False"` on the group's first row.
