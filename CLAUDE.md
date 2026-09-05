# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Flower is a cross-platform music player built with Avalonia UI (.NET 10, C#), running on Windows, macOS, Linux, iOS, and Android. Every feature must work across all platforms. Uses LibVLC for playback and TagLib# for metadata. Shared `Flower` library project + platform-specific entry points.

## How It Gets Used

**Plex, but only for music, and smaller.** One person runs one server for
themselves, with maybe a handful of friends or family also listening. That is
the whole deployment model, and it settles arguments in both directions:

- **No multi-tenancy, no scale work.** Single-digit concurrent listeners, one
  library, one owner. Don't reach for sharding, job queues, horizontal scaling,
  or per-tenant isolation — the load is a few streams at once, on hardware the
  owner already has.
- **But it is not single-user either.** Other people do get accounts and do
  listen, sometimes from outside the house. So auth, per-device pairing, rate
  limiting and the trust boundary are load-bearing, not ceremony — the thing
  standing between a shared library and the open internet is
  `PeerSignatureAuth` plus `LanGuard`, and it is the one area where "it's just
  for me" reasoning does not apply.
- **The owner is technical; their listeners are not.** Setup on the server can
  reasonably ask for a config file or a terminal command. Setup on a listener's
  phone cannot ask for anything beyond opening a link or a pairing screen. When
  a design trades server-side complexity for client-side simplicity, take the
  trade — see `REMOTE-TRANSPORT-PLAN.md`, which is that argument in full.
- **`Flower.Server` is the only server.** A client never serves: it browses
  mDNS without advertising itself, pairs by redeeming an admin-issued code, and
  accepts no incoming connections. The app used to be able to host the protocol
  itself, and the cost of keeping both was a Client/Server role concept running
  through the settings screens, the sidebar and two pairing flows — for a
  topology that is a star. See `SYNC-PLAN.md`, "Peer-to-peer, built and
  removed", which also records what that gave up and should come back.

## No Users Yet

The app is a WIP with no released users and no data anyone else depends on. **Backward compatibility is not a constraint** — the sync/pairing protocol, on-disk JSON shapes, the server's DB schema, config keys and defaults can all be changed outright rather than versioned, sentinel-detected, or migrated. Prefer the clean design over the compatible one, and delete the old path instead of keeping a fallback. (Third-party *client* compatibility is a separate question: the OpenSubsonic surface is a published protocol others implement, so changing it means breaking real clients — that one still needs a reason.)

## Planning Docs

`docs/` holds long-lived design/investigation notes, one file per initiative. Check the relevant file before touching that area — each records its own current status and what's left; this index is intentionally just a pointer, not a summary.

- `SELF-HOSTING.md` — the one user-facing document here: running your own server and reaching it remotely. `SYNC-PLAN.md` holds the reasoning behind it.
- `REMOTE-ACCESS-PLAN.md` — the client-side half of remote access: how a paired server stays reachable off the LAN (candidate addresses, LAN↔tailnet handover).
- `REMOTE-TRANSPORT-PLAN.md` — who carries the traffic when neither end has a public address: Tailscale vs Cloudflare Tunnel vs embedding `tsnet`.
- `OPEN-INTERNET-REVIEW.md` — the `LanGuard`/rate-limit/signature read-through that gates turning any remote transport on.
- `ARCHITECTURE-REVIEW.md` — standing whole-codebase backlog: correctness, performance, duplicated sources of truth, test gaps.
- `CROSS-PLATFORM-PLAN.md` — iOS/Android platform-gap remediation.
- `SYNC-PLAN.md` — desktop↔phone sync + self-hosted server (same OpenSubsonic client protocol).
- `MOBILE-PLAN.md` — getting iOS/Android from scaffolding to runnable.
- `AIRPLAY-BLUETOOTH-PLAN.md` — Bluetooth device picker + AirPlay output routing.
- `AUDIOPHILE-PLAN.md` — EQ, gapless playback, DSD/APE, hi-res passthrough.
- `AUDIO-QUALITY-PLAN.md` — render-path defect audit (clicks, looped fragments, truncated tails) and the PCM-level test suite that should prove them fixed.
- `MEDIA-KEYS-PLAN.md` — hardware media keys + OS now-playing integration.
- `AUTO-UPDATE-PLAN.md` — desktop auto-update via Velopack.
- `VERSIONING-PLAN.md` — git-tag-driven versioning via MinVer.
- `CRASH-REPORTING-PLAN.md` — crash reporting options.
- `STORE-DEPLOYMENT-PLAN.md` — submitting iOS/Android to app stores.
- `PERFORMANCE-TRACKING-PLAN.md` — CI benchmark regression tracking + runtime timing.
- `STREAMING-SERVICES-PLAN.md` — feasibility of streaming Spotify/Apple Music/YouTube Music.
- `ALBUM-GRID-PLAN.md` — design record for the Albums/Recently Added art-tile grid.
- `SMART-PLAYLIST-PLAN.md` — rule-based self-updating playlists (iTunes-style smart playlists).

