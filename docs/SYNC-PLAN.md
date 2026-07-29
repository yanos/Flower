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

## Next: first-party `Flower.Server`, headless with a web interface

Promoted from "optional, later" — this is the next initiative. Goal: a headless server
(NAS/VPS/home box) that plays music through a browser-reachable web interface, lets the owner
configure the server through that same interface, and lets new devices request pairing with no
local screen to pop a dialog on. Speaks OpenSubsonic itself so the existing client — and any
third-party Subsonic mobile client — works against it for free.

**Recommended stack:** ASP.NET Core Minimal API + Kestrel (range-request streaming via
`Results.File(..., enableRangeProcessing: true)`, no custom code, uses `sendfile`); SQLite via
EF Core (same as Navidrome — needs WAL mode + explicit `busy_timeout` + `IDbContextFactory<T>`
per request, since EF Core 7+ no longer auto-retries `SQLITE_BUSY`, and WAL requires local
storage, not NFS/SMB); single admin password + long-lived JWT/API tokens (no OAuth);
multi-arch Docker image via `dotnet publish -a $TARGETARCH` + `docker buildx`; no transcoding
in v1 (stream originals with range support only, matching Navidrome's default).

### Web UI framework: Avalonia.Web (Browser/WASM), not Blazor

Blazor Server was the first instinct (better fit for a small, greenfield admin panel), which
raised the question of moving desktop/mobile to Blazor too for consistency. Investigated and
rejected:

- **.NET MAUI Blazor Hybrid has no official Linux support** — a hard blocker, since Flower
  desktop needs Windows/macOS/Linux from one codebase.
- **Photino.Blazor** (the community option that does cover Linux) is explicitly early-stage,
  "not as feature rich as Electron," and its own maintainers just announced leaning on
  AI-assisted triage due to team bandwidth — too much dependency risk to build the whole app
  on.
- **Mobile Avalonia isn't a pain point to escape** — `MOBILE-PLAN.md` confirms it's fully
  working today (real-device-validated audio, import, touch UI, not scaffolding). The
  hand-rolled UI surface (`MusicListView`'s virtualization, `RubberBandScroll`'s custom
  physics, `ScreenStackPanel`'s nav stack) would need full reimplementation in Blazor's DOM
  model, not porting.
- A literal **web/PWA mobile app** (no native shell) was also rejected: iOS PWA background
  audio actively breaks after first launch, storage caps around 50MB, and there's no reliable
  background sync — that would break the gapless LibVLC/Miniaudio engine, the private
  on-device file library, and P2P WiFi sync outright.

So desktop/mobile stay on Avalonia, unchanged — and the decision was then made to keep
**the browser UI on Avalonia too**, via **Avalonia.Web (Browser/WASM)**, rather than introduce
Blazor at all. Avalonia.Web compiles the same Views/ViewModels/`Flower/Controls/` (including
the real `MusicListView`) to WebAssembly via Avalonia's Skia pipeline — the browser UI is a new
platform head alongside `Flower.Desktop`/`Flower.iOS`/`Flower.Android`, not a parallel
Razor-component rewrite. Known tradeoffs accepted going in: a heavier first-load payload than
plain HTML (acceptable for a personal jukebox revisited repeatedly — the browser caches the
bundle), and Skia-canvas rendering means weaker out-of-the-box accessibility/browser-zoom
behavior than real DOM/HTML would have.

**LibVLCSharp and Miniaudio-CS have no WebAssembly build**, so the browser head is the one
place that can't reuse `Flower.csproj` completely unmodified: it needs its own `IAudioSink`
implementation driven by the browser's Web Audio API via JS interop. This reuses the existing
seam (`IAudioManager`/`IAudioSink` already abstract over `LibVlcRawStreamSink` and
`MiniaudioSink`), but matching the native engine's *gapless* quality bar in a browser is new,
real engineering — ship "browser playback works, gaplessness may lag the native engine at
first" as an accepted v1 scope call.

### Remote access without opening router ports: Tailscale, documented not automated

Jellyfin/Navidrome/Plex self-hoster consensus converges on one answer: don't port-forward — a
directly port-forwarded login page gets scraped by Shodan within hours. **Tailscale** (mesh
WireGuard VPN, zero port-forwarding) is the recommended path, and it solves TLS for free:
`tailscale serve`/`tailscale cert` auto-provisions and renews a Let's Encrypt cert for the
tailnet's MagicDNS name, no ACME code needed in `Flower.Server` for this path. Cloudflare
Tunnel is worth documenting as a secondary option for sharing with people who won't install a
VPN client, but it terminates TLS at Cloudflare's edge — a materially different trust boundary.

There's no .NET binding for `tsnet` (Tailscale's embed-in-your-app library, Go/Rust/Python/
Elixir only), so embedding Tailscale directly would mean shelling out to the external binary.
**Decision: document, don't automate** — setup docs tell the user to install Tailscale on the
server and their own devices; the only code change needed is widening `LanGuard` (below) so
Tailscale-originated traffic isn't rejected outright.

**Automated SSL**, two tiers: Tailscale users get certs for free as above (and `Flower.Server`
can stay plain HTTP bound to the tailnet/LAN interface, since WireGuard already encrypts that
traffic — finally closing, for this deployment shape, the "still plaintext HTTP" gap Phase 4
below explicitly deferred). Users who want a public domain without Tailscale get
[**LettuceEncrypt**](https://github.com/natemcmaster/LettuceEncrypt) — a small, Kestrel-native
ACME library, one `AddLettuceEncrypt()` call, no custom ACME plumbing.

### Security hardening for a server that's no longer LAN-only

- **`LanGuard` must stop being hardcoded RFC1918-only.** Add Tailscale's CGNAT range
  (`100.64.0.0/10`) to the allowed set, and make the allow-list a config option rather than a
  fixed constant, so a user behind a trusted tunnel/proxy can widen it without a code change.
- **The browser UI and its admin API routes need their own auth**, separate from the
  device-signed P2P scheme (a browser tab isn't a device with a keypair) — single admin
  password, `Flower.Web` logs in via a REST call and holds a token/cookie for subsequent
  calls. Rate-limit/lock the login route (reuse `RateLimiter`); if cookie-based, apply the
  usual CSRF mitigations, or use a bearer token in memory instead of a cookie to avoid the
  CSRF surface entirely.
- **The in-browser player needs its own stream-auth bridge** — `/rest/stream`-equivalent
  routes are gated by the device-signed `TrustedPeer` mode today, which a browser tab can't
  produce. Route the web player's audio requests through the same admin session instead, as a
  distinct auth mode, not a relaxation of the existing one.
- **Pairing codes need brute-force resistance** (below): short expiry, single-use, hard
  per-IP attempt cap on the redeem endpoint.

### Pairing redesign: admin-issued one-time codes

Today's flow (`SyncHttpServer.PeerApprovalRequested`, raised from `HandlePairRequestAsync`)
holds an incoming pair request open for 60 seconds waiting on a human to click Approve in a
popup, and fails closed if nobody's listening — fine when the admin is at the machine, a bad
fit for a headless box nobody's watching. Replace it for `Flower.Server` with an **admin-issued,
one-time pairing code**, proactive instead of reactive:

1. Admin, logged into the browser UI, hits "Add device" → server generates a short single-use
   code (e.g. 8-char alphanumeric) with a ~10 minute expiry, shown on-screen (plus a QR
   encoding the server's tailnet address + code).
2. Admin relays the code to whoever's setting up the new device out-of-band.
3. The new device's "pair with server" flow sends its self-signed public key **plus the code**
   to a new endpoint, kept separate from the existing device-to-device `pair-request` so that
   flow's semantics don't change at all. Server validates the code (exists, unexpired,
   unconsumed), consumes it, completes the same proof-of-possession handshake already built
   (verify offered key → derive fingerprint → write to `TrustedPeerStore`) — no 60-second live
   wait, no dialog.
4. Redeem endpoint is rate-limited hard per-IP (reuse `RateLimiter`) to bound brute-force
   attempts against the code within its expiry window.

Additive only: the existing GUI reactive-approval path (`PeerApprovalRequested`,
`ConfirmDialogWindow`, `TrustedDevicesWindow`) is untouched for desktop↔desktop/mobile P2P
pairing. The code-based flow is specific to pairing *against* `Flower.Server`.

**Effort:** roughly 3-5 weeks for one engineer for the server backend — most of it EF Core
schema/migration and SQLite concurrency hardening, not streaming or Docker — plus the new
`Flower.Web` head on top (see project structure below).

## Project structure: extracting a shared `Flower.Core` library, and a new `Flower.Web` head

`Flower.csproj` pulls in Avalonia/LibVLCSharp/every ViewModel — `Flower.Server`'s headless
backend can't reference that (the browser UI is a different story — see below). **Done**: a new
`Flower.Core` classlib (plain `net10.0` — no need to dual-target `net9.0`; `Flower.iOS`'s
`net10.0-ios26.0` head references a `net10.0` library exactly the way `Flower.Desktop`/
`Flower.Android`/`Flower.CLI` already did, confirmed by a full-solution build) now holds:

- **`Models/`** (`Track`, `Playlist`/`MainPlaylist`, `Library`) — pure move, no Avalonia references. `TimeSpanTicksConverter` had to go from `internal` to `public`: a source-generated `JsonSerializerContext` can't see an `internal` converter type from a different assembly.
- **`Importer/`** (`Importer`, `IMusicImporter`, `PlatformMusicImporter`, the iTunes importers) — `Flower.Server`'s scanner becomes the same `Importer.ImportAsync` desktop already uses. Brought `TagLibSharp`/`plist-cil` along (`Flower.csproj` still references `TagLibSharp` too, for its own non-import uses like `AlbumArtLoader`/`TrackInfoWindow`).
- **The OpenSubsonic wire contracts + REST client** (`OpenSubsonicContracts.cs`/`OpenSubsonicClient.cs`) — moved as-is. **Not** `LibraryOpenSubsonicMapper` (the `Track`→`Child` mapping) — that stays in `Flower`, since it calls `AlbumArtLoader` (Avalonia `Bitmap`-backed); `Flower.Server` will write its own `TrackEntity`→`SubsonicSongDto` mapping instead, per the "Reuse boundary" note below. `OpenSubsonicClient`'s own JSON parsing needed a new `OpenSubsonicJsonContext` in `Flower.Core` (mirroring `Flower`'s `ExternalProtocolJsonContext`, which stays put since it also covers `SyncHttpServer.SyncInfoResponseDto`) — two source-generated contexts for the same `SubsonicEnvelope` shape, harmless duplication.
- **The pairing/trust primitives** — `DeviceKeyStore`/`DeviceSigningKey`, `SignedRequestCanonicalizer`/`SignatureVerifier`/`NonceReplayGuard`, `TrustedPeerStore`, `RateLimiter`, `LanGuard`. (`DeviceIdentity`/`DeviceIdentityStore` did **not** move — it's this device's own display alias, a client-only concern, unchanged from the "Stays in `Flower`" list below.) `DeviceKeyStore`/`TrustedPeerStore` needed their own `FlowerCoreJsonContext` (`DeviceKeyMaterial`/`TrustedPeer`/`DeniedPeer`) since `Flower`'s `FlowerJsonContext` can't be referenced from `Flower.Core`. Also moved, as a necessary transitive dependency not originally called out here: `AppDataDirectory`/`PlatformDataDirectory` (the app-support-directory resolver — `AppDataDirectory` went from `internal` to `public`; still `Flower`'s own resolution logic for now, `Flower.Server` will likely want its own config-driven data directory rather than reusing this as-is). `SyncHttpServer` itself (the `HttpListener`-based P2P host) stays in `Flower` — it's mobile/desktop-specific — but `Flower.Server` gets its own Kestrel-based route layer calling the same moved-out primitives instead of reimplementing them.
- **`Flower/Logging/`** (`AppLogging`, `InMemoryLogStore`/`InMemoryLogEventSink`/`InMemoryLogEntry`, `CrashReportScanner`, `PlatformCrashInfo`) — no Avalonia coupling, and a headless `Flower.Server` needs the exact same Serilog file/console bootstrap rather than a duplicated copy, so it moved too even though nothing in `Flower.Core` strictly required it (see the `Importer` fix below, which *removed* the one forcing dependency but the logging infrastructure stayed moved anyway on its own merits). `AppLogging.LogsDirectory` uses the now-`public` `AppDataDirectory` above. `Flower.Core.csproj` carries the Serilog/`System.Diagnostics.EventLog` package refs this needs.

**`Importer.TryResolveAppleMusicFolder` no longer needs a static logger.** It's called from `AppSettingsStore.Load()` (to auto-populate a configured library path) before any `Importer` instance necessarily exists, which briefly justified a static `AppLogging.CreateLogger<Importer>()` field — but the method only needed to *accept* a logger from whichever caller already has one, not manufacture its own from a global. Fixed with an `ILogger? logger = null` parameter instead: `AppSettingsStore` passes its own `_logger`, and `Importer.Import()`'s instance `_logger` flows through its own `ResolveMusicPath` the same way. (This was going to shrink `Flower.Core` back to excluding `Flower/Logging/` entirely, until the "why not just move it, it's Avalonia-free and `Flower.Server` wants it too" call above superseded that — the `Importer` fix is still worth keeping on its own: a static global logger for one call site when the caller already has a perfectly good instance one was avoidable either way.)

**Startup logging/DI setup tightened as part of this pass, in `Flower` (prompted by looking at `AppLogging` closely, not really about what moved into `Flower.Core`):** `App.axaml.cs`'s DI container (`ServiceCollection`) is now created near the very top of `OnFrameworkInitializationCompleted`, immediately after `AppLogging.Initialize()` configures Serilog's sinks — and the *first* thing registered on it is `.AddLogging(builder => builder.AddSerilog())`, the standard `Microsoft.Extensions.Logging` DI pipeline, rather than `AppLogging` building its own separate `SerilogLoggerFactory` internally. That one collection then threads through the rest of `Bootstrap`, accumulating `.AddSingleton(...)` registrations as each service gets constructed exactly as before, and only gets built + handed to `Ioc.Default.ConfigureServices(...)` once, at the end (`CommunityToolkit.Mvvm`'s `Ioc.Default` can only be configured once, so the container itself still can't be *finished* until everything it holds exists — only the logging registration genuinely moved earlier). `AppLogging.CreateLogger`/`CreateTypedLogger` (used throughout `Bootstrap` for ad-hoc-`new`'d services, plus genuinely-static call sites like `RubberBandScroll`/`AlbumArtLoader` that can't take constructor-injected loggers at all) now read from that same DI-built `ILoggerFactory` via a new `AppLogging.UseLoggerFactory(...)` setter, instead of wrapping `Log.Logger` a second, independent time.

**Stays in `Flower`:** `LibraryStore`/`PlaylistStore` (server uses SQLite instead — JSON full-rewrite-on-save doesn't fit incremental upserts/foreign keys); `AppSettingsStore`/`ColumnVisibilityStore`/`DeviceIdentityStore`/`PlaylistSyncStateStore` (client-only concerns); `LibraryOpenSubsonicMapper` (Avalonia `Bitmap`-coupled via `AlbumArtLoader`, see above); `TrackListBuilder`/`TrackRowViewModel` (holds an Avalonia `Bitmap`); `SyncHttpServer`/`NetworkDiscoveryService`/`PlaylistSyncService` (P2P sync is a different feature from `Flower.Server`).

**Reuse boundary:** `Importer.ImportAsync()` produces shared `Flower.Core.Track`s → server-internal `TrackEntity` (EF Core) → server-internal mapping to shared `SubsonicSongDto` → HTTP/JSON → OpenSubsonic client maps back to its own `Flower.Core.Track`. Two deliberate seams (`TrackEntity`, `SubsonicSongDto`) keep DB churn and the Path-can't-cross-the-wire rule out of the shared model.

**Mechanical steps — done:** new `Flower.Core` classlib → `git mv` `Models/`/`Importer/`/the OpenSubsonic contracts+client/pairing-trust primitives in, along with their package refs → `Flower.csproj` gets a `ProjectReference` → confirmed `Flower.Tests` passes unchanged (359/359, plus the pre-existing `RequiresLibVLC` suite bar one flaky timing test that passes in isolation, unrelated to this move). **Next:** scaffold `Flower.Server` (`dotnet new webapi`) referencing `Flower.Core`.

**New `Flower.Web` project** (Avalonia Browser/WASM head): references `Flower.csproj` directly — same Views/ViewModels/`Flower/Controls/` as desktop, not a reimplementation. Two things it needs that the other heads don't: native audio package refs (`LibVLCSharp`/`Miniaudio-CS`, and the `LibVlcRawStreamSink`/`MiniaudioSink` classes that use them) conditioned out of the `browser-wasm` build via an MSBuild `Condition`; and a new `WebAudioSink : IAudioSink` (JS interop to the Web Audio API) registered in its own `App.axaml.cs`, the same per-platform DI-swap pattern already used to pick `MiniaudioSink` vs. `LibVlcRawStreamSink` today. It talks to `Flower.Server`'s REST API (OpenSubsonic + the new admin/pairing endpoints) instead of running `Importer`/LibVLC locally and serves as the static bundle `Flower.Server`'s Kestrel process hosts.

Once `Flower.Core` exists, add a `SubsonicLibraryImporter : IMusicImporter` (and later `JellyfinLibraryImporter`) so "local files" vs. "self-hosted server" is a settings choice via `IMusicImporter`, not a special-cased second code path — this supersedes `CROSS-PLATFORM-PLAN.md` item #3's original `IMusicSource` proposal, which shipped instead as `IMusicImporter`.

### Suggested build order

1. **Done.** Extract `Flower.Core` (mechanical git-mv + reference fixups; confirm `Flower.Tests` still passes unchanged).
2. Scaffold `Flower.Server`: EF Core/SQLite schema, importer wired up, OpenSubsonic endpoints working against a real Navidrome-compatible client.
3. Pairing-code endpoint + admin auth + `LanGuard` CGNAT allowance + rate limiting on the redeem route — get a real device pairing against a real headless instance before building UI on top of it.
4. Scaffold `Flower.Web`: get existing Views/ViewModels building and rendering in-browser first (against `Flower.Server`'s REST API for data, native audio refs conditioned out) before tackling playback; add `WebAudioSink` and transport controls; then the pairing-code "Add device" screen and admin settings screens; full jukebox browse/search/queue last since it needs a populated library to be worth testing against.
5. Docker packaging + docs: the "expose this over Tailscale" setup guide as the primary documented remote-access path, LettuceEncrypt as the secondary one.

## Mobile-specific note: streaming vs. background sync

Don't conflate these on iOS: **active playback while streaming** works fine backgrounded (standard `AVAudioSession` background-audio entitlement — unrelated to the P2P listener finding above, since here mobile is a client making outbound requests). **Bulk library sync/download** is subject to the same foreground constraints as the WiFi sync transport — expect a download queue to pause when fully backgrounded with nothing playing.

---

## Phase 4 — Cryptographic identity and hardening (done)

The fingerprint-only trust model above had a real gap: `X-Flower-Fingerprint`
was a self-reported GUID with no proof of possession, handed out by the
ungated `/info` endpoint and visible in cleartext on every request - anyone
on the LAN who observed a trusted peer's fingerprint could impersonate it.
**Done**: every device now generates an ECDSA P-256 keypair on first run
(`DeviceKeyStore`, `device-key.json`); `DeviceIdentity.Fingerprint` is
derived from the public key (`SignedRequestCanonicalizer.ComputeFingerprint`)
rather than an independent random value. Every gated request is signed
(`X-Flower-Signature`/`-Timestamp`/`-Nonce`, or the same as query params for
a URL handed directly to LibVLC/`OpenSubsonicClient.BuildUrl`) over
method+path+query+body+timestamp+nonce (`SignedRequestCanonicalizer`/
`DeviceSigningKey`/`SignatureVerifier`), verified against the public key
`TrustedPeerStore` captured at the moment a fingerprint was actually
approved - never a cached `/info` value. A `±60s` timestamp window plus
`NonceReplayGuard` bounds replay of a captured request. Pairing
(`pair-request`) and the new `unpair-notify` endpoint are proof-of-possession
"self-signed" (verified against the offered key itself, since there's
nothing to look up yet); every other gated endpoint is `TrustedPeer` mode.

**Breaking migration, by design**: existing `trusted-peers.json` entries have
no public key on file, so `TrustedPeerStore.GetPublicKey` returns null for
them and they fail exactly like the existing "trust was revoked" path
(`PeerTrustRejected` → `UnpairServer()`) - one re-tap of "Ask to pair"
restores the pairing with a real key captured this time. Every device's own
fingerprint also changes on upgrade (now derived from its new keypair), so
this is a one-time "everyone re-pairs once" event.

**Also done in this pass**: a declarative route table (`SyncHttpServer`'s
`Route`/`AuthMode`/`RateLimitCategory`) replaced the old `if`/`else if`
dispatch chain; rate limiting (`RateLimiter`, fixed-window, IP-keyed for
pre-trust endpoints, fingerprint-keyed post-trust); LAN-only enforcement
(`LanGuard`, hard reject on any non-private/loopback `RemoteEndPoint` -
closes `docs/todo.txt`'s "only stream on local network," since the wildcard
`http://+:{port}/` bind has no other network-layer boundary); persisted
pairing denials (`TrustedPeerStore.DenyAsync`/`denied-peers.json`,
surfaced in `TrustedDevicesView` with a "Forget refusal" action); a
server-initiated unpair notification (`POST /api/flower/v1/unpair-notify`,
`PeerUnpairNotifier`, fire-and-forget) so a revoked peer can learn about it
proactively instead of only via a later 403/poll; and a 20 MB request-body
cap (`RequestBodyReader.ReadWithCapAsync`).

**Explicitly still deferred**: TLS/transport encryption. `HttpListener`'s
HTTPS support off Windows remains a long-standing gap
(`dotnet/runtime#19752`); the signing scheme above closes the impersonation
hole without it, but traffic (including the signed headers themselves) is
still plain HTTP - eavesdropping on the same LAN is an accepted residual
risk, same "same-LAN threat model" framing as before, revisit if sync ever
needs to leave a trusted LAN.

## Status summary

All numbered steps through Phase 4 are **done**: `CROSS-PLATFORM-PLAN.md` item #3 updated to the private-file-library iOS design; WiFi/LAN discovery + LocalSend-style transfer; `UIFileSharingEnabled` for USB; Bluetooth/programmatic-USB deliberately not built; playlist metadata sync; the OpenSubsonic client; the full Phase 3 stack (trust gate, embedded host, merge logic, mobile download UI); and Phase 4's cryptographic identity/signed-request hardening (route table, rate limiting, LAN-only enforcement, persisted denials, server-initiated unpair, body size cap). `Flower.Server` build-order step 1, extracting `Flower.Core`, is also **done** (see "Project structure" above).

**Remaining work:** fold the client into the `IMusicImporter` abstraction as a user-facing settings choice; add Jellyfin as a second `IMusicImporter` backend; real-device Android download-path verification and end-to-end testing against a real peer; and, now the next initiative, scaffold `Flower.Server` (headless, web-interfaced, pairing-code-based pairing) on top of the new `Flower.Core`, then add the `Flower.Web` Avalonia Browser/WASM head.
