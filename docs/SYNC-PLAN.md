# Sync & Self-Hosting — Investigation & Plan

## Goal

Two related goals, unified by one decision below: sync music (files + metadata) between Flower desktop (Windows/macOS/Linux) and Flower mobile (iOS/Android) across all six desktop×phone permutations; and support "self-hosting," where a server (user's NAS/home box/VPS, or another Flower app on the network) holds a canonical library that other Flower apps stream/sync against.

## Architecture prerequisite (decided)

iOS owns its music files itself — its own sandboxed Documents-folder library, imported/read via TagLib, same as desktop's `Importer` scans `~/Music`. **No `MPMediaLibrary`/Apple Music integration.** Supersedes the `MPMediaLibrary` proposal in `CROSS-PLATFORM-PLAN.md` item #3. `Track.Path` stays a plain filesystem path on iOS.

**Why:** `MPMediaLibrary` has no external write access (DRM-restricted) — syncing *to* iOS would be impossible under any transport. Owning files directly unblocks both USB and WiFi sync.

---

## The unifying decision: one OpenSubsonic client, three interchangeable servers

Peer-to-peer WiFi sync and self-hosted-server support were originally scoped as separate protocols. They're now one client protocol with three interchangeable things on the other end.

- **Protocol: OpenSubsonic** (actively-maintained open successor to the classic Subsonic API, JSON-first, backwards compatible). Building a client means **zero server code** to get self-hosting working — point Flower at Navidrome, Gonic, Airsonic-Advanced, or Ampache.
- **Target server: Navidrome** (Go, Docker-first, drives OpenSubsonic's evolution). Feature coverage confirmed sufficient: browsing, ranged `/stream`, playlist CRUD, favorites, cover art, search, scrobble.
- **Auth**: classic `token=md5(password+salt)` or OpenSubsonic's API-key extension.
- **No usable .NET client library existed**, so Flower hand-rolled one. **Done**: `OpenSubsonicClient`/`OpenSubsonicContracts.cs` (`Flower/Services/`) — auth, ID3-based browsing (`getArtists`/`getArtist`/`getAlbumList2`/`getAlbum`/`getSong`), `search3`, playlist CRUD, star/scrobble, and URL builders for `stream`/`download`/`getCoverArt`. Unit tested against a fake `HttpMessageHandler`. Not yet wired into UI/Settings/an `IMusicImporter` backend.

**The insight that reshapes both halves of this doc:** once Flower speaks OpenSubsonic as a client, the server on the other end can be a third-party Navidrome/Jellyfin instance, a first-party headless `Flower.Server`, or **another Flower app on the network hosting the protocol embedded, in-process, no separate server**. All three look identical to the client.

- **Flower.Desktop hosts the OpenSubsonic API itself, in-process, with no database** — a thin mapping layer over the `Library` already loaded in memory. Unlike a standalone `Flower.Server`, which needs SQLite/EF Core because it's headless.
- **Mobile's sync client and the self-hosting client are the same code** — just two different base URLs.

### Staged path to "always available to sync with"

An embedded-in-the-GUI server only serves while Flower.Desktop is open. Natural staged path toward always-on:

1. **Tray/menu-bar mode** — closing the window hides it instead of quitting; embedded host keeps serving. Cheap.
2. **Auto-start on login** (`LaunchAgent`/Windows startup/systemd `--user`). Per-OS installer plumbing, not new sync logic.
3. **A true headless daemon** — survives logout, starts at boot. This is really `Flower.Server` deployed locally instead of on a NAS/VPS; `Flower.CLI` (`CLI-PLAN.md`) is the natural process to register as an OS service.

Stages 1-2 are cheap; stage 3 is real OS-service-installer work and isn't worth building speculatively.

### Sequencing: don't split `Flower.Core` out until there's a concrete reason to

The OpenSubsonic contracts/client and the embedded host are built directly in the existing `Flower` project — no Avalonia/LibVLC boundary to cross yet. Only extract `Flower.Core` once something needs to run where it *can't* reference Avalonia/LibVLC (stage 3 above, or a standalone `Flower.Server`). See "Project structure" below for the deferred design.

---

## Recommendation: build WiFi/LAN sync, skip Bluetooth, treat USB as secondary

### 1. WiFi/LAN sync — the transport to actually build

**Why:** desktop and mobile are the same .NET/Avalonia codebase — no OS-vendor device protocol, no native interop.

- **Discovery**: `Makaretu.Dns.Multicast.New` for mDNS (`Zeroconf` was rejected — browse/resolve only, can't advertise). Verified macOS ↔ iOS Simulator. Android needs a `MulticastLock` (implemented, **not yet verified on a real device**). iOS 14+ needs `NSBonjourServices`/`NSLocalNetworkUsageDescription` (implemented, working).
- **Transfer protocol**: reimplements [LocalSend's open protocol](https://github.com/localsend/protocol) (HTTPS+JSON, self-signed certs, mDNS + HTTP fallback) rather than inventing one. **Phase 1 done**: device identity exchange + Devices sidebar, over plain HTTP for now. **Phase 2 done**: playlist metadata sync (no audio yet):
  - `Playlist.Id` (stable Guid) + `UpdatedAt` timestamp; `Track.SyncKey` (Title+Artists+Album+Duration fingerprint) matches tracks across devices since `Path` is local-only. A synced playlist can only reference tracks present on both devices so far.
  - `SyncHttpServer` endpoints: `GET /api/flower/v1/playlists`, `POST /api/flower/v1/playlists/apply`. `PlaylistSyncPlanner` (pure, unit tested) does a three-way merge against a persisted baseline (`PlaylistSyncStateStore`); real conflicts prompt the user (`PlaylistConflictWindow`).
  - `PlaylistSyncService` elects one side (deterministic fingerprint compare) to drive each sync session and pushes the fully-merged manifest back so both sides agree without independent resolutions.
  - **Done since**: playlist deletion sync (`PlaylistSyncPlanner`'s `Delete` decision kind) and resync-on-local-edit while still connected (see `MainViewModel.ScheduleContentSync` below).
- **Server**: `HttpListener`, not Kestrel — Kestrel/ASP.NET Core hosting isn't available on iOS/Android. `HttpListener`'s HTTPS support outside Windows is a long-standing gap (`dotnet/runtime#19752`), so phase 1 uses plain HTTP and defers TLS/trust to file-transfer time.
- **Critical iOS constraint**: iOS suspends the process (and its listener) within seconds of backgrounding — sync needs both apps open in the foreground. Android tolerates this better but battery optimization can still throttle it.
- **Rejected alternatives**: Syncthing, KDE Connect, Resilio Sync, Dukto — all have no reliable non-foreground iOS story or aren't embeddable in .NET. Useful only as prior art.

**Effort:** Medium–Large. **Risk:** Low–Medium (iOS foreground-only window is the main platform risk).

### 2. USB — keep it cheap and manual, don't build a programmatic library

| | Android phone | iOS phone |
|---|---|---|
| **Windows** | Easy (`MediaDevices` NuGet) | Easy manual (Files tab), moderate programmatic |
| **macOS** | Hard — no native Finder MTP | Easy manual (Finder Files tab) |
| **Linux** | Easy on GNOME (`gvfs-mtp`) | Moderate — programmatic-only |

Plan: ship `UIFileSharingEnabled`/`LSSupportsOpeningDocumentsInPlace` on iOS (free, Info.plist only) for a Finder/iTunes drag-and-drop path; document Android's existing MTP file-transfer mode as the supported flow. **Do not** build a one-click sync button backed by a programmatic USB library.

**Why not build one anyway (investigated, rejected):** Windows MTP still requires WPD (raw `libusb` can't coexist with it without breaking Explorer's own access). macOS/Linux MTP is the one piece genuinely worth writing (~1-3 weeks), but iOS AFC means reimplementing `usbmuxd`/`lockdownd`/pairing/TLS — either porting LGPL-licensed `libimobiledevice` logic (real licensing risk, not a dynamic-link exception case) or a slower clean-room rebuild, and Apple has broken this stack before (iOS 17 broke `libimobiledevice`/`ifuse` pairing). **Verdict**: skip it — the cheap transport (WiFi) already beats a cable-bound one requiring manual Android mode-switching.

### 3. Bluetooth — dropped entirely

No supported path for bulk Bluetooth file transfer from an iOS app to an arbitrary desktop (`ExternalAccessory` needs MFi certification; `CoreBluetooth` BLE is ~12-28 KB/s — a 5MB song takes 3-7 minutes). Android Classic RFCOMM is faster (~1-3 MB/s) but iOS being blocked makes this a non-starter as a unified transport, and it's 50-100x slower than WiFi regardless. Not worth building.

---

## Phase 3 — Full library sync and on-demand audio download

**Goal:** today a synced playlist can only reference tracks present on *both* devices. This phase makes a peer's whole library known everywhere (metadata only) and lets the user pull actual audio for one track on demand (the mobile download button).

**Protocol: OpenSubsonic**, reusing the same client built above (`getIndexes`/`getArtists`/`getAlbumList`/`getSong` for browsing, `stream`/`download` for audio). Still on `HttpListener`, not Kestrel (either device may be the one holding a file). Trust/auth is Flower's own fingerprint-based pairing gate (below), not OpenSubsonic credentials, between two Flower devices. `Track.SyncKey` is still the cross-device identity — an OpenSubsonic id is only stable within one server's own scan.

**Confirmed real problem, changed after real-world testing:** the original per-album (`getAlbumList2`+`getAlbum`) catalog fetch produced 1000+ HTTP connections against a 1,397-album library, causing real network/battery cost on iOS. `LibrarySyncService` now uses a bespoke bulk endpoint instead — `GET /api/flower/v1/library` returning the whole manifest in one response, same shape as the playlist endpoint. The standard `/rest/getAlbumList2`/`getAlbum` endpoints are unchanged for real OpenSubsonic interop; only Flower-to-Flower bulk sync moved off them.

### Data model

`Track.Path` is already nullable — a track with `Path == null` is metadata known via sync but not locally downloaded. `Track.OriginDeviceFingerprint` tracks which peer currently has the file.

### Trust gate

Phase 1/2 deferred trust for plain HTTP; this phase needs it before handing over audio on request. **Done**: `TrustedPeerStore` (`trusted-peers.json`) persists approved `(Fingerprint, Alias, ApprovedAt)` entries; denials aren't persisted (re-prompted next time). `SyncHttpServer.AuthorizeAsync` gates every `/api/flower/v1/*` path (only `/api/localsend/v2/info` stays open, since trust can't be evaluated before a peer's fingerprint is known). Unrecognized fingerprint → `PeerApprovalRequested` → `ConfirmDialogWindow` prompt; unanswered after 60s or no UI listening denies by default (unlike playlist-conflict's "keep local" default, this has no safe implicit default). Revoke via `TrustedDevicesWindow` ("Trusted Devices…" in the app menu). Still plain HTTP — trust here means *authorization*, not encryption; acceptable for a same-LAN threat model, revisit if sync ever leaves the local network. Unit tested (`StoreRoundTripTests.cs`).

### Merge behavior

On receiving a peer's catalog: match by `SyncKey`. Already present as a real file + peer has a live copy → update `OriginDeviceFingerprint` to the closer peer. Not present → insert a placeholder `Track` (`Path = null`). Never delete a local, `Path`-backed track just because a peer doesn't mention it. Symmetric/bidirectional, same discovery-triggered flow as playlist sync (`LibrarySyncService` alongside `PlaylistSyncService`). Side benefit: `PlaylistSyncMapper.ResolveTracks` already matches against the local library, so a placeholder-referencing playlist just works once placeholders exist.

### Mobile UI: the download button

Mobile-only for v1 (desktop has no "not fully local" track concept and enough storage not to need it). **Done**: a `Path == null` row renders dimmed with a download icon in place of the normal action affordance; tap elsewhere is a no-op until downloaded. Download resolves the peer via `OriginDeviceFingerprint` against currently-discovered devices, streams `GET /rest/stream` to the platform's normal import location, sets `Track.Path`, persists, and fires `Library.NotifyTrackChanged()` (lighter than `UpdateTracks` — no add/remove, same `Track` reference). `Track.OriginFileExtension` carries the extension across the wire since `Path` doesn't exist yet at receive time. `SyncHttpServer` gained `GET /rest/stream`; `LibraryDownloadService` does the resolve/download/persist; `TrackRowViewModel` gained `IsPlaceholder`/`IsDownloading`/`IsDownloadUnavailable`/`IsDownloadIdle` (static icon swap, not an animated spinner — v1 simplification).

**Known gap, deliberately accepted:** on Android, a downloaded file lands in app-private storage, not MediaStore-indexed — `Library.UpdateTracks`'s carry-forward was widened so it survives a rescan that doesn't independently find it, but it's not independently rediscoverable. **Not yet verified on a real Android device.** iOS doesn't have this gap. The download flow is unit-tested only (`LibraryOpenSubsonicMapperTests`/`LibrarySyncMapperTests`/`LibraryTests`), not yet exercised end-to-end against a real peer on either platform.

### Additional Phase 3 work beyond the original scope

- **Album art sync — done.** `Track.OriginAlbumArtHash` (SHA-256 of the origin's art bytes) is the cache key `AlbumArtLoader`'s remote-fetch path uses against `GET /rest/getCoverArt`; a changed hash is just a cache miss. Art decoding was moved to a background thread after it was found to stall UI scrolling on the main thread.
- **Play count sync — done, not originally scoped.** `Track.RemotePlayCounts` (`Dictionary<fingerprint, count>`) is a small G-Counter CRDT — each device stamps its own contribution, receivers merge by per-key max (safe under repeats/reordering/relay). A device never accepts a peer's report of its own key back. Rides the existing bulk-catalog sync.
- **Resync on local change — done.** `MainViewModel.ScheduleContentSync` debounces (5s, restarts on every call) and re-syncs on `Library.TracksUpdated`/`PlaylistsUpdated`, guarded by an in-flight counter to avoid a merge's own events triggering an infinite resync loop.
- **A real data-corruption bug, found and fixed.** Four call sites (play-count-on-end, tag edits, iTunes import) called `UpdateTracks(Library.Tracks)` just to fire the refresh event, which doubled every sync placeholder each time (already-present + carried-forward again) — produced a multi-GB `library.json` in practice. Fixed by switching those sites to `Library.NotifyTrackChanged()`.
- **Device sidebar dedup by fingerprint** — matching was by raw mDNS instance name, which collides for two devices sharing a default computer name; now matches by `Fingerprint` once resolved.

### Deliberately deferred, not designed now

Resumable/partial downloads (retry-from-scratch in v1); multi-source download (only the recorded origin is tried); batch actions ("download this playlist/album"); auto-download-on-tap-to-play (kept as an explicit button for now).

**Effort:** Medium (reuses Phase 1/2 machinery; new pieces are the trust gate, streaming endpoint, mobile row UI). **Risk:** Low-Medium, concentrated in the trust gate's default-deny posture and mobile storage/import-path plumbing.

---

## Optional, additive: Jellyfin client support

Many self-hosters already run Jellyfin (MIT-licensed `Jellyfin.Sdk`, separate from the GPLv2 server — plain network client use, no derivative-work concern) for movies/TV and would rather not run a second server for music. Worth adding as a second optional `IMusicImporter` backend once the Subsonic client exists — not a replacement for it, since Jellyfin is ~5-10x heavier (300-800MB RAM idle) and video-first; treat it as "support the server users already have."

## Optional, larger effort, later: first-party `Flower.Server`

For users who want a pure-Flower self-hosted server, or who've outgrown the embedded-in-Flower.Desktop option. Have it speak OpenSubsonic itself so the existing client — and any third-party Subsonic mobile client — works against it for free.

**Recommended stack:** ASP.NET Core Minimal API + Kestrel (range-request streaming via `Results.File(..., enableRangeProcessing: true)`, no custom code, uses `sendfile`); SQLite via EF Core (same as Navidrome — needs WAL mode + explicit `busy_timeout` + `IDbContextFactory<T>` per request, since EF Core 7+ no longer auto-retries `SQLITE_BUSY`, and WAL requires local storage, not NFS/SMB); single admin password + long-lived JWT/API tokens (no OAuth); multi-arch Docker image via `dotnet publish -a $TARGETARCH` + `docker buildx`; no transcoding in v1 (stream originals with range support only, matching Navidrome's default).

**Effort:** roughly 3-5 weeks for one engineer — most of it EF Core schema/migration and SQLite concurrency hardening, not streaming or Docker.

## Project structure: extracting a shared `Flower.Core` library

**Deferred work** — reference design for whenever `Flower.Server` or the always-on daemon stage is actually undertaken, not an early prerequisite (see "Sequencing" above).

`Flower.csproj` pulls in Avalonia/LibVLCSharp/every ViewModel — a headless server can't reference that. What moves into a new `Flower.Core` (dual-targeting `net10.0;net9.0`, matching `Flower.iOS`'s TFM):

- **`Models/`** (`Track`, `Playlist`/`MainPlaylist`, `Library`) — pure move, no Avalonia references today.
- **`Importer/`** (`Importer`, `IMusicImporter`, `PlatformMusicImporter`) — biggest payoff: `Flower.Server`'s scanner becomes the same `Importer.ImportAsync` desktop already uses. Brings `TagLibSharp`/`plist-cil` along.
- **The OpenSubsonic wire-contract module + mapping functions** — already exists in `Flower`, shared three ways (desktop/mobile client, `Flower.Server`). Deliberately excludes `Track.Path` from the DTO (a filesystem path means something different per device) in favor of a `streamUrl`/opaque id.

**Stays in `Flower`:** `LibraryStore`/`PlaylistStore` (server uses SQLite instead — JSON full-rewrite-on-save doesn't fit incremental upserts/foreign keys); `AppSettingsStore`/`ColumnVisibilityStore`/`DeviceIdentityStore`/`PlaylistSyncStateStore` (client-only concerns); `TrackListBuilder`/`TrackRowViewModel` (holds an Avalonia `Bitmap`); `SyncHttpServer`/`NetworkDiscoveryService`/`PlaylistSyncService` (P2P sync is a different feature from `Flower.Server`).

**Reuse boundary:** `Importer.ImportAsync()` produces shared `Flower.Core.Track`s → server-internal `TrackEntity` (EF Core) → server-internal mapping to shared `SubsonicSongDto` → HTTP/JSON → OpenSubsonic client maps back to its own `Flower.Core.Track`. Two deliberate seams (`TrackEntity`, `SubsonicSongDto`) keep DB churn and the Path-can't-cross-the-wire rule out of the shared model.

**Mechanical steps:** new `Flower.Core` classlib → `git mv` `Models/`/`Importer/`/the OpenSubsonic contracts+client in, along with their package refs → `Flower.csproj` gets a `ProjectReference` → confirm `Flower.Tests` passes unchanged → scaffold `Flower.Server` (`dotnet new webapi`) referencing `Flower.Core`.

Once `Flower.Core` exists, add a `SubsonicLibraryImporter : IMusicImporter` (and later `JellyfinLibraryImporter`) so "local files" vs. "self-hosted server" is a settings choice via `IMusicImporter`, not a special-cased second code path — this supersedes `CROSS-PLATFORM-PLAN.md` item #3's original `IMusicSource` proposal, which shipped instead as `IMusicImporter`.

## Mobile-specific note: streaming vs. background sync

Don't conflate these on iOS: **active playback while streaming** works fine backgrounded (standard `AVAudioSession` background-audio entitlement — unrelated to the P2P listener finding above, since here mobile is a client making outbound requests). **Bulk library sync/download** is subject to the same foreground constraints as the WiFi sync transport — expect a download queue to pause when fully backgrounded with nothing playing.

---

## Status summary

All numbered steps through Phase 3 are **done**: `CROSS-PLATFORM-PLAN.md` item #3 updated to the private-file-library iOS design; WiFi/LAN discovery + LocalSend-style transfer; `UIFileSharingEnabled` for USB; Bluetooth/programmatic-USB deliberately not built; playlist metadata sync; the OpenSubsonic client; and the full Phase 3 stack (trust gate, embedded host, merge logic, mobile download UI).

**Remaining work:** fold the client into the `IMusicImporter` abstraction as a user-facing settings choice; add Jellyfin as a second `IMusicImporter` backend; real-device Android download-path verification and end-to-end testing against a real peer; and, only once there's concrete demand, extract `Flower.Core` and scaffold `Flower.Server`.