## Agent skills

### Issue tracker

GitHub Issues on `yanos/Flower`, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `CONTEXT.md` at the root, ADRs in `docs/adr/`. Neither exists
yet, and their absence is not a problem to fix. See `docs/agents/domain.md`.

## Build & Run

```bash
dotnet build Flower.Desktop/Flower.Desktop.csproj                               # Windows/Linux head
dotnet build Flower.MacOS/Flower.MacOS.csproj                                   # macOS head, needs `sudo dotnet workload install macos`
dotnet test Flower.Tests/Flower.Tests.csproj --filter 'Category!=RequiresLibVLC&Category!=RequiresFfmpeg'  # fast, day-to-day
dotnet test Flower.Tests/Flower.Tests.csproj                                    # full run, needs a local VLC install
dotnet run --project Flower.Server                                              # server + its browser UI
```

The browser UI (`Flower.Web`) has no run configuration of its own — building
`Flower.Server` publishes it and drops it in beside the binary, so running the
server is all it takes to have it served. That needs the `wasm-tools` workload;
without it the step is skipped and the server serves a page explaining how to get
it. `-p:IncludeWebUi=false` skips it deliberately. See `Flower.Server.csproj`'s
"The browser UI" targets and `Flower.Server/Configuration/WebUiHosting.cs`.

**Kill every `Flower.Server` you start before you finish.** A server left running
keeps advertising itself over mDNS, so it shows up in the sidebar of any Flower
client on the network — under the machine name, colliding with the row of the
server that is actually wanted, and answering (or refusing) requests meant for it.
Several forgotten instances at once is worse: they fight over one name and the row
flaps. Sweep them at the end of any session that ran one:

```bash
pkill -f 'Flower\.Server'; sleep 3; pkill -9 -f 'Flower\.Server'
lsof -nP -iTCP -sTCP:LISTEN | grep -i flower   # expect no output
```

Also pass `--Flower:DataDirectory=<scratch>` to anything throwaway, so a test
instance never writes to `~/Library/Application Support/Flower/Server`.

`Flower.Tests/` covers `TrackListBuilder`, `Playlist`, `Library`, `PlaylistControlViewModel`, the JSON stores, and the gapless audio pipeline (`GaplessRingBuffer`, `TrackDecoder`, `GaplessCoordinator`, `GaplessAudioManager`) — xUnit tests against pure logic plus, for the gapless pipeline specifically, layered coverage: fake-decoder unit tests (fast, no LibVLC), real-LibVLC decode tests against synthetic WAV fixtures generated at test time (tagged `RequiresLibVLC`, need a local VLC install same as the app itself), and full-pipeline playlist integration tests (`PlaylistPlaybackIntegrationTests`) using `Avalonia.Headless` for the `Dispatcher`-driven auto-advance path. `Flower.Tests/TestSupport/` holds the shared fakes (`FakeTrackDecoder`, `FakeAudioSink`, `FakeAudioManager`) and fixture generators (`SyntheticWav`, now in `Flower.DeviceChecks`) these all build on.

`GaplessCoordinator` gives the armed (decode-ahead) role its own independent LibVLC core, separate from current's — two `MediaPlayer`s sharing one core was silently dropping `OnDrain`/`EndReached` under real decode load. See its `_secondCore`/`_cores` remarks and `GaplessCoordinatorRealDecodeTests`' class comment for the full writeup, including a second bug that fix exposed (a fast handover racing `ArmAsync`'s own `PrepareAsync`, fixed in `ArmAsync`).

Playback position (`GaplessAudioManager.Time`/`Position`, the seek bar) is driven off `GaplessCoordinator.CurrentTrackBytesProduced`, which is computed from the shared ring's actual bytes-*read* (real playback consumption), not a decoder's own `BytesProduced` (decode progress) — a decoder that finished decoding ahead before its track was promoted stops producing any new bytes at all, so a decode-side counter reads as permanently frozen at zero for that whole track. See `_currentTrackReadSplit`'s remarks on `GaplessCoordinator`.

## Device Checks

`Flower.DeviceChecks/` answers one question — *does this platform actually turn
a track into the right audio?* — on the platform in question rather than on a
developer's Mac. `DecodeChecks.RunAll()` decodes a synthetic WAV from disk and
over a loopback HTTP server, and compares the result to the fixture's own
samples byte for byte; a decoder producing the right number of bytes and none
of the right ones fails here and passes every byte-count assertion ever
written.

It exists because both streaming bugs so far were invisible to a green desktop
suite: VLC's mp4 demuxer refusing an unseekable stream, then .NET's mobile
`HttpClientHandler` having no synchronous path at all. Both were found by a
person listening to a phone.

The fixture set is the point. `Fixtures/` holds the same two seconds of 440Hz
sine as WAV, FLAC, ALAC, MP3 and AAC, at both 48kHz and 44.1kHz, committed
rather than generated (`Fixtures/regenerate.sh`) because a phone has no
encoder. 44.1kHz is not thoroughness: it is what a music library is actually
made of, and it is the one rate the pipeline resamples, so a 48kHz-only
fixture never touched the resampler. Each is decoded from disk, over the
loopback server, from a server that refuses ranges, and from one that refuses
HEAD - `Flower.Server` maps `/rest/stream` with `MapGet`, so a HEAD to it is a
405 and every real stream reaches its length through the ranged-GET probe
instead. `LoopbackMediaServer` honours the end of a range for the same reason:
serving a whole file to a `bytes=0-0` probe is something no real server does,
and a loopback that did it decoded a track the probe had already delivered.

Which oracle applies is a property of the fixture, not a choice: lossless at
the pipeline's own rate must come back byte for byte, and everything else is
held to `PcmOracle.ToneMismatch` - audible, in tune, right length. That is a
real bar rather than a lowered one, and `PcmOracleTests` is what says so: it
rejects silence, noise, and a wrong tone at the same loudness.

So the checks carry no test framework, no `HttpListener` (iOS has none — hence
`LoopbackMediaServer` on a raw `TcpListener`), and no assertion that is not
about the samples:

```bash
dotnet test Flower.Tests/Flower.Tests.csproj --filter FullyQualifiedName~DeviceChecksTests  # here
scripts/ios-device-checks.sh                                                                # iOS Simulator
```

Two checks are not about decoding at all. The first is authentication:
`LoopbackMediaServer.RequiresFreshNonce` enforces the same single-use-nonce rule
`NonceReplayGuard` does on the real server, and answers a repeat the way the
real one does - Subsonic's error envelope on an HTTP 200. It exists because a
stream URL was signed once, at resolve time, and then fetched several times
(probe, body, reopen), so the probe spent the nonce and the body GET was refused
as a replay. The track got a correct length and ~130 bytes of JSON for audio.
Nothing here could catch it while the loopback authenticated nothing, which is
the general lesson: a check that cannot refuse a request cannot find a bug about
being refused. See `PeerCredentialsHandler` and docs/OPEN-INTERNET-REVIEW.md #2c.

The second is a server answering 429. Being throttled
is not a track failing, and the difference between waiting it out and treating
it as an I/O error is the difference between playback stalling and an album
disappearing - which is what happened, because `/rest` charged one request
budget across browsing, cover art and audio, and a cover-art burst spent it.
`LoopbackMediaServer.RefuseBodiesWith429` reproduces it (bodies only - the
cheap `bytes=0-0` probe got through in the real failure, which is why the
symptom was "the track has a length and plays nothing"). See
docs/OPEN-INTERNET-REVIEW.md #2b for the whole chain and the three places it is
fixed.

The whole suite runs **once per decoder this platform has** (`DecoderUnderTest`,
`AvailableDecoders`), not once. It was written against `TrackDecoder` by name,
so electing `FfmpegTrackDecoder` moved the entire streaming path out from under
every check here while all of them stayed green - and the first thing that
happened was an album playing nothing on a phone. Each decoder is handed its own
sample format rather than the run moving `GaplessFormat`'s process-wide one, so
LibVLC's S16 and FFmpeg's S24 coexist without either reaching the rest of the
test process.

Another is that a decoder must play when started the way pressing play starts
one. `GaplessCoordinator.PrepareAsync` is called only on the decode-ahead path;
`Play()` constructs a decoder and calls `StartDecoding()` on it directly, which
`TrackDecoder` supports by doing its own open there. Every check here prepared
first, so `FfmpegTrackDecoder` requiring a prepare faulted on every press of
play - only tracks that happened to be armed ahead of time opened at all - and
not one check noticed. `... plays the way pressing play plays it` is that shape,
per fixture per decoder, and `FfmpegTrackDecoder.StartDecoding` now opens on its
decode thread when no prepare has happened.

One of those checks is that the catalogued extension is not a fact about the
bytes. `OriginFileExtension` is `Child.Suffix` - it describes a file on the
server's disk, and what arrives is whatever that server chose to send. Both
decoders take it as a demuxer hint, and forcing a demuxer discards the probe
entirely, so a suffix that disagrees with the bytes is not a slow open, it is a
track that will not play at all. `FfmpegTrackDecoder.DemuxerHintFor` therefore
names only the MP4 family (where a hint buys something a probe cannot: a moov
atom at the end of an unseekable stream), exactly as `TrackDecoder.DemuxHintFor`
already did, and `FfmpegDecoder.OpenStream` rewinds and probes when even that
hint will not open the stream.

CI runs the same checks per-OS in the `decode-checks` job, which installs VLC
for them. The two runs are meant to be comparable: when they disagree, the
difference is the platform. Android has no head yet.

## Git Workflow

- Prefer `rebase` over `merge`.
- Never commit before the user has ask for it — building and passing tests isn't enough.
- Don't use git worktrees in this repo — they conflict with Rider. Edit `master` directly.

## Code Style

`if` bodies always go on their own line, never on the same line as the `if`.

## Project Layout

| Project | Purpose |
|---|---|
| `Flower/` | Shared library: all UI, ViewModels, Models, business logic |
| `Flower.Desktop/` | Windows/Linux entry point (still runs on macOS, without Apple frameworks) |
| `Flower.MacOS/` | macOS entry point — `net10.0-macos`, so AVKit/AppKit are reachable (needs the `macos` workload) |
| `Flower.Android/` | Android entry point |
| `Flower.iOS/` | iOS entry point |
| `Flower.Tests/` | xUnit tests for the shared library |
| `Flower.DeviceChecks/` | Functional decode checks that run on any platform, phone included |
| `Flower.DeviceChecks.iOS/` | iOS head that runs them on a simulator or a device |

All meaningful code lives in `Flower/`.

## Architecture

MVVM via Avalonia compiled bindings + `CommunityToolkit.Mvvm` source generators. DI via `Microsoft.Extensions.DependencyInjection`, service-located through `Ioc.Default`.

**Startup** (`App.axaml.cs`): load cached `library.json` synchronously so the UI has data immediately → register services in `Ioc.Default` → show `MainWindow`/`MainView` → background rescan updates and persists the library.

**Key classes:**
- `Track` — immutable metadata record, plus `DateAdded` (first-seen date, carried forward across rescans by `Library.UpdateTracks` matching on `Path`).
- `Library` — canonical track list; fires `TracksUpdated` after each background rescan.
- `MainPlaylist : Playlist` — the play queue.
- `IAudioManager` / `GaplessAudioManager` — playback abstraction and implementation; raises playback events ViewModels subscribe to.
- `MainViewModel` — track list, sidebar navigation, search, columns, status bar, and the Cmd/Ctrl+L "scroll to now playing" flow. Recently Added has its own independent sort state from Songs/Albums/Artists.
- `PlaylistControlViewModel` — play/pause/next/previous, repeat/shuffle (persisted in `settings.json`). Shuffle/repeat only affect auto-advance and `Next()`, never manual `Previous()`.
- `CurrentlyPlayingControlViewModel` — seek bar + elapsed/total time.
- `Importer.Importer` — recursive scan of `AppSettings.LibraryPaths` (mp3/m4a/wav/flac/alac) via TagLib#, falling back to `~/Music`.
- `PlatformShortcuts.Primary` — Meta on macOS, Control elsewhere; all shortcuts should reference this, not a hardcoded modifier.

**Persistence** (macOS: `~/Library/Application Support/Flower/`): `library.json`, `playlists.json` (track references only, resolved against the library), `config.json` (column state), `settings.json` (`AppSettings`).

**VLC native libraries** (`VlcNativeSetup.Initialize()`): Windows and Android/iOS are self-contained via NuGet. macOS requires VLC.app installed (the official NuGet is abandoned) and Linux requires a system VLC install (no NuGet exists), with a `DllImportResolver` mapping `libvlc` → `libvlc.so.5`. A missing VLC on macOS/Linux still hard-crashes at startup — friendly UX for that is still open (`CROSS-PLATFORM-PLAN.md`).

**Miniaudio native libraries** (`native/miniaudio/`): the `Miniaudio-CS` NuGet only ships desktop binaries, so Android (`android/build.sh`, NDK/CMake → `Flower.Android/libs/<abi>/libminiaudio.so`) and iOS (`ios/build.sh`, Xcode → `Flower.iOS/Frameworks/ios-{device,simulator}/miniaudio.framework`) are compiled and vendored directly in-repo instead — no NuGet package, see `native/miniaudio/README.md` to rebuild. Pinned to the exact miniaudio commit `Miniaudio-CS`'s own bindings were generated against (0.11.22), not the latest upstream release, to avoid an ABI mismatch. iOS additionally needs a `DllImportResolver` in `MiniaudioSink`'s static constructor — unlike Android, where naming the output `libminiaudio.so` alone is enough, .NET-for-iOS's default P/Invoke probing doesn't know to look inside an embedded framework's nested bundle path. `App.axaml.cs` now routes every platform, including Android/iOS, to `MiniaudioSink`; `LibVlcRawStreamSink` is kept unreferenced as a fallback for one release cycle (see its own remarks) rather than deleted outright.

**Album art is fetched in batches** (`CoverArtBatch`, `POST /api/flower/v1/cover-art/batch`): a grid asks for one cover per tile, and a library of 1400 albums is 1400 requests during one cold scroll - more than any per-source budget worth having, and when that budget ran out what got refused was playback. `AlbumArtLoader` coalesces a viewport's worth of misses over a 40ms debounce into one request of up to 32 ids; a peer that cannot answer one degrades to the old request-per-album path. Deliberately on Flower's own surface rather than `/rest`, which is a published protocol other clients implement. See `docs/OPEN-INTERNET-REVIEW.md` #2b.

**FFmpeg façade** (`native/ffmpeg/`): `flower-ffmpeg`, an eight-function C façade over `avformat`/`avcodec`/`avutil`/`swresample`, plus `Flower/Audio/Ffmpeg/`'s `FfmpegDecoder` and `FfmpegTrackDecoder : ITrackDecoder`. It exists because LibVLC's `amem` seam truncates every track to 16 bits whatever format is requested — see `docs/AUDIOPHILE-PLAN.md`. Built, not restored: run `native/ffmpeg/macos/build.sh` (or `linux/build.sh`, or `windows/build.ps1`) before the `RequiresFfmpeg` tests, or filter them out — CI builds it on all three desktops and requires it, via `FLOWER_REQUIRE_DECODERS`, so a façade that stops building shows up as a failing check rather than a shorter suite. Windows downloads a pinned LGPL FFmpeg build rather than compiling one, having neither a package manager to ask nor a reason to cross-compile. Mobile is two scripts instead of one, per platform — `<ios|android>/build-ffmpeg.sh` cross-compiles FFmpeg itself, then `build.sh` links it statically into `flower_ffmpeg.framework` per slice or `libflower_ffmpeg.so` per ABI, checked in under `Flower.iOS/Frameworks/` and `Flower.Android/libs/` like miniaudio's. A phone has no package manager to find an FFmpeg in, which is also where the LGPL obligation stops being someone else's build to point at. Read `native/ffmpeg/README.md` before touching it: the per-platform status and the LGPL-only constraint on any shipping build are both there.

**The decoder is elected, and the election decides the bit depth** (`DecoderElection`, `GaplessFormat`, `PcmSampleFormat`): `AppSettings.AudioDecoder` names LibVLC or FFmpeg, `FLOWER_DECODER` overrides it per run, and asking for a decoder this platform has no artifact for falls back to LibVLC with a warning rather than throwing from a decode thread. **FFmpeg is the default**, on every head — LibVLC's `amem` truncates to 16 bits whatever is asked of it, so defaulting to it defaults to a ceiling nobody chose, and the fallback means a head without a loadable façade still plays. `FLOWER_DECODER=libvlc` is how a bad session is recovered without editing `settings.json` back, and the setting is written out on every save, so an existing `settings.json` keeps whatever it already says — a machine that has run the app before needs its `"AudioDecoder"` key changed by hand, not just a newer build.

The canonical PCM format follows from that choice — LibVLC pins the pipeline to S16 because `amem` hardcodes S16N and ignores the requested fourcc, FFmpeg widens it to packed S24 — and `MiniaudioSink` gets a veto: a device that refuses `ma_format_s24` narrows it back and reopens. Negotiated once at startup and frozen, like the sample rate, because a decoder already open cannot change format. `PcmSampleFormat` stops at S24 deliberately: `OutputStage` works in float, whose mantissa is exactly 24 bits, so S32 would be a widening that quietly narrows again.

Two consequences worth knowing before touching the render path. `OutputStage`'s dither and clamp are the destination format's, not S16 constants. And `flower_audio_bridge`'s transport fade reads samples rather than bytes, so `flower_audio_bridge_create` is told the width (`bytesPerSample`, 2 or 3) and refuses any other - a format it cannot fade is one it must not render. It used to walk `int16_t*` unconditionally, which cost `MiniaudioSink` the whole native bridge at S24, i.e. cost electing FFmpeg on a phone the GC-pause resilience the bridge exists for. Changing that signature means rebuilding the vendored `libminiaudio.so`/`miniaudio.framework` binaries.

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
