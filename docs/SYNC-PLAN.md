# Sync & Self-Hosting — Investigation & Plan

## Goal

Two related goals, unified by one decision below: sync music (files + metadata) between Flower desktop (Windows/macOS/Linux) and Flower mobile (iOS/Android) across all six desktop×phone permutations; and support "self-hosting," where a server (user's NAS/home box/VPS, or another Flower app on the network) holds a canonical library that other Flower apps stream/sync against.

## Architecture prerequisite (decided)

iOS owns its music files itself — its own sandboxed Documents-folder library, imported/read via TagLib, same as desktop's `Importer` scans `~/Music`. **No `MPMediaLibrary`/Apple Music integration.** `Track.Path` stays a plain filesystem path on iOS. (This overturned an earlier cross-platform proposal to read iOS's library through `MPMediaLibrary`; that document is gone, and the reason it was wrong is the paragraph below, which is the part worth keeping.)

**Why:** `MPMediaLibrary` has no external write access (DRM-restricted) — syncing *to* iOS would be impossible under any transport. Owning files directly unblocks both USB and WiFi sync.

---

## The unifying decision: one client protocol, one kind of server

Peer-to-peer WiFi sync and self-hosted-server support were originally scoped as separate protocols. They became one client protocol, then — see "Peer-to-peer, built and removed" below — one thing on the other end of it, and finally — see "OpenSubsonic, built and removed" — one protocol rather than two.

- **Protocol: OpenSubsonic** (actively-maintained open successor to the classic Subsonic API, JSON-first, backwards compatible). Building a client means **zero server code** to get self-hosting working — point Flower at Navidrome, Gonic, Airsonic-Advanced, or Ampache. **This is now history on both sides**: the client was cut back and then deleted, and the server surface went in September 2026 — see "OpenSubsonic, built and removed" below. Flower speaks its own protocol to its own server.
- **Target server: Navidrome** (Go, Docker-first, drives OpenSubsonic's evolution). Feature coverage confirmed sufficient: browsing, ranged `/stream`, playlist CRUD, favorites, cover art, search, scrobble.
- **Auth**: classic `token=md5(password+salt)` or OpenSubsonic's API-key extension.
- **No usable .NET client library existed**, so Flower hand-rolled one. **Done, then mostly deleted**: `OpenSubsonicClient` (`Flower.Core/Services/`) covered auth, ID3-based browsing, `search3`, playlist CRUD, star/scrobble and URL builders. Every browse call lost its last caller once the catalog moved to `GET /api/flower/v1/library`, so in September 2026 the client was cut back to what playback and the download button use — a signed `stream` URL and a resumable download — and the `subsonic-response` envelope parser went with it. A real browsing client is still the right thing for a third-party server with no bulk route; it is not carried until something asks.
- **Mobile's sync client and the self-hosting client are the same code** — just two different base URLs.

The server on the other end is a third-party Navidrome/Jellyfin instance or a first-party headless `Flower.Server`. Both look identical to the client.

### Peer-to-peer, built and removed

There used to be a third option, and for a while it was the interesting one: **a Flower app hosting the protocol itself, embedded, in-process, no separate server**. Any desktop or phone could be flipped into "Server" mode from Settings, and `SyncHttpServer` — a raw `HttpListener` inside the shared Avalonia library — served `/info`, pairing, the bulk library and playlist manifests, a log-report endpoint, and an OpenSubsonic browse/stream surface over the `Library` already in memory. Every client advertised itself over mDNS so other apps could find it. It worked, and it was fully built: the trust gate, the signed-request hardening, the AirDrop-style approval prompt, a two-section sidebar for "Servers" versus "Devices", 22 real-socket round-trip tests.

**It is gone.** What it cost was not the listener but everything shaped around it: a Client/Server role concept in two Settings screens and a persisted setting, two pairing flows (redeem a code / wait for someone to tap Allow) that every pairing surface had to fork on, two kinds of discovered peer, a trusted-device roster on a device that had no business having one, and a security boundary — `LanGuard` standing between a wildcard bind and the open internet — duplicated on every phone in the house. For a deployment model that is *one person runs one server* (see `CLAUDE.md`), the second server implementation was carrying the complexity of a mesh to serve a topology that is a star.

So: **a client is never a server.** Only `Flower.Server` serves, only `Flower.Server` advertises over mDNS, and a client browses without advertising. Pairing is always a redeemed code. Every discovered device is a server, which is what collapsed the role concept, the two sidebar sections and the two pairing paths into one each.

**What that gave up.** Browsing another *device's* library on the LAN — opening your desktop's collection from your phone and playing a track off it, with no server involved — went with the listener. The client half is gone too, and deliberately: `PeerLibraryViewModel` was the device-detail browse pane, and against `Flower.Server` — the only peer left — it showed the same catalog the library already holds, since `LibrarySyncService` merges a paired server's tracks in and `IStreamUrlResolver` streams them from the ordinary track list. The sidebar row's pane is that server's *settings* now (`MainViewModel.SelectedServerSettings`, `RemoteServerSettingsBackend`), which is the one thing about a server that had nowhere else to live.

**And how it comes back: through the server, not around it.** This used to read "it should return as its own design", with two things missing — a minimal browse-and-stream-only listener on the serving side, and an honest answer about what it means for a phone to open a port. **September 2026 decision: there is no answer to the second question, so do not ask it.** A device never listens. If music on one device should be reachable from another, it goes *to the server*, and the server serves it — the same star topology, the same single trust boundary, the same one implementation of pairing, rate limiting and `LanGuard`.

That is a better answer than the listener on every axis that matters here. Nothing on a phone binds a socket, so there is no second `LanGuard`, no trusted-device roster on a device with no business holding one, and no design question about a listener on a cellular network. The traffic is encrypted and pinned because it is the ordinary paired-client path — a device-to-device listener would have been plain HTTP at a link-local address, which is what `NetworkDiscoveryService.HttpOrigin` still assumes and what `CleartextOrigins` exists to contain.

**What it needs that does not exist: ingest.** A server can only middle-man music it has, and sync today moves playlist metadata and track *references*, not audio — the download button pulls files from the server, and nothing pushes the other way. So this decision is really a statement about where the library lives: **one library, on the server; devices hold subsets of it, not private collections worth browsing.** Which suits the deployment model in `CLAUDE.md` exactly. The work was an upload path — a device offering a file the server does not have, the server taking it into its own library and scan. **October 2026: it exists** — see "Ingest: an owner's device keeps the server's files in step" below. What is still owed is the UI that admits a song is a copy rather than a stream; today an upload is a line in the status bar and a spinner beside the server's name.

### OpenSubsonic, built and removed

**September 2026: `/rest/*` is gone.** OpenSubsonic was the right *starting* protocol and the wrong thing to keep. It earned its place twice — it meant zero server code to get self-hosting working at all, and it gave the catalog, the id scheme and the wire shapes a design someone else had already debugged. Both debts are paid: `Flower.Server` exists, and the shapes it taught are still here (`LibraryContracts`, `EntityId`, `CatalogIdentity`, `Track.Starred`, the playlist attributes in `Schema`). What was left was a second published surface serving a client that does not exist yet.

What it cost was not the folder. `Flower.Server/Subsonic/` was eight files and three lines of wiring, exactly as advertised — but hanging off it were: a password, the only guessable credential in the system, with a store, an admin surface to mint and revoke it, and a settings pane in every Flower head to drive that; a second gate on the media routes; a second set of rate-limit budgets; and a standing argument in favour of the plain HTTP port, because a third-party client cannot pin a certificate. Every one of those is a trust-boundary component, which is the part of this system where `CLAUDE.md`'s "it's just for me" reasoning explicitly does not apply.

So the ledger for deleting it: **the system now has no password in it at all.** The only caller that cannot pin the server's self-signed certificate is a browser tab, which narrows what a real certificate is *for* (`docs/SELF-HOSTING.md`), and the plain port's remaining callers are a tab on a LAN address and an old bookmark. `MediaEndpoints` separated the gate from the file-reading specifically so the adapter could be deleted without taking media serving with it; that it came out clean is that argument having been right.

**What it gave up, and the terms for its return.** Playing a Flower library in someone else's app — Symfonium, play:Sub, Amperfy, a car head unit — and pointing Flower at a Navidrome or Jellyfin someone else runs. The client half of that was already gone before this (`OpenSubsonicClient` had lost its last browse caller; see the protocol bullet above), so this is the server half following it. Neither is hard to rebuild — the shapes never left — and the stated intent is to publish Flower's own protocol instead, so that a third-party client speaks the one surface this project actually maintains. Bringing OpenSubsonic back needs a reason of the form "a specific person wants to use a specific app", not "a client might exist".

### Staged path to "always available to sync with"

`Flower.Server` is a headless daemon already, so there are no stages left here: run it on the NAS, the home box or the VPS and it survives logout and starts at boot. See `SELF-HOSTING.md`. This section used to describe a ladder — tray/menu-bar mode, then auto-start on login, then a true daemon — climbed to keep an in-GUI host alive; the daemon at the top of it is what got built, and the ladder went with the host.

### `Flower.Core` exists

The OpenSubsonic contracts, the client, the wire shapes, the signature and trust machinery, and the SQLite layer all live in `Flower.Core`, which is what `Flower.Server` reaches for and `Flower` (Avalonia-coupled) shares. See "Project structure" below. This section used to argue for *deferring* that split until something needed to run where it could not reference Avalonia or a native audio stack — `Flower.Server` was that something.

---

## Recommendation: build WiFi/LAN sync, skip Bluetooth, treat USB as secondary

> **Read the rest of this section, and Phase 3 below, as a record of what was built rather than of what runs.** All of it shipped; the half of it that made a Flower app *serve* — `SyncHttpServer`, its endpoints, the approval prompt, the client's own mDNS advertisement — has since been removed, and the client half now talks to `Flower.Server` instead. See "Peer-to-peer, built and removed" above for why. Endpoints named below still exist and still carry the same shapes; they are answered by `Flower.Server`'s `SyncEndpoints`/`DiscoveryEndpoints`, not by another phone.

### 1. WiFi/LAN sync — the transport to actually build

**Why:** desktop and mobile are the same .NET/Avalonia codebase — no OS-vendor device protocol, no native interop.

- **Discovery**: `Makaretu.Dns.Multicast.New` for mDNS (`Zeroconf` was rejected — browse/resolve only, can't advertise). Verified macOS ↔ iOS Simulator. Android needs a `MulticastLock` (implemented, **not yet verified on a real device**). iOS 14+ needs `NSBonjourServices`/`NSLocalNetworkUsageDescription` (implemented, working).
- **Transfer protocol**: reimplements [LocalSend's open protocol](https://github.com/localsend/protocol) (HTTPS+JSON, self-signed certs, mDNS + HTTP fallback) rather than inventing one. **Phase 1 done**: device identity exchange + Devices sidebar, over plain HTTP for now. **Phase 2 done**: playlist metadata sync (no audio yet):
  - `Playlist.Id` (stable Guid) + `UpdatedAt` timestamp; `Track.SyncKey` (Title+Artists+Album+Duration fingerprint) matches tracks across devices since `Path` is local-only. A synced playlist can only reference tracks present on both devices so far.
  - Because `PlaylistSyncPlanner` decides "did this side change?" purely from `UpdatedAt` against a per-peer baseline, *every* mutation has to bump it. A drag-reorder did not: `MainViewModel.ReorderPlaylistTrack` mutated `Playlist.Tracks` directly, so reorders silently never propagated to a paired device. `Playlist.Tracks` is now `IReadOnlyList<Track>` so mutation has to go through the methods that bump `UpdatedAt` (ARCHITECTURE-REVIEW Tier 2.4).
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

**Confirmed real problem, changed after real-world testing:** the original per-album (`getAlbumList2`+`getAlbum`) catalog fetch produced 1000+ HTTP connections against a 1,397-album library, causing real network/battery cost on iOS. `LibrarySyncService` now uses a bespoke bulk endpoint instead — `GET /api/flower/v1/library` returning the whole manifest in one response, same shape as the playlist endpoint. The standard `/rest/getAlbumList2`/`getAlbum` endpoints stayed alongside it for third-party interop at the time, and were removed with the rest of the adapter in September 2026.

That endpoint is now conditional: it serves `Library.ChangeToken` as its `ETag` and answers `304` to a matching `If-None-Match`, and the serialized manifest is cached against the token it was built from (the album-art hashes it embeds cost ~1,400 TagLib file opens to compute). The same token is advertised on `/info`, which is what finally lets a Client notice a *server-side* change: sync previously fired only on first mDNS contact or a debounced local change, so a track added on the Server went unnoticed for as long as both apps stayed running. The ~5s `/info` poll every Client already runs now carries the token, so a change on either side converges within one poll. See ARCHITECTURE-REVIEW §1.4.

### Data model

`Track.Path` is already nullable — a track with `Path == null` is metadata known via sync but not locally downloaded. `Track.OriginDeviceFingerprint` tracks which peer currently has the file.

### Trust gate

Phase 1/2 deferred trust for plain HTTP; this phase needs it before handing over audio on request. **Done**: `TrustedPeerStore` (`trusted-peers.json`) persists approved `(Fingerprint, Alias, ApprovedAt)` entries; denials aren't persisted (re-prompted next time). `SyncHttpServer.AuthorizeAsync` gates every `/api/flower/v1/*` path (only `/api/localsend/v2/info` stays open, since trust can't be evaluated before a peer's fingerprint is known). Unrecognized fingerprint → `PeerApprovalRequested` → `ConfirmDialogWindow` prompt; unanswered after 60s or no UI listening denies by default (unlike playlist-conflict's "keep local" default, this has no safe implicit default). Revoke via `TrustedDevicesWindow` ("Trusted Devices…" in the app menu). Still plain HTTP — trust here means *authorization*, not encryption; acceptable for a same-LAN threat model, revisit if sync ever leaves the local network. Unit tested (`StoreRoundTripTests.cs`).

### Merge behavior

On receiving a peer's catalog: match by `SyncKey`. Already present as a real file + peer has a live copy → update `OriginDeviceFingerprint` to the closer peer. Not present → insert a placeholder `Track` (`Path = null`). Never delete a local, `Path`-backed track just because a peer doesn't mention it. Symmetric/bidirectional, same discovery-triggered flow as playlist sync (`LibrarySyncService` alongside `PlaylistSyncService`). Side benefit: `PlaylistSyncMapper.ResolveTracks` already matches against the local library, so a placeholder-referencing playlist just works once placeholders exist.

### UI: the download button

Mobile first (desktop was assumed to have enough storage not to need it); **desktop now has it too** — a paired server's placeholders carry the same download icon (`TrackDownloadButton`, shown only while there is actually something fetchable) in three places, each right-aligned at the far edge of the thing it belongs to: the track list's Title column, an expanded album's song rows, and an album grid tile beside the album name — where one click fetches the whole album behind one spinner. Download items sit in the track-list, expanded-album and album-tile right-click menus too, acting on the whole current selection. The icon's state (fetchable / in flight / failed, plus the spinner angle) is `DownloadIndicatorViewModel`, shared by the track row, the expanded song row and the tile; the per-row spinner state, the 400 ms minimum spinner duration and the 3-at-a-time batch throttle moved out of `MobileMainViewModel` into `TrackDownloadRunner` (`MainViewModel.Downloads`), which both heads now drive; a desktop batch also raises the status bar's busy spinner. **Done**: a `Path == null` row renders dimmed with a download icon in place of the normal action affordance; tap elsewhere is a no-op until downloaded. Download resolves the peer via `OriginDeviceFingerprint` against currently-discovered devices, streams `GET /rest/stream` to the platform's normal import location, sets `Track.Path`, persists, and fires `Library.NotifyTrackChanged()` (lighter than `UpdateTracks` — no add/remove, same `Track` reference). `Track.OriginFileExtension` carries the extension across the wire since `Path` doesn't exist yet at receive time. `SyncHttpServer` gained `GET /rest/stream`; `LibraryDownloadService` does the resolve/download/persist; `TrackRowViewModel` gained `IsPlaceholder` plus the download-icon state now living in `DownloadIndicatorViewModel` (`IsDownloadable`/`IsDownloading`/`IsDownloadUnavailable`/`IsDownloadIdle`/`SpinAngle`).

**Known gap, deliberately accepted:** on Android, a downloaded file lands in app-private storage, not MediaStore-indexed — `Library.UpdateTracks`'s carry-forward was widened so it survives a rescan that doesn't independently find it, but it's not independently rediscoverable. **Not yet verified on a real Android device.** iOS doesn't have this gap. The download flow is unit-tested only (`LibraryDtoMapperTests`/`LibrarySyncMapperTests`/`LibraryTests`), not yet exercised end-to-end against a real peer on either platform.

### Additional Phase 3 work beyond the original scope

- **Album art sync — done, and the key has since changed meaning.** `Track.OriginAlbumArtId` is the cache key `AlbumArtLoader`'s remote-fetch path uses against `GET /rest/getCoverArt`. It was `OriginAlbumArtHash`, a SHA-256 of the origin's art bytes stamped by the app's own embedded host, so replacing a cover was self-invalidating. That host is gone and `Flower.Server` sends the *album id*, which is the better key: one entry per album rather than one per version of every cover the library has ever had, and it can address art that changed since the last sync, which a byte-derived key cannot. Noticing a change is revalidation's job — `AlbumArtLoader.Invalidate` deletes the entry outright rather than relying on the key to move. Renamed to match in September 2026. Art decoding was moved to a background thread after it was found to stall UI scrolling on the main thread.
- **Play count sync — done, not originally scoped.** `Track.RemotePlayCounts` (`Dictionary<fingerprint, count>`) is a small G-Counter CRDT — each device stamps its own contribution, receivers merge by per-key max (safe under repeats/reordering/relay). A device never accepts a peer's report of its own key back. Rides the existing bulk-catalog sync.
- **Resync on local change — done.** `MainViewModel.ScheduleContentSync` debounces (5s, restarts on every call) and re-syncs on `Library.LibraryChanged`/`PlaylistsUpdated`, guarded by an in-flight counter to avoid a merge's own events triggering an infinite resync loop.
- **A real data-corruption bug, found and fixed.** Four call sites (play-count-on-end, tag edits, iTunes import) called `UpdateTracks(Library.Tracks)` just to fire the refresh event, which doubled every sync placeholder each time (already-present + carried-forward again) — produced a multi-GB `library.json` in practice. Fixed by switching those sites to `Library.NotifyTrackChanged()`.
- **Device sidebar dedup by fingerprint** — matching was by raw mDNS instance name, which collides for two devices sharing a default computer name; now matches by `Fingerprint` once resolved.

### Deliberately deferred, not designed now

Resumable/partial downloads (retry-from-scratch in v1); multi-source download (only the recorded origin is tried); auto-download-on-tap-to-play (kept as an explicit button for now). Batch actions are no longer deferred — an album downloads from its tile's own icon or its right-click menu, and any multi-selection from the track list's.

**Effort:** Medium (reuses Phase 1/2 machinery; new pieces are the trust gate, streaming endpoint, mobile row UI). **Risk:** Low-Medium, concentrated in the trust gate's default-deny posture and mobile storage/import-path plumbing.

---

## Optional, additive: Jellyfin client support

Many self-hosters already run Jellyfin (MIT-licensed `Jellyfin.Sdk`, separate from the GPLv2 server — plain network client use, no derivative-work concern) for movies/TV and would rather not run a second server for music. Worth adding as a second optional `IMusicImporter` backend once a browse client exists again — not a replacement for it, since Jellyfin is ~5-10x heavier (300-800MB RAM idle) and video-first; treat it as "support the server users already have."

## Next: first-party `Flower.Server`, headless with a web interface

Promoted from "optional, later" — this is the next initiative. Goal: a headless server
(NAS/VPS/home box) that plays music through a browser-reachable web interface, lets the owner
configure the server through that same interface, and lets new devices request pairing with no
local screen to pop a dialog on. Speaks OpenSubsonic itself so the existing client — and any
third-party Subsonic mobile client — works against it for free. (That last sentence is why the
adapter was built and no longer describes the server: see "OpenSubsonic, built and removed".)

**Recommended stack:** ASP.NET Core Minimal API + Kestrel (range-request streaming via
`Results.File(..., enableRangeProcessing: true)`, no custom code, uses `sendfile`); SQLite
(WAL mode + an explicit `busy_timeout`; WAL requires local storage, not NFS/SMB). **Superseded
on both counts since this was written:** the EF Core layer this originally specified is gone —
the server runs on `Flower.Core`'s raw-SQLite layer, the same one the client uses, see the Tier
4.1 note below — and the "single admin password + long-lived tokens" line is replaced by the
passwordless design in "Passwordless by design" above. No OAuth either way;
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
- **Mobile Avalonia isn't a pain point to escape** — the mobile shell is fully
  working today (real-device-validated audio, import, touch UI, not scaffolding). The
  hand-rolled UI surface (`MusicListView`'s virtualization, `RubberBandScroll`'s custom
  physics, `ScreenStackPanel`'s nav stack) would need full reimplementation in Blazor's DOM
  model, not porting.
- A literal **web/PWA mobile app** (no native shell) was also rejected: iOS PWA background
  audio actively breaks after first launch, storage caps around 50MB, and there's no reliable
  background sync — that would break the gapless decode/miniaudio engine, the private
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

**The native audio stack has no WebAssembly build** — this was true of LibVLCSharp and Miniaudio-CS then and is true of the FFmpeg façade and Miniaudio-CS now — so the browser head is the one
place that can't reuse `Flower.csproj` completely unmodified: it needs its own `IAudioSink`
implementation driven by the browser's Web Audio API via JS interop. This reuses the existing
seam (`IAudioManager`/`IAudioSink` already abstract over `MiniaudioSink` and
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
- **The browser UI does *not* need its own auth** — this doc used to claim it did, on the
  grounds that "a browser tab isn't a device with a keypair." That premise is false: WebCrypto
  can generate a non-extractable P-256 keypair in IndexedDB, so the browser is a device with a
  keypair, and it pairs and signs like any other. See "Passwordless by design" below. The
  configured admin password goes away entirely rather than being replaced.
- **The in-browser player still needs a stream-auth bridge**, but for a narrower reason than
  the one given here before: not "a browser tab can't produce a `TrustedPeer` signature" (it
  can), but that an `<audio>` element can't attach a signature *header* to the request it
  issues. Short-lived stream tickets, minted by a normally-signed call, cover that gap — see
  below.
- **Pairing codes need brute-force resistance** (below): short expiry, single-use, hard
  per-IP attempt cap on the redeem endpoint.

### Passwordless by design: two paths, because only two are possible

**No accounts, no registration, and no password the user has to invent or remember.** Every
Flower surface — desktop, mobile, *and* the browser admin UI — authenticates the same way, with
a device keypair and a one-time pairing code. The one thing that could never join that scheme was
a third-party Subsonic client, because the protocol it implements is published and fixed — and
with that surface retired (see "OpenSubsonic, built and removed"), **path B is gone and there is
no password anywhere in this system.** What follows is the record of it having existed.

An earlier revision of this section had three paths, with the browser UI on WebAuthn passkeys as
its own middle tier. That was built on a false premise — see the security bullet above — and is
now folded into path A.

**Status — done.** Built and tested: `TrustedPeer.IsAdmin` and the admin-granting pairing code
that sets it; `/api/admin` gated by `DeviceSignatureAuth.VerifyTrustedPeer` + `IsAdmin` instead of
a login; the `PairingInvite` (`flower://pair?host=…&code=…&fp=…`) shared type and the `fp=`
server-key pin; the startup bootstrap code printed to stdout; and `StreamTicketService` with its
mint route, which the browser's `<audio>` element presents. Path B was built end to end too
(`SubsonicCredentialStore`, the admin routes that minted and revoked, `SubsonicAuth`, the `apiKey`
form, and ticket redemption on `/rest`) and was deleted with the adapter.

### The server had to be findable first

Pairing assumes the user can get to the server. `Flower.Server` could not be found at all: it
never advertised `_flowersync._tcp` and never served `/api/localsend/v2/info`, the two things a
client needs to put a peer in its sidebar, so a running server was simply invisible. This
predates the pairing work - the server was built as a Subsonic endpoint reached by typed
address, and the app's own discovery stack lives in the Avalonia project (`Flower/`), which
`Flower.Server` cannot reference.

**Fixed by moving discovery down into `Flower.Core`**, where both ends can share it, rather than
by reimplementing the record and response shapes on the server: `IMdnsBackend`/`PlatformMdns`,
`MakaretuMdnsBackend` (extracted out of `NetworkDiscoveryService.cs` and made public), the
`Makaretu.Dns.Multicast.New` package reference, plus a new `SyncProtocol` holding the three facts
both sides must agree on - the service type, the default port, and the `/info` path - and the
`SyncInfoResponseDto` wire shape with its own `SyncProtocolJsonContext`. `Flower.iOS`'s
`BonjourMdnsBackend` needed no change (same namespace). (Written when both ends advertised and
both had to be kept from drifting; only the server advertises now, and `NetworkDiscoveryService`
browses. The shared constants stayed, since a browser and an advertiser still have to agree.)

The server side is then small: `MdnsAdvertiser`, an `IHostedLifecycleService` that advertises in
`StartedAsync` (the bound port is only known once Kestrel has started) and unadvertises on
shutdown so clients prune the row immediately; and `DiscoveryEndpoints`, serving
`SyncInfoResponseDto` with `trustsCaller` answered from `TrustedPeerStore` for the ~5s poll every
client already runs. (It carried an `isServer` flag too, back when a Flower app could answer this
same handshake; it is the only thing answering it now, and the flag is gone.) Both failure modes are warnings, not startup failures -
an undiscoverable server still serves every request, it just has to be reached by address.
New options: `Flower:Alias` (sidebar name, defaults to the machine name) and
`Flower:AdvertiseOnLan` (on by default; off for tailnet/reverse-proxy-only deployments).

#### Only a bind something else could reach gets advertised

`MdnsAdvertiser` originally took the port out of Kestrel's bound address and discarded the host.
But the mDNS record resolves to the machine's LAN addresses whatever Kestrel actually bound, so a
server started on `--urls http://localhost:5599` published itself as reachable at
`<lan-ip>:5599` - an address that refuses every connection, on a row that collides with the real
server's, since both advertise under the machine name. The client is left logging
`Connection refused` against a peer it was told exists, with nothing on its side to fix.

This bites dev instances rather than deployments, which is exactly why it is worth catching in
code: the symptom shows up on a *different* machine, a hop away from the cause. `AdvertisablePort`
now returns null unless some bound address is non-loopback, and a loopback-only server logs that
it is skipping the advertisement and serves on. A wildcard bind (`0.0.0.0`, `[::]`, `+`, `*`) is
not loopback and still advertises, which is the normal path.

### Pairing from the client: "Pair" plus a code box

Discovery gets the server into the sidebar; redeeming the code is what makes it usable. The
client had only the app-to-app flow - an "Ask to pair" button that POSTs `/api/flower/v1/pair-request`
and waits up to 60s on the peer's live approval prompt - which a headless server does not
implement and could never answer.

The two flows are told apart by the handshake's `deviceType`, not by `isServer` (an app in Server
role sets that too): `DiscoveredDevice.DeviceType`/`PairsByCode` is true only for `"server"`, and
an absent field reads as an app - the conservative default, since it keeps the approval flow
rather than demanding a code nobody can produce. On that peer the button reads **Pair** rather
than "Ask to pair", a code box appears next to it, and `PeerPairingService.RedeemPairingCodeAsync`
POSTs `/api/flower/v1/pair-redeem` with the same self-signed request shape plus
`X-Flower-PairingCode`. There is no "Waiting for server..." state on this path - the redeem
either comes back trusted within one round trip or the code was wrong, in which case
`PeerSyncCoordinator` rolls the pairing straight back and raises `PairingCodeRejected` so the UI
can say so instead of appearing to have done nothing.

All three client surfaces got it: the sidebar device-detail header (the primary one), Settings'
`ServerPickerView` row (whose typed code is carried across the ~5s `Refresh()` rebuild, which
would otherwise wipe it mid-keystroke), and mobile's `ConfirmPairServerView` sheet. Codes are
normalized server-side (case, dashes, spaces), so what a user copies off the admin screen or
hears over the phone works as typed.

**Discovery is convenience only, deliberately.** Being found gets a server a row and an address
and nothing else - it is untrusted until a pairing code is redeemed, because the code is what
carries the `fp=` fingerprint pin. The alternative (tapping the row to request approval) was
rejected: it reintroduces the reactive-approval shape this whole redesign replaced, and it
bypasses the pin.

**Deleted, not deprecated:** `AdminAuthService` (bearer tokens, `/api/admin/login`,
`/logout`, `/logout-all`), and the `Flower:AdminUsername`/`Flower:AdminPassword` options
together with the startup check that refused to boot without them.

**Built (see "The browser is a device, for real" below).** The open question settled the way
the non-extractable-key property argued it would: .NET-for-WebAssembly has no `ECDsa` at all,
so the browser half is a JS-interop `crypto.subtle` module in the `webaudio.js` mould
(`Flower.Web/wwwroot/webcrypto.js`) rather than a storage backend under `DeviceSigningKey`.
The formats needed no bridging code, exactly as the table below predicted.

#### Path A — key-based: every Flower surface, browser included

The hard part already exists: every device has a self-signed keypair, `TrustedPeerStore` does
proof-of-possession, and `SignatureVerifier` checks per-request signatures. Pairing is only "get
a public key to the server with evidence a human authorized it."

**The browser is a device.** `Flower.Web` generates an ECDSA P-256 keypair via WebCrypto with
`extractable: false` and keeps it in IndexedDB. The private key is a handle the page can sign
with but never read — a *stronger* storage guarantee than the file-backed key the desktop app
has today. The formats already line up exactly, with no bridging code:

| | today, in `Flower.Core` | WebCrypto |
|---|---|---|
| curve | `ECCurve.NamedCurves.nistP256` (`DeviceKeyStore.cs:74`) | `ECDSA` / `P-256` |
| hash | `HashAlgorithmName.SHA256` | `SHA-256` |
| signature encoding | `DSASignatureFormat.IeeeP1363FixedFieldConcatenation` (`DeviceSigningKey.cs:34`) | raw `r‖s`, which *is* P1363 |

So `SignatureVerifier` (`SignatureVerifier.cs:51`) accepts browser-produced signatures unchanged
— no new algorithm, no new verification path, no second auth mode in the server. Admin routes
get gated by the same `DeviceSignatureAuth` middleware as device routes, plus an `IsAdmin` flag
on the peer record. Devices differ by capability, not by authentication mechanism.

**Pairing, one mechanism for all of them.** The app-to-app flow held an incoming request open for
60 seconds waiting on a human to click Approve, and failed closed if nobody was listening — fine
when the admin is at the machine, a bad fit for a headless box nobody's watching. `Flower.Server`
replaces it with an **admin-issued, one-time pairing code**, proactive instead of reactive — and
since the app-to-app flow is gone, this is now the only way anything pairs with anything:

1. Admin hits "Add device" → server generates a short single-use code with a ~10 minute expiry,
   displayed on-screen both as text and as a QR encoding

   ```
   flower://pair?host=100.x.y.z:4533&code=K7M2-P9QX&fp=<server-key-fingerprint>
   ```

   The `fp=` field is load-bearing and is the main addition over a bare code: it lets the
   *client* pin the server's public key at pair time, making the QR a mutual trust bootstrap
   rather than a one-directional one. That's what buys security without TOFU over plain LAN
   HTTP.
2. Admin relays the code (or just shows the screen) to whoever's setting up the new device.
3. **(Built.)** The new device — phone, desktop, or a browser tab — scans the QR, or types the
   code where there's no camera, and sends its public key **plus the code** to a redeem
   endpoint kept separate from the existing device-to-device `pair-request`, so that flow's
   semantics don't change at all. Server validates the code (exists, unexpired, unconsumed),
   consumes it, completes the proof-of-possession handshake already built (verify offered key →
   derive fingerprint → write to `TrustedPeerStore`) — no 60-second live wait, no dialog.
4. **(Built.)** Redeem endpoint is rate-limited hard per-IP (reuse `RateLimiter`) to bound
   brute-force attempts against the code within its expiry window.

Codes are **Crockford base32** (no I/O/0/1) so they survive being dictated over the phone —
already the alphabet `PairingCodeService` uses. Codes stay in-memory and un-persisted: they only
need to outlive their own ~10 minute expiry, so losing outstanding ones on restart is a cheap,
retryable cost rather than a schema concern.

**Bootstrap.** On first start the server prints a pairing code to stdout, visible in
`docker logs`. The first browser redeems it through the same endpoint as everything else — the
first-run claim stops being its own mechanism and becomes simply "the first code." The same
lever is the account-recovery story: a CLI/`docker exec` command that prints a fresh code, which
is what you use after clearing site data, losing a laptop, or locking yourself out.

**Additive for P2P.** The existing GUI reactive-approval path (`PeerApprovalRequested`,
`ConfirmDialogWindow`, `TrustedDevicesWindow`) is untouched for desktop↔desktop/mobile pairing.
The code-based flow is specific to pairing *against* `Flower.Server`.

**Costs, stated plainly.** An IndexedDB key doesn't sync across a user's browsers the way a
passkey would, so each browser pairs itself — fine here, and arguably more honest, since each
browser genuinely is a separate device. Clearing site data locks that browser out until it
re-pairs, hence the recovery lever above. And a WebCrypto key gives no phishing resistance where
a WebAuthn passkey would — immaterial when there's no password to phish and no public login page
to imitate.

**Why not WebAuthn passkeys** (the rejected middle path): they need a secure context, so on
plain LAN HTTP they don't run at all and would need an opaque-token fallback tier anyway —
whereas WebCrypto and IndexedDB work fine over HTTP. Passkeys would also have been a second auth
mode in the server for exactly one client type. The syncing and phishing-resistance advantages
they hold over this design don't pay for that.

#### Path B — the protocol-mandated exception: third-party Subsonic clients

> **Removed, September 2026.** The adapter this path existed for is gone, and path B with it —
> there is no password in the system now. Kept as the record of how it worked, and of what would
> have to come back with it. See "OpenSubsonic, built and removed".

DSub / substreamer / Symfonium implement a published protocol and will send `u=`/`t=`/`s=` or an
`apiKey`. No design choice on this side changes that, so this one genuinely can't merge into
path A. What it *can* do is stop being a separate subsystem: it's a second **credential type**
issued by the same admin action, not a second registry.

| | redeemed by | becomes |
|---|---|---|
| Flower device / browser (path A) | posting a public key + the code | `TrustedPeer` with a fingerprint |
| Subsonic client (path B) | using the code directly as the password | long-lived credential row |

One issuer, one registry, one revoke button, one last-seen list — two redemption modes. The user
copies a generated credential rather than inventing a secret, and each is individually revocable
without touching any other client.

Also implement OpenSubsonic's **`apiKey` extension** for clients that support it — same
credential object underneath, cleaner wire format, no md5-salt round trip.

#### The in-browser player: stream tickets

The one piece path A doesn't solve by itself. A signed request needs a signature header, and an
`<audio src="...">` can't send one. So a normally-signed call mints a **short-lived, single-URL
stream ticket** which rides as a query parameter on the media URL. Note this would have been
required under the passkey design too — it's a property of `<audio>`, not of how the browser
authenticates, so unifying on keys neither creates nor removes this work.

**Effort:** the original estimate here was 3-5 weeks for one engineer, most of it EF Core
schema/migration and SQLite concurrency hardening. That's spent and the shape of it changed:
the server no longer has a persistence layer of its own to harden — it runs on `Flower.Core`'s
raw-SQLite layer and the shared resident `Library` (see the Tier 4.1 note below), so the
schema/migration bulk of that estimate is gone rather than done-as-scoped. What remains is the
`Flower.Web` head (see project structure below), the passwordless auth work above, and Docker
packaging.

## Project structure: extracting a shared `Flower.Core` library, and a new `Flower.Web` head

`Flower.csproj` pulls in Avalonia, the native audio stack and every ViewModel — `Flower.Server`'s headless
backend can't reference that (the browser UI is a different story — see below). **Done**: a new
`Flower.Core` classlib (plain `net10.0` — no need to dual-target `net9.0`; `Flower.iOS`'s
`net10.0-ios26.0` head references a `net10.0` library exactly the way `Flower.Desktop`/
`Flower.Android`/`Flower.CLI` already did, confirmed by a full-solution build) now holds:

- **`Models/`** (`Track`, `Playlist`/`MainPlaylist`, `Library`) — pure move, no Avalonia references. `TimeSpanTicksConverter` had to go from `internal` to `public`: a source-generated `JsonSerializerContext` can't see an `internal` converter type from a different assembly.
- **`Importer/`** (`Importer`, `IMusicImporter`, `PlatformMusicImporter`, the iTunes importers) — `Flower.Server`'s scanner becomes the same `Importer.ImportAsync` desktop already uses. Brought `TagLibSharp`/`plist-cil` along (`Flower.csproj` still references `TagLibSharp` too, for its own non-import uses like `AlbumArtLoader`/`TrackInfoWindow`).
- **The OpenSubsonic wire contracts + REST client** (`OpenSubsonicContracts.cs`/`OpenSubsonicClient.cs`) — moved as-is. **Not** `LibraryOpenSubsonicMapper` (the `Track`→`Child` mapping) — that stays in `Flower`, since it calls `AlbumArtLoader` (Avalonia `Bitmap`-backed); `Flower.Server` will write its own `TrackEntity`→`SubsonicSongDto` mapping instead, per the "Reuse boundary" note below. `OpenSubsonicClient`'s own JSON parsing needed a new `OpenSubsonicJsonContext` in `Flower.Core` (mirroring `Flower`'s `ExternalProtocolJsonContext`, which stays put) — two source-generated contexts for the same `SubsonicEnvelope` shape, harmless duplication. (**Since undone, September 2026.** `LibraryOpenSubsonicMapper` never did get a server twin worth keeping: the app's copy outlived the embedded host with no callers and was deleted, and the server's mapping moved *into* `Flower.Core` as `LibraryDtoMapper.ToTrackDto` — the catalog shape belongs to the catalog, and `/rest` reshapes it. Both `SubsonicEnvelope` contexts are gone with the client's browse half that needed them.)
- **The pairing/trust primitives** — `DeviceKeyStore`/`DeviceSigningKey`, `SignedRequestCanonicalizer`/`SignatureVerifier`/`NonceReplayGuard`, `TrustedPeerStore`, `RateLimiter`, `LanGuard`. (`DeviceIdentity`/`DeviceIdentityStore` did **not** move — it's this device's own display alias, a client-only concern, unchanged from the "Stays in `Flower`" list below.) `DeviceKeyStore`/`TrustedPeerStore` needed their own `FlowerCoreJsonContext` (`DeviceKeyMaterial`/`TrustedPeer`/`DeniedPeer`) since `Flower`'s `FlowerJsonContext` can't be referenced from `Flower.Core`. Also moved, as a necessary transitive dependency not originally called out here: `AppDataDirectory`/`PlatformDataDirectory` (the app-support-directory resolver — `AppDataDirectory` went from `internal` to `public`; still `Flower`'s own resolution logic for now, `Flower.Server` will likely want its own config-driven data directory rather than reusing this as-is). (`SyncHttpServer`, the `HttpListener`-based P2P host, stayed in `Flower` at the time, on the grounds that it was mobile/desktop-specific; it has since been removed outright, and `Flower.Server`'s Kestrel route layer — which already called these same moved-out primitives rather than reimplementing them — is the only server left.)
- **`Flower/Logging/`** (`AppLogging`, `InMemoryLogStore`/`InMemoryLogEventSink`/`InMemoryLogEntry`, `CrashReportScanner`, `PlatformCrashInfo`) — no Avalonia coupling, and a headless `Flower.Server` needs the exact same Serilog file/console bootstrap rather than a duplicated copy, so it moved too even though nothing in `Flower.Core` strictly required it (see the `Importer` fix below, which *removed* the one forcing dependency but the logging infrastructure stayed moved anyway on its own merits). `AppLogging.LogsDirectory` uses the now-`public` `AppDataDirectory` above. `Flower.Core.csproj` carries the Serilog/`System.Diagnostics.EventLog` package refs this needs.

**`Importer.TryResolveAppleMusicFolder` no longer needs a static logger.** It's called from `AppSettingsStore.Load()` (to auto-populate a configured library path) before any `Importer` instance necessarily exists, which briefly justified a static `AppLogging.CreateLogger<Importer>()` field — but the method only needed to *accept* a logger from whichever caller already has one, not manufacture its own from a global. Fixed with an `ILogger? logger = null` parameter instead: `AppSettingsStore` passes its own `_logger`, and `Importer.Import()`'s instance `_logger` flows through its own `ResolveMusicPath` the same way. (This was going to shrink `Flower.Core` back to excluding `Flower/Logging/` entirely, until the "why not just move it, it's Avalonia-free and `Flower.Server` wants it too" call above superseded that — the `Importer` fix is still worth keeping on its own: a static global logger for one call site when the caller already has a perfectly good instance one was avoidable either way.)

**Startup logging/DI setup tightened as part of this pass, in `Flower` (prompted by looking at `AppLogging` closely, not really about what moved into `Flower.Core`):** `App.axaml.cs`'s DI container (`ServiceCollection`) is now created near the very top of `OnFrameworkInitializationCompleted`, immediately after `AppLogging.Initialize()` configures Serilog's sinks — and the *first* thing registered on it is `.AddLogging(builder => builder.AddSerilog())`, the standard `Microsoft.Extensions.Logging` DI pipeline, rather than `AppLogging` building its own separate `SerilogLoggerFactory` internally. That one collection then threads through the rest of `Bootstrap`, accumulating `.AddSingleton(...)` registrations as each service gets constructed exactly as before, and only gets built + handed to `Ioc.Default.ConfigureServices(...)` once, at the end (`CommunityToolkit.Mvvm`'s `Ioc.Default` can only be configured once, so the container itself still can't be *finished* until everything it holds exists — only the logging registration genuinely moved earlier). `AppLogging.CreateLogger`/`CreateTypedLogger` (used throughout `Bootstrap` for ad-hoc-`new`'d services, plus genuinely-static call sites like `RubberBandScroll`/`AlbumArtLoader` that can't take constructor-injected loggers at all) now read from that same DI-built `ILoggerFactory` via a new `AppLogging.UseLoggerFactory(...)` setter, instead of wrapping `Log.Logger` a second, independent time.

**Stays in `Flower`:** `LibraryStore`/`PlaylistStore` (thin wrappers now — their storage moved to the shared `Flower.Core/Persistence/Sql/` repositories in Tier 4.1, which is what the parenthetical here originally argued was impossible while they were JSON); `AppSettingsStore`/`ColumnVisibilityStore`/`DeviceIdentityStore`/`PlaylistSyncStateStore` (client-only concerns); `LibraryOpenSubsonicMapper` (Avalonia `Bitmap`-coupled via `AlbumArtLoader`, see above); `TrackListBuilder`/`TrackRowViewModel` (holds an Avalonia `Bitmap`); `NetworkDiscoveryService`/`PlaylistSyncService` (finding a server and syncing with it are the client's side of the conversation). `ClientLogStore`/`LogSyncContracts` were on this list too, until the log-sharing feature gained a server-side home and they moved into `Flower.Core` — see "Give log sharing a server-side home" in the changelog below.

**Reuse boundary:** `Importer.ImportAsync()` produces shared `Flower.Core.Track`s → the shared SQLite schema and repositories (`Flower.Core/Persistence/Sql/`) → server-internal mapping to shared `SubsonicSongDto` → HTTP/JSON → OpenSubsonic client maps back to its own `Flower.Core.Track`. One deliberate seam remains, `SubsonicSongDto`, which keeps the Path-can't-cross-the-wire rule out of the shared model.

> **The `TrackEntity` seam is gone — see ARCHITECTURE-REVIEW.md Tier 4.1.** It was drawn while the client was JSON and the server was EF Core, so "keep SQLite/EF concerns out of the shared model" was keeping a real asymmetry out. Both sides are now on one raw-SQLite layer, where the seam would only duplicate SQL, so the schema, the migration runner, the row mapper and the write path are shared. The server does not keep a library of its own at all: it runs on the same resident `Flower.Core` `Library`, and both hosts answer OpenSubsonic browse requests out of that library's `Snapshot` — so the album-grouping rule has one implementation (`SubsonicIdentity.AlbumIdFor`) instead of two that only tests held to the same answer. Both write through on the spot as well, and playlists went the same way once `Playlist` gained Subsonic's `comment`/`is_public`/`created_at` — the server reads `library.Playlists` directly. The server has no `Library` wrapper of its own — it resolves the shared type straight out of DI, and both hosts hand it the same `TrackRepository` and `PlaylistRepository` as its `ITrackStore`/`IPlaylistStore`, so every write — stats, stars, a finished download, a rescan, a sync merge, a playlist rename — is issued by the code that applies the change rather than by whichever caller remembered to save afterwards. Nothing stays server-side: `Flower.Server/Data/` is gone entirely. `SubsonicSongDto` is untouched and stays a seam — the wire shape is a published protocol, which is a different argument entirely.

**Mechanical steps — done:** new `Flower.Core` classlib → `git mv` `Models/`/`Importer/`/the OpenSubsonic contracts+client/pairing-trust primitives in, along with their package refs → `Flower.csproj` gets a `ProjectReference` → confirmed `Flower.Tests` passes unchanged (359/359, plus the pre-existing real-decode suite — `RequiresLibVLC` then, `RequiresFfmpeg` now — bar one flaky timing test that passes in isolation, unrelated to this move). **Next:** scaffold `Flower.Server` (`dotnet new webapi`) referencing `Flower.Core`.

### `Flower.Server` v1 — done

New `net10.0` `Microsoft.NET.Sdk.Web` project (`Flower.Server/`), referencing `Flower.Core` only (no Avalonia, no audio stack). Minimal API + Kestrel, plain HTTP, binds `0.0.0.0:4533` by default (`appsettings.json`'s `Urls` key - override via `ASPNETCORE_URLS`/`Urls` env var same as any ASP.NET Core app).

- **Schema — as built, then superseded.** Originally EF Core/SQLite in `Flower.Server/Data/`: a `TrackEntity` restating `Track`'s fields, `PlaylistEntity`/`PlaylistTrackEntity`, `FlowerDbContext` with `PRAGMA journal_mode=WAL` and `Default Timeout=30`, `IDbContextFactory<FlowerDbContext>` per request, `EnsureCreatedAsync()` instead of formal migrations. **That whole layer is gone** — `Flower.Server/Data/` is empty and the server shares `Flower.Core`'s schema, migration runner, row mapper and write path with the client, running on the same resident `Library`. See the Tier 4.1 note above for the full account. The one design decision that survived the move intact: no separate Artist/Album tables — artist and album ids are deterministic hashes of the normalized name (`SubsonicIdentity`, same normalize-then-hash shape as `Track.SyncKey`), so browsing groups rows by these instead of needing an upsert-reconciled Artist/Album table just to hand out stable ids.
- **Importer wiring (`LibraryImportService`):** runs once at startup, reusing `Flower.Core`'s own `Importer.ImportAsync` unchanged (per the "Reuse boundary" note) against `Flower:LibraryPaths` from config, upserting `TrackEntity` rows matched by `Path` and removing rows for files no longer present - same carry-forward shape as `Library.UpdateTracks`, just against SQLite instead of an in-memory list. No rescan-on-demand endpoint yet (deferred - step 3's admin UI is the natural place to trigger one).
> **The adapter is one folder.** Everything below that is specifically
> OpenSubsonic — the endpoints, the auth, the envelope, the ID3 shapes, the
> credential store and its admin routes — lives in `Flower.Server/Subsonic/`,
> reached through `AddSubsonicAdapter()` and `MapSubsonicEndpoints()`. Deleting
> the folder and four call sites leaves a server that still builds and still
> serves every Flower client; the file header in `SubsonicEndpoints.cs` lists
> them.
>
> **The media routes are shared now, not owned here.** `stream`, `download` and
> `getCoverArt` are still mapped under `/rest`, but the handlers moved to
> `MediaEndpoints`/`SyncEndpoints` and are mapped under `/api/flower/v1` too.
> Every Flower client — desktop, mobile and the browser tab — uses the latter;
> `/rest` serves them for third-party clients that speak nothing else. The gates
> differ and the bytes do not: the native gate takes a device signature or a
> stream ticket, `/rest` additionally takes a Subsonic password.

- **OpenSubsonic endpoints (`SubsonicEndpoints`):** `ping`, `getArtists`, `getArtist`, `getAlbum`, `getAlbumList2` (alphabetical/by-artist/newest/random), `getSong`, `search3`, `getPlaylists`/`getPlaylist`/`createPlaylist`/`updatePlaylist`/`deletePlaylist`, `star`/`unstar`, `scrobble`, `stream`/`download` (`Results.File(..., enableRangeProcessing: true)`), `getCoverArt` (embedded tag picture, falling back to a `cover.*`/`folder.*` file next to the track - originally a private copy of `AlbumArtLoader.TryGetLocalArtBytes`'s logic on the grounds that it lived in the Avalonia-coupled `Flower` project, since **un**duplicated: the lookup needs no Avalonia at all and now lives in `Flower.Core`'s `LocalAlbumArtReader`, shared by all three callers. The two copies had already drifted - the server's accepted only three image extensions to the client's eight - see ARCHITECTURE-REVIEW Tier 2.2). Responses are built by `SubsonicResults` over the shapes in `Flower.Server/Services/SubsonicContracts.cs` (they lived in `Flower.Core`'s `OpenSubsonicContracts.cs` until the September 2026 protocol split moved every `/rest`-only type server-side, leaving Flower's own `TrackDto` behind in `Flower.Core`) - reflection-based JSON (not source-generated), since this project isn't trimmed/AOT the way mobile is. GET-only and `f=json`-only for v1 (matches Flower's own client and every real Subsonic client is fine defaulting to json); real multi-client XML support is deferred, not designed.
- **Auth:** v1 is a single configured admin username/password (`Flower:AdminUsername`/`Flower:AdminPassword`), validated against the classic Subsonic `token=md5(password+salt)` scheme via `OpenSubsonicClient.ComputeToken` (`SubsonicAuth`), applied as an endpoint-group filter on `/rest/*`, behind two per-source-IP rate-limit budgets (a 10/60s failed-auth lockout and a 600/60s request ceiling - ARCHITECTURE-REVIEW Tier 3.1). `Flower:AdminPassword` has no default: the server throws at startup rather than boot on a placeholder. This is a placeholder scheme, not the final design - "Passwordless by design" above is the real story, and under it this configured password goes away rather than being kept as a fallback (path B replaces it with per-client generated credentials).
- **Verified end-to-end** against a real `OpenSubsonicClient` instance (not just curl): ping, browse (artists→albums→songs), search3, create/update/delete playlist, star, scrobble, ranged `stream`, `download`, ArtistID3/AlbumID3/Child all round-trip correctly.
- **Not yet built:** admin/browser auth, pairing-code endpoint, `LanGuard`/rate limiting (all step 3); a rescan trigger beyond startup; Jellyfin backend; Docker packaging.

### Pairing-code endpoint, admin auth, `LanGuard` — done

Step 3 of the build order below, built entirely on `Flower.Core`'s existing pairing/trust primitives (`TrustedPeerStore`, `SignedRequestCanonicalizer`/`SignatureVerifier`/`NonceReplayGuard`, `RateLimiter`, `LanGuard`) rather than reinventing any of them server-side.

- **Data isolation:** `Program.cs` now sets `PlatformDataDirectory.Current` to `Flower:DataDirectory` before anything touches a store, straight off `IConfiguration` (the DI container doesn't exist yet at that point in startup) - without this, `TrustedPeerStore`/`DeviceKeyStore` would resolve their file paths via `AppDataDirectory`'s per-OS user-profile default and silently read/write the real developer machine's own `~/Library/Application Support/Flower/trusted-peers.json`, exactly the failure mode `feedback_test_isolation_appdata` warns about for tests. Verified by timestamp: the real file was untouched across a full pairing smoke-test run against a `Flower:DataDirectory`-scoped one.
- **Admin auth (`AdminAuthService`) - superseded as designed, see "Passwordless by design" above.** The credential check here is slated for deletion: under the two-path design the browser pairs as a keyed device and admin routes are gated by `DeviceSignatureAuth` plus an `IsAdmin` flag, so there is no login route and no configured password. As currently built: single configured admin username/password (already-existing `Flower:AdminUsername`/`Flower:AdminPassword`) in, opaque 32-byte random bearer token out (`POST /api/admin/login`, 24h expiry, in-memory - no cookie, so no CSRF surface to defend, per the "Security hardening" section above). `POST /api/admin/pairing-codes`, `GET /api/admin/devices`, `DELETE /api/admin/devices/{fingerprint}`, `POST /api/admin/logout` (revokes the presenting token) and `POST /api/admin/logout-all` (revokes every session) all sit behind an `AddEndpointFilter` bearer check on a `/api/admin` route group.
  - **Gotcha hit and fixed:** `/login` was originally mapped on that same `/api/admin` group before the auth filter was added to it - but a `RouteGroupBuilder`'s conventions (including `AddEndpointFilter`) apply to every endpoint ever mapped on that builder instance regardless of Map-vs-AddEndpointFilter call order, not just ones mapped after the filter call. That gated `/login` behind the very bearer token it's supposed to issue, so correct credentials always came back 401. Fixed by mapping `/login` directly on `app`, outside the authenticated group.
- **Pairing-code redemption (`PairingCodeService` + `PairingEndpoints`):** admin-issued 8-char codes (excludes `0/O/1/I` to avoid transcription errors), 10-minute expiry, single-use, in-memory (losing outstanding codes on a server restart is an acceptable cost for not needing an EF migration for state this ephemeral). `POST /api/flower/v1/pair-redeem` is a new, separate route from `SyncHttpServer`'s existing device-to-device `pair-request` (that flow's semantics don't change at all) - same proof-of-possession self-signed handshake as that route (`DeviceSignatureAuth.VerifySelfSigned`, a Kestrel/`HttpContext` port of `SyncHttpServer.VerifySelfSigned`), plus a code that must exist/be unexpired/be unconsumed, consumed atomically on success, then written to `TrustedPeerStore.ApproveAsync`. Rate-limited to 5/60s per source IP (tighter than `SyncHttpServer`'s own pair limiter, since a code's entire usable life is ~10 minutes) - a code that fails redemption for a reason other than "already consumed" is left unconsumed so a legitimate device can retry within the window.
- **`LanGuard`:** now takes an optional `extraAllowedCidrs` param, and unconditionally treats Tailscale's CGNAT range (`100.64.0.0/10`) as private/allowed alongside the existing RFC1918/loopback/link-local ranges (benefits `SyncHttpServer`'s desktop↔desktop P2P too, not just `Flower.Server` - a Tailscale-reachable peer should be trusted exactly like a LAN one). `Flower.Server`'s new `FlowerServerOptions.AllowedCidrs` (empty by default) feeds further user-configured ranges (a reverse proxy on its own subnet, say) through to a global `app.Use(...)` middleware gating every route on `context.Connection.RemoteIpAddress`, the same "wildcard bind means this check is the only thing standing between LAN-only and internet-exposed" role it already plays for `SyncHttpServer`.
- **Verified end-to-end**, not just built: admin login (right/wrong credentials, rate limit), pairing-code issuance, a simulated new device (fresh ECDSA keypair via `DeviceSigningKey`, no `Flower.Server`-side signing needed since the *device* proves possession) redeeming a code and appearing in `GET /api/admin/devices`, re-redeeming the same code failing (`400`, already consumed), a bogus code failing, the redeem rate limit tripping (`429`) after 5 attempts/60s, and revoke (`DELETE /api/admin/devices/{fingerprint}`) removing the entry.
- **Not yet built:** anything that actually *uses* `TrustedPeerStore`-recorded trust once pairing succeeds - today's `/rest/*` surface still only accepts the single shared admin Subsonic token (`SubsonicAuth`), so a newly-paired device's signing key isn't yet a path to browsing/streaming on its own. That's `Flower.Web`'s problem to define (step 4) once there's a UI consuming it; TLS (LettuceEncrypt/Tailscale) and Docker packaging remain step 5.

### Server data, settings and logs on disk — done

Where a self-hosted `Flower.Server` keeps its state, which until now was "next to whatever directory you launched it from".

- **`Flower:DataDirectory` defaults to the per-OS user data location** (`ServerDataDirectory.Resolve`), not `./data`. The old default was relative to the *working directory*: `dotnet run` from the repo put it in the project folder, a published binary put it wherever the operator's shell was, a systemd unit without `WorkingDirectory=` put it in `/` — one install, several libraries, none of them findable. Explicit configuration (a container's `Flower__DataDirectory=/data`, a NAS volume) is still the point of the setting and still wins; it is just no longer the only way to get a sane answer.
- **A subdirectory of `AppDataDirectory.Path`, not the directory itself** — `~/Library/Application Support/Flower/Server`. Sharing the app's own directory on a developer machine would share `device-key.json`, so the client and the server would present the *same* device fingerprint and pairing could never work (a device would be pairing with itself), on top of two writers on one `flower.db`.
- **`flower-server.json` in that directory is the operator-editable settings file**, layered over the `appsettings.json` that ships next to the binary: the data directory is what an operator owns and keeps across an upgrade or a container rebuild, so that is where a changed setting belongs. Seeded on first run with the knobs documented as underscore-prefixed keys, so the seeded file binds to nothing — a real `"LibraryPaths": []` in there would outrank and silently blank out paths set in `appsettings.json`. `DataDirectory` is the one setting that cannot live in it, being what locates it.
  - The source is **inserted** into the configuration chain (one slot after the last `appsettings` file), not appended. Appended — the obvious way to write it — it would outrank the environment and the command line, and a container's `Flower__*` or `ASPNETCORE_URLS` would be quietly overruled by a file on its data volume. `ServerSettingsFilePrecedenceTests` pins both directions.
- **File logging, into `<DataDirectory>/logs`**, through the same `AppLogging` Serilog bootstrap the app uses rather than a second configuration of the same sinks — the server had console output only, which on a headless box means a 3am crash is whatever the init system happened to retain. `builder.Logging.ClearProviders()` first, so `AppLogging`'s console sink replaces the default provider instead of doubling every line; `Logging:LogLevel` still applies on top. `AppLogging.Initialize` gained an optional `fileSizeLimitBytes` (32 MB here, unset in the app) since a daemon's "one file per launch" can run for weeks — note retention then counts files, so a rolling host keeps the newest 10 *segments* rather than the newest 10 runs.

### `Flower.Web` scaffolding — rendering milestone done

New `net10.0-browser` `Flower.Web/` project (`Microsoft.NET.Sdk.WebAssembly`, `Avalonia.Browser`), referencing `Flower.csproj` directly — same Views/ViewModels/`Flower/Controls/` as desktop, not a reimplementation, per the original plan below. `WasmBuildNative=true` is required (not optional) for anything to render at all: Avalonia's Skia renderer needs `libSkiaSharp`/`libHarfBuzzSharp` statically linked into the WASM bundle via the `wasm-tools` SDK workload's Emscripten toolchain, or `SKImageInfo`'s static constructor throws `DllNotFoundException` on first use - confirmed by testing both with and without the flag. CI gets a matching `build-web` job (`.github/workflows/tests.yml`) installing `wasm-tools` and building `Release`, mirroring the existing iOS/Android build-check jobs.

- **No native audio at all, not just conditioned out of the project file:** the native audio stack ships no WASM build, so rather than fight MSBuild `Condition`s to strip it from `Flower.csproj` (referenced by every platform), `App.axaml.cs`'s `Bootstrap()` just never constructs the sink or `GaplessAudioManager` on `OperatingSystem.IsBrowser()` - it registers a new no-op `NullAudioManager : IAudioManager` (`Flower/Audio/`) instead. Real playback (`WebAudioSink`, Web Audio API interop) is still a later pass; this milestone is deliberately UI-rendering-only, per the build order below.
- **No P2P sync stack either, discovered empirically, not anticipated in the original plan:** `SyncHttpServer` (raw `HttpListener`) and `NetworkDiscoveryService` (mDNS multicast UDP) are trivially unavailable in a browser sandbox, but the real blocker runs deeper - **.NET-for-WASM's crypto backend has no asymmetric crypto support at all** (verified directly: `ECDsa.Create()`/`RSA.Create()` both throw `PlatformNotSupportedException` for every curve/key size tried; only symmetric crypto and hashing work), so `DeviceSigningKey` - and everything built on it (`SyncHttpServer`, `NetworkDiscoveryService`, `PlaylistSyncService`, `LibrarySyncService`, `LibraryDownloadService`, `PeerPairingService`, `PeerUnpairNotifier`, `PairedServerReachability`, `PeerTrackResolver`) - simply cannot be constructed there, and `MainViewModel` (needed by `MobileMainViewModel`, which is what actually renders on browser via the existing `ISingleViewApplicationLifetime` path Android/iOS already use) hard-required all of them as non-null constructor parameters. Fixed by making those ten parameters nullable *and* defaulted (`= null`, trailing) on `MainViewModel`'s constructor - confirmed empirically that a bare nullable-typed parameter with no default is **not** enough for `Microsoft.Extensions.DependencyInjection`'s constructor-selection to pick this constructor over `MainViewModel`'s existing parameterless one when the type isn't registered (nullable-reference annotations aren't consulted for this; only a real default value is). `App.axaml.cs` skips constructing the whole chain on `IsBrowser()` and only conditionally registers each one in the DI container (`services.AddSingleton(instance)` throws on a null instance, so unlike the constructor parameter, these are just left unregistered on browser rather than "registered as null"). `PeerLibrary` (`MainViewModel`'s live peer-browse VM, built from `DeviceIdentity`/`DeviceSigningKey`) is now nullable too, guarded at its few call sites (`MainView.axaml.cs` - desktop-only, never reached on browser anyway). Every other platform gets the exact same non-null instances as before; `Flower.Tests`' `MainViewModelSidebarNavigationTests` still constructs a real `MainViewModel` with everything non-null (positional args reordered to match).
- **Verified rendering, not just build success:** headless Chrome (via Playwright) loading the served `dotnet run` output shows `MobileMainView` actually rendering - header, settings gear, the correct "No Music Yet / Add a library folder in Settings to get started" empty-library state, and the full bottom tab bar (Recent/Songs/Albums/Artists/Playlists/Search) - with zero console/page errors. Confirmed via full-solution platform build too: Desktop, iOS-simulator (`RuntimeIdentifier=iossimulator-arm64`, matching CI), and Android all still build clean; all 366 fast tests still pass.
- **Since built:** the admin settings screen, including the "Add device" pairing-code button, and an answer to the browser-auth question above that does not need a browser-side key at all - see "The server's settings page in the browser" below.
- **Built and exercised end to end against a real server and a real browser tab:** the library. The browser head runs the same startup rescan every other head does, over an `OriginLibraryImporter` that pulls the origin server's whole catalog as placeholders and plays them on minted stream tickets — see "The browser's library" below, which turns out not to be Subsonic-shaped at all. A browser tab is now a working song list, album grid, playlist sidebar and player; what is left is browse/search/queue depth, not another missing pillar. See "The first browser hand-run" below for what that run actually proved, and the one bug it found. A paired browser session's `/rest/*` auth is still the classic Subsonic credential scheme; the admin-session token now also covers the two sync routes the browser cannot otherwise reach (`GET /library` and the stream-ticket mint — see seam 3 under "The browser's library"), but nothing on `/rest`, which still has `StreamTicketService` for callers that cannot sign.

### The server's settings page in the browser — done

The admin settings screen build-order step 4 called for, built as **the same Avalonia view and view-model the desktop app's Settings window uses**, not a second implementation of it.

**The shared screen.** `SettingsWindow` used to be the settings screen: a `Window` whose code-behind read and wrote `MainViewModel` directly, control by control. It is now a ~20-line frame around `Flower/Views/SettingsPanel.axaml`, a `UserControl` driven by a new `SettingsViewModel` over an `ISettingsBackend`. Two backends implement it — `LocalSettingsBackend` (this device: the same property sets, stores and unawaited rescan as before) and `RemoteServerSettingsBackend` (a Flower server, over its admin API) — and a `SettingsCapabilities` record decides which tabs and controls appear, so administering a headless server runs the identical XAML rather than a drifting copy. `TrustedDevicesView` is gone, absorbed into the panel's Devices tab.

- **The draft is the point.** The old window applied the alias, the theme and every checkbox the moment they were touched, and only treated the folder list as cancellable — so "Cancel" quietly kept most of what had just changed. `SettingsViewModel` holds edits until Save. Nothing about the remote case could have worked the old way anyway: every write there is a request, and firing one per keystroke is not an option.
- **Forget-this-device is confirmed inline**, not in a `ConfirmDialogWindow`. Avalonia.Browser is single-view; there is no `Window` to own a dialog, which is what made the old `TopLevel.GetTopLevel(this) is not Window` guard silently turn Forget into a no-op there.
- **Rendered as a full-page overlay in the browser** (`MainView.ShowSettingsOverlay`) and as a dialog on desktop, chosen by whether there is a `Window` at all.
- **And reached by a row in the sidebar**, for a tab opened at the server's address rather than handed over by the desktop button. The overlay could only be opened two ways before — `#page=settings` in the URL, or Cmd/Ctrl+, — and the keyboard one is not a way in that anyone finds, because the two entry points it mirrors are the macOS app menu and `MainWindow`'s, neither of which a single-view lifetime has. So the browser's sidebar ends with a **Server** section holding one row, **Server Settings** (`SidebarItemKind.ServerSettings`, built only under `OperatingSystem.IsBrowser()`), and every way in now lands on it: the row, Cmd/Ctrl+, and `page=settings` all select it. On desktop there is no row, because Settings there is the app menu's and a *selected* server is administered through the device-detail pane.
- **It is a page in the content area, not a screen over the view.** The full-view overlay it replaced covered the top bar, which is the transport, the seek bar and the volume — taken away to configure the server a track is streaming from, which is the wrong trade. The panel now sits where the track list sits (`MainViewModel.IsShowingServerSettings`, mutually exclusive with `IsShowingTrackList`), scrolled, with the same inline chrome the device-detail pane uses: a Save, no Cancel, nothing to close. It is built lazily, on the first selection of the row, and that is not about speed — constructing it is what starts the admin requests, and an ordinary listener's tab would only collect refusals.
- **What it shows is the server's settings, never the tab's own.** `OpenSettingsWindow`'s no-`Window` branch used to build a `LocalSettingsBackend`; a WASM tab's own settings are nearly all inapplicable (no library folders, no Music.app, no app-data folder to reveal) and written to an in-memory filesystem a refresh empties. Both ways in now go through `App.CreateOriginServerSettings`. Not gated on the tab actually being an administrator, for the reason the desktop button isn't: nothing client-side knows that without asking, and the panel showing the server's own refusal (`SettingsViewModel.ErrorMessage`, which carries the `{"error": ...}` body) beats a row that isn't there.
- **A 2xx is not a promise that the API answered** (`ServerAdminClient.ReadJsonAsync`). The client has always allowed for a *failure* body that is not JSON — "a proxy's HTML error page" is the case `DescribeFailureAsync` names — and had no equivalent doubt about a success body, so a 200 carrying HTML reached the deserializer and came back as a JSON syntax error about a request nothing named. On the browser head it came back worse than that: `Flower.Web` is trimmed, which strips `System.Text.Json`'s resource strings, so the reader got the bare resource key — `ExpectedStartOfValueNotFound, < Path: $ | LineNumber: 0 | BytePositionInLine: 0`. The response's media type is now checked before parsing and a parse failure is wrapped, both reporting which request was made, what came back and that something other than the server answered it; the parse error stays as the inner exception. Reported from a real session and not reproduced here — every admin route answers correctly over Kestrel, in a real browser tab, with a bundle deployed — so this makes the failure name itself rather than claiming to have fixed it.
- **And the Logs pane's Copy copies the whole pane when nothing is selected** (`LogViewer.CopyMenuItem_Click`), rather than doing nothing, which is what `TextEditor.Copy()` alone does with an empty selection. A failure this pane reports is one line of placeholder text, and reaching for Copy on it is not a request for the empty selection — which made the one line most worth copying the one line that could not be.
- **The iTunes/Music.app switches administer the server, not the browser.** They were originally capability-off for the remote backend, on the reasoning that a browser tab has no Music.app - but the machine being configured usually does: the common self-hosted case is a Mac already holding the Music.app library the server is meant to serve. The server now adopts Music.app's own media folder as a library path on the first scan that finds it - persisting it to `flower-server.json` exactly as `AppSettingsStore.Load` persists it to `settings.json`, so it is visible and removable in the folder list - and applies both per-track imports after each scan. All three switches are disabled with a note when there is no folder to find (`GET /api/admin/settings` reports `appleMusicFolder: null`), which is every non-Mac host: a switch that can only ever do nothing is worse than one that says why. `SyncDateAddedFromITunes` now defaults on in the app too - it used to be opt-in to avoid reordering anyone's Recently Added on update, which with no released users protects nobody and left the more truthful of the two dates switched off by default.
- **Which is shared with the app, not reimplemented beside it.** Three things came out of that feature having a client half and a server half. `MusicLibrarySettings` (Flower.Core) holds the settings both hosts have - the library folders and the three iTunes switches - and `AppSettings` and `FlowerServerOptions` both derive from it, so those are declared, defaulted and documented once; what is left in each is genuinely host-specific (window geometry and column widths on one side, a data directory and mDNS/LanGuard settings on the other, populated by mechanisms that don't unify either - a JSON file this process rewrites versus an `IConfiguration` stack). `ITunesIntegration` (Flower.Core) holds what those switches *mean*: whether there is a media folder to adopt (`ResolveMediaFolderToAdopt`, called by both `AppSettingsStore.Load` and the server's rescan), which of the two imports should run, and the one-line description of where they read from. And `ServerSettingsDto`/`ServerSettingsUpdateDto` moved to Flower.Core beside `LibrarySyncContracts`, replacing the server's own matching pair - every operator-editable setting was a field in both, so adding one meant adding it twice. The rest of the `/api/admin` surface is still a matching pair of records on purpose: neither side has had to edit those in step with the other.
- **The master switch adopts the folder but never un-adopts it.** Unchecking "Use iTunes/Music.app library" used to also drop Music.app's media folder from the library folders, on the reasoning that the switch owned the folder it had added. For the common case - a library that *is* Music.app's folder and nothing else - that made declining two metadata imports silently empty the whole library, since a scan of no folders finds no tracks. The switch now governs only what Flower does *to* Music.app: adopt the folder when turned on, run the two per-track imports, and stop offering the folder again when turned off. Taking the folder back out is Remove Folder's job, right below it. Both are needed to drop Music.app entirely, which is why they are two controls.
- **The app no longer has that master switch; the server still does** (October 2026). The three bullets above describe both hosts as they were. Since then the app's first desktop launch *asks* before taking a Music.app library (`ITunesLibraryOffer`, `ITunesLibraryOfferWindow`), and a question answered once leaves a switch nothing to say - so `IntegrateWithITunes` moved off `MusicLibrarySettings` onto `FlowerServerOptions`, `AppSettingsStore.Load` stopped adopting the folder on every load, and the app's Settings shows the two imports alone, usable whenever a Music.app library exists (`SettingsCapabilities.ITunesMasterSwitch` is what tells the one shared panel which host it is drawing). What the two hosts still share is `MusicLibrarySettings`' folders and two imports, `ITunesIntegration.ApplyImports` and the description line; `ResolveMediaFolderToAdopt` is now called only by the server's rescan, which gates it and the imports on its own switch. One rule came with it: the app's startup rescan no longer runs either import on a device paired to a server, which is what Settings' save already refused to do - with no master switch, and both boxes disabled on a paired device, it would otherwise have been an import nothing could turn off.
- **A settings write reloads configuration immediately** (`IConfigurationRoot.Reload()` in `PUT /api/admin/settings`) instead of waiting for the `reloadOnChange` file watcher. That watcher is debounced, so the rescan that follows a save - the page's own after a folder change, or the one the endpoint starts itself after an iTunes switch is turned on - would otherwise read the *previous* configuration and appear to have ignored the change that just triggered it.

**Serving the UI from the server.** `Flower.Server` now serves a published `Flower.Web` bundle as static files (`WebUiHosting`), with the WASM content types the default provider does not know and an `index.html` fallback that deliberately does not shadow `/api` or `/rest`. Hosting is **optional**, and that is the design rather than an omission: `Flower.Web` cannot build without the `wasm-tools` workload's Emscripten toolchain (`WasmBuildNative=true`), so a project reference would make the one head that has to compile on a headless box or in a minimal container image depend on a browser toolchain. Instead `Flower.Server.csproj` builds the bundle itself: a target probes for the Emscripten pack, and when it is there runs `dotnet publish Flower.Web` and copies the result into `$(OutDir)wwwroot` (and into the publish directory, so `dotnet publish Flower.Server` is a complete deployment on its own). `WebUiHosting` then finds it at `AppContext.BaseDirectory/wwwroot` with no configuration at all - running the server is the only step. Without the toolchain the target is skipped and the server answers `/` with a short page saying so, which beats a 404 on the address a client's button just opened. `-p:IncludeWebUi=false` skips it explicitly; `Flower:WebUiPath` overrides the location outright, and when set is the *only* place looked at, so a deliberately-named path never falls through to some other bundle.

The `.br`/`.gz` siblings are dropped on the way in - nothing negotiates content encoding, so they were ~32 MB of files that could never be served.

There is deliberately no standalone `Flower.Web` dev host any more. Run under its own `dotnet run`, the browser head is close to inert: `App.axaml.cs` skips `DeviceSigningKey` and the whole sync stack on `IsBrowser()` (WASM has no `ECDsa`), there is no library, and the settings page it exists for only works same-origin with the server it administers - a separate origin cannot reach the API at all. So the only way to run the web UI is to run the server and open its address, which is also how a user reaches it.

**How the browser authenticates — the open question from step 4, answered without WebCrypto.** The browser cannot sign: .NET-for-WebAssembly has no asymmetric crypto at all (the same finding that gutted the P2P stack there, above). Rather than build the non-extractable WebCrypto keypair the "browser is a device" design sketches — a real initiative of its own — the browser's authority is **derived** from a device that can sign:

- `AdminSessionService` mints an opaque, one-hour, fingerprint-bound token, shaped exactly like the existing `StreamTicketService` and narrowed the same three ways. `POST /api/admin/sessions` mints one; the admin filter accepts it *or* a device signature, and re-checks `TrustedPeer.IsAdmin` against the trust store on every request carrying one, so demoting or revoking a device takes effect on its next request rather than at token expiry. Revoking a device revokes its sessions along with its stream tickets.
- A session token cannot mint another one. A bearer token that can mint its own successor is not short-lived in any meaningful sense; re-minting is one click on a device that holds a real key.
- The token travels in the **URL fragment**, never the query string — a fragment is not sent to the server, so it cannot land in an access log or a `Referer` — and `weblocation.js` erases it from the address bar as soon as it is read.
- **Bootstrap**: at first run there is no admin device to mint anything, so the console prints an admin session URL beside the pairing code it already prints, under the same gate (no admin on file, or `--pairing-code`). Never on an ordinary boot.
- This does not close off giving the browser its own key later; it removes the dependency on doing so first.

> **Superseded — the derived-authority design above was the interim, and it is gone.** Every
> bullet in this section describes `AdminSessionService`, which is deleted. The browser holds
> its own non-extractable WebCrypto keypair now and signs like every other device; what the URL
> fragment carries is a single-use pairing code, not a token. See "The browser is a device, for
> real" below, and `docs/OPEN-INTERNET-REVIEW.md` finding 7 for why the interim could not
> survive a remote transport. The paragraphs are kept because the reasoning that led here is
> worth reading — including the cost this predicted, which is exactly what it cost.

**The client's way in.** The desktop device-detail pane gained a **"Server Settings…"** button for a selected headless server it has already paired with (`MainViewModel.CanOpenSelectedServerSettings`). It signs a request against that server — a session mint originally, an admin-granting pairing code now — and opens the returned URL in the OS browser. Deliberately *not* also gated on this device being an admin — nothing client-side can know that without asking, and a button that says "paired, but not an administrator" inline is a better answer than a hidden one.

**New admin routes**, all on the existing signature gate: `GET`/`PUT /api/admin/settings` (written to `flower-server.json` through `ServerSettingsWriter`, a read-modify-write over a `JsonNode` so the seeded documentation keys and anything an operator added survive), `GET /api/admin/library` + `POST /api/admin/library/rescan` (`LibraryRescanCoordinator`, on its own DI scope because the request that starts a 16k-track scan is answered long before it finishes), and `GET /api/admin/logs` off `InMemoryLogStore`, plus `GET /api/admin/devices/{fingerprint}/logs` off `ClientLogStore`.

**Whose log is read where.** Both log routes are read from one place: the **Logs tab of the server's own settings page**, which lists the server itself and then every device on its roster, and renders whichever is selected through the app's own log viewer (`LogViewerViewModel`/`LogViewer` — filter box, minimum level, font size, word wrap, an AvaloniaEdit document rather than a flat `TextBox` tail). The client's Log window (View > Log…) shows this device's own live log and nothing else. It briefly did both — a sidebar there listing the paired server and its other devices — which put the answer in the wrong place twice over: a listener's phone offering to show its server's log it cannot read, and the owner reading a device's snapshot from a window whose other row was a live tail. A client shows the client; the server shows everyone.

- **The "admin requests take no body" rule had to go**, and could: minimal APIs bind parameters *before* endpoint filters run, so a body-bound parameter would consume the stream before the signature check could hash it. No handler binds a body; the filter buffers and rewinds it, exactly as `SyncEndpoints` already does.
- **`PUT /settings` answers with the merged result, not a re-read.** Re-reading looks more honest but is not: `flower-server.json` is watched with `reloadOnChange`, and that watcher is debounced, so `CurrentValue` right after the write is usually still the old value — the page would show the change revert and then quietly reappear. It also reports `RestartRequired` for the fields `MdnsAdvertiser` reads once at startup.
- **`LibraryImportService` and the `LanGuard` middleware moved to `IOptionsMonitor`.** Both held an `IOptions<>` snapshot bound once for the life of the process, so a folder added in the browser would have been ignored by the very rescan the browser triggers next, and a CIDR added to get *back in* would not have applied until a restart.
- **A real pre-existing bug, found by the first test written against it:** every "paired but not an admin" refusal was a 500, not a 403. `Results.Forbid()` runs ASP.NET Core's authentication forbid handler, and this app registers no authentication scheme at all — it authenticates by device signature — so it threw rather than answering.

### `WebAudioManager` — real browser playback, done

Real audio now plays in `Flower.Web`, closing the "browser playback" gap flagged above. Turned out not to be a `WebAudioSink` (the originally-planned `IAudioSink` implementation feeding `GaplessRingBuffer`) - that shape doesn't work in a browser at all: `GaplessCoordinator` decodes through a native façade with no WASM build, so an `IAudioSink` alone would have nothing to consume even with a working render side. Built a browser-only `IAudioManager` instead (`Flower/Audio/WebAudioManager.cs`), bypassing `IAudioSink`/`GaplessCoordinator`/`GaplessRingBuffer` entirely and driving a single reused `HTMLAudioElement` (`Flower.Web/wwwroot/webaudio.js`) via `[JSImport]` - the browser's own decoder handles whatever format the `<audio>` element supports directly from the track's URL, the same as a plain HTML `<audio src>` tag, so there's no PCM pipeline to build at all on this platform.

- **`[JSImport]`/`System.Runtime.InteropServices.JavaScript` compiles fine in `Flower.csproj`'s shared non-browser `net10.0` TFM** (verified directly, mirroring the earlier `ECDsa`-on-WASM check) - the reference assembly ships in the standard `net10.0` ref pack, so `WebAudioManager` needed no platform-conditional compilation, same as the rest of `Flower.csproj`.
- **Module import path pitfall, found and fixed:** `JSHost.ImportAsync(name, "./webaudio.js")` (relative) 404'd - the dotnet WASM loader resolves relative import specifiers against `/_framework/`, not `wwwroot`'s own root. Fixed with a site-root-relative path (`"/webaudio.js"`).
- **No `[JSExport]`/JS→C# callbacks** - `WebAudioManager` polls `getCurrentTime`/`getDuration`/`getPaused`/`getEnded` on a 250ms `Timer`, the same pattern `GaplessAudioManager` already uses for its own `PositionChanged` polling, rather than wiring the `<audio>` element's native `timeupdate`/`ended` events back into C#. Simpler, and the staleness cost (up to one poll interval) is the same tradeoff already accepted elsewhere in the codebase.
- **Accepted v1 scope, matching the plan's own "gaplessness may lag" call:** no gapless handover (`SetUpcoming` is a no-op - a future pass could preload the next track into a second hidden `<audio>` element), no in-browser EQ (`ApplyEqualizer` is a no-op).
- **Verified end-to-end against a real synthetic WAV, not just "no exceptions":** headless Chrome (Playwright) driving a temporary diagnostic hook confirmed real decode+playback - position advancing in step with wall-clock time, `Pause()`/`Resume()` actually freezing/resuming playback (not just flipping a flag), `Position` (seek) jumping the reported time correctly, `Volume` accepted without error, and `EndReached` firing exactly once at track end. Diagnostic hook and test asset were removed after verification; `WebAudioManager` itself ships clean.
- Full-platform regression check after the change: Desktop, iOS-simulator (`RuntimeIdentifier=iossimulator-arm64`), Android, and `Flower.Web` all still build with 0 errors; all 366 fast `Flower.Tests` still pass.

### The browser's library: a shared `RemoteLibraryImporter` — built

The browser's empty library was never a missing *client*. `OpenSubsonicClient`, `LibrarySyncMapper.ToPlaceholderTrack` (a Subsonic `Child` → a `Path == null` Flower `Track`) and the bulk manifest endpoint all exist already and are already shared with the desktop. What is missing is an `IMusicImporter` face on the pull the desktop already performs, and one credential a browser can actually present. The aim here is that the browser-specific code ends up being roughly one credential class and a stream-URL step — everything else is either shared as-is or a refactor of existing desktop code that pays for itself.

**It is not Subsonic-shaped, despite what this doc used to say.** Earlier revisions named the missing piece `SubsonicLibraryImporter`, which contradicts a decision taken later and recorded in `LibrarySyncService`'s own doc comment: the desktop deliberately stopped browsing over `getAlbumList2`/`getAlbum`, because one request per album meant hundreds or thousands of connections in a burst against a real library — observed in practice as heavy iOS `nw_connection` log churn — and moved to Flower's own `GET /api/flower/v1/library` (whole catalog, one request, `ETag`/304). The browser wants that same endpoint, so the class to build is a **`RemoteLibraryImporter`** over the bulk manifest. A genuine `SubsonicLibraryImporter` is still worth building later, but only for third-party servers (Navidrome, Jellyfin) that have no such endpoint — never for talking to `Flower.Server`, which has one.

**Seam 1 — `IPeerCredentials`, the only real browser fork.** Three places build the same signed identity headers today: `LibrarySyncService.SyncWithAsync` inline, `PlaylistSyncService.AddSignedIdentityHeaders`, and `PeerOpenSubsonicClientFactory.Create` as a `PeerIdentityParamsBuilder` delegate. Collapse them onto one interface with the shape `OpenSubsonicClient` already accepts — `Authorize(method, path, query, body)` returning the key/value pairs to attach — with two implementations. `SignedDeviceCredentials` is the existing body moved verbatim (desktop, mobile, CLI); `AdminSessionCredentials` returns a single `X-Flower-Admin-Session` header and is about ten lines. That second class is essentially the entire browser-specific auth surface, and the interface is the seam that makes swapping it for a real browser keypair later a local change rather than a sweep.

**Seam 2 — `RemoteLibraryImporter : IMusicImporter` — done.** (`Flower.Core/Importer/`), constructed over a base URL, an `IPeerCredentials`, the origin server's fingerprint and this device's own. Its body is lifted, not rewritten, from `SyncWithAsync`: the GET, the `If-None-Match`/304 handling, the `LibrarySyncManifestDto` deserialize, then `ToPlaceholderTrack` per song. It exposes both `ImportAsync` (the `IMusicImporter` contract, ETag ignored) and a `FetchAsync(ifNoneMatch)` returning not-modified/ETag/tracks. `LibrarySyncService` then keeps only what is genuinely its own — the per-peer `_lastSeenTokens` cache, `PeerTrustRejected`, `SyncRolePolicy`, and the additive merge into `Library` — and calls `FetchAsync` instead of hand-rolling its own request. Desktop behaviour is unchanged and there is one HTTP path where there were two. Built as designed, in `Flower.Core/Importer/RemoteLibraryImporter.cs`, with `LibrarySyncMapper` and a `LibrarySyncJsonContext` moved/added alongside it so the browser can read a manifest without loading the Avalonia project. Two details the design did not name: the 304 answer is a `NotModified` flag on the returned `RemoteLibraryFetch` rather than an empty track list, because a caller that cannot tell those apart hands `Library.MergeSyncedTracks` an empty catalog and prunes every placeholder it holds; and a refusal throws rather than returning empty, for the same reason plus so `LibrarySyncService` can keep telling 403 (revoked) from 401 (bad signature). `ConnectionClose` is a constructor flag — true for a peer's `HttpListener`, false in the browser where the fetch stack owns connection reuse. Covered by `RemoteLibraryImporterTests` (importer alone, against a real socket) on top of the existing `LibrarySyncConditionalPullTests` (through the sync service).

Two constructor arguments carry the browser's asymmetry. `ownFingerprint` is empty there: it exists only so `ToPlaceholderTrack` can filter this device's own echo out of an incoming `RemotePlayCounts`, and an empty string matches nothing. `originFingerprint` is the *server's*, which the browser has no other way to know — it must read it from `/info` (`SyncProtocol.InfoPath`) at startup. Placeholder tracks without it are unplayable, so this is a real prerequisite and not a detail.

**Seam 3 — the server accepts the session token on two more routes — done.** `SyncEndpoints`' auth filter gates on `DeviceSignatureAuth.AuthenticateTrustedPeer`; add a fallback that resolves a valid `X-Flower-Admin-Session` to its minting fingerprint via `AdminSessionService`, and the same fallback on `POST /api/flower/v1/stream-tickets`. No new endpoints, and the browser can then both read the catalog and mint playable URLs. Built as `Flower.Server/Services/PeerOrSessionAuth.cs`, one call used by both routes: signature first, session token only if that failed. Narrower than `/api/admin`'s version in one way and wider in another — a resolved session there still has to clear `IsAdmin`, whereas here it only has to still be a peer this server trusts, since that is all a signature would have proved either. That trust check is made live on every request rather than taken from the token, so a device revoked through any path (present or future) loses catalog access immediately and not at session expiry. Pinned by `BrowserSessionSyncAccessTests`, which sends requests carrying no signature headers at all — exactly what a browser tab can produce — and checks that a minted ticket is still bound to one track and still attributed to the minting device, so revoking that device kills it.

This **widens the admin-session bearer past `/api/admin`**, which `AdminSessionService`'s own doc comment records as a deliberate boundary, so it is a decision and not a detail. The case for it: both additions are read-and-play rather than administration, and `LanGuard` still keeps the token unusable from off the LAN. The case against is that it is still a bearer token, and it is now a bearer token for the catalog. The principled alternative is the non-extractable WebCrypto keypair the "browser is a device" design sketches, which is **not** a drop-in: `DeviceSigningKey.Sign` is synchronous and `crypto.subtle` is asynchronous, so adopting it re-shapes every signing call site. Recommendation: take the bearer fallback now, keep the keypair as the follow-up, and note that a 60-minute session minted by the desktop's "Server Settings…" button is thin for a jukebox tab left open all evening — session lifetime or renewal will need revisiting once the browser is a player rather than a settings screen.

**Seam 4 — playback, where sharing genuinely runs out — done.** `WebAudioManager.Play` sets the `<audio>` element's source to `track.Path ?? string.Empty`, which is always empty for a placeholder track. It needs to mint a stream ticket and use the returned URL. It cannot reuse `PeerTrackResolver`, which resolves a `DiscoveredDevice` through mDNS discovery — the browser's server is simply its own origin. The awkward part is that `Play` is synchronous while minting is a network call.

Resolved by making the seam itself asynchronous rather than by pushing a pre-resolve step up into the callers: `IStreamUrlResolver.Resolve` is now `Task<string?> ResolveAsync(Track)`, and `PlaylistControlViewModel.Play` checks `IsCompleted` on what comes back. `PeerStreamUrlResolver` builds a signed URL out of pure computation, so its task is always already completed and every non-browser head still starts playback on the calling stack — which is what keeps the dozens of existing tests that play a track and assert on the next line honest, rather than turning them all into timing questions. Only when the task is genuinely pending does `Play` defer: it hands off to `StartWhenResolved`, which finishes on the UI thread and drops the result if anything has been started since (a `_playGeneration` counter — otherwise pressing Next while a URL is in flight lets the abandoned track hijack playback when it lands). `SetUpcoming` got the same treatment via `ArmUpcoming`, which doubles as the browser's cache priming: the next track's ticket is minted while the current one is still playing, so auto-advance normally finds a URL waiting.

The minted URL is `/api/flower/v1/stream?id=…&ticket=…`; it was `/rest/stream` until the media routes moved onto Flower's own surface, which also let the ticket stop being a credential `/rest` accepts at all — it now opens the two media routes in `SyncEndpoints` and nothing else. The browser implementation is `StreamTicketUrlResolver` (beside the interface), which `POST`s to `/api/flower/v1/stream-tickets?id=…` with `AdminSessionCredentials` and caches the minted URL per origin track id, re-minting once a ticket is within five minutes of expiring. That margin exists because a media element re-presents its ticket on every range request a seek makes, so a ticket has to outlive the *track*, not the request that started it. A track longer than `StreamTicketService`'s whole 15-minute lifetime will still lose seekability partway through; the fix for that belongs on the server (a ticket that renews while it is being read) and is not built.

This also completed **seam 1**'s missing half: `AdminSessionCredentials` (`Flower.Core/Services/`) is the ten-line class that design predicted, and it now owns the `X-Flower-Admin-Session` header name that `AdminEndpoints` and `BrowserSessionSyncAccessTests` both used to spell out separately. Covered by `StreamTicketUrlResolverTests` (the resolver against a real socket — the route, the id, the header, reuse, renewal, and both refusal paths) and four new cases in `PlaylistPlaybackIntegrationTests` for the deferred start, including the out-of-order hijack.

**Not wired up yet:** nothing registers `StreamTicketUrlResolver`, because that needs the session token in the container — seam 5.

**Seam 5 — startup wiring — done.** The `if (!OperatingSystem.IsBrowser())` guard around the startup rescan in `App.axaml.cs` is gone; the block runs whatever `IMusicImporter` is registered, and the only surviving fork is the two iTunes syncs, now gated on a new `IMusicImporter.ScansLocalFiles` rather than on a platform check — they read *this* machine's Music.app database and have nothing to say about a catalog pulled off a server. That makes "local files" versus "self-hosted server" a choice of importer rather than a special-cased second code path, which is what the abstraction existed for; it is also where the original `IMusicSource` proposal landed, under the name `IMusicImporter`.

The browser branch of `RegisterServices` is no longer a bare early return: `RegisterBrowserServices` registers a shared `HttpClient`, `AdminSessionCredentials` (as both itself and `IPeerCredentials`), `OriginLibraryImporter` as the `IMusicImporter`, and `StreamTicketUrlResolver` as the `IStreamUrlResolver` — the four things a tab has instead of the peer-to-peer stack.

Two problems the design did not name. **The fingerprint has to be read before the catalog is worth having,** and `RemoteLibraryImporter` wants it at construction while DI registration is synchronous — so `OriginLibraryImporter` wraps it and resolves `/info` lazily, on the rescan's already-asynchronous path, once per tab. It throws rather than importing without one: a library of rows that can never play is worse than an empty one. **And the session token can only be read once** — `BrowserLocation.TakeFragment` erases the fragment as it reads it — so a new `BrowserSession` reads it at registration and everything else takes it from the container. That also forced a small behaviour change: the settings overlay now opens on the fragment's `page=settings`, not on the mere presence of a token, since an ordinary jukebox tab carries one too. A tab opened with no token at all registers `EmptyLibraryImporter` and shows nothing, rather than inheriting the filesystem scanner and sending a browser sandbox looking for a music folder.

Covered by `OriginLibraryImporterTests` — the fingerprint reaches every placeholder, `/info` is read once across repeated rescans, a server that will not identify itself yields no library, and the `ScansLocalFiles` answers each importer gives.

**Known to remain afterwards:** nothing of the two named here. Album art is done (see "Album art in the browser" below) and so are playlists (see "Playlists in the browser"), which were indeed less work than they looked because `SyncEndpoints` already served `GET /api/flower/v1/playlists` over the very gate seam 3 opens.

### The first browser hand-run — done

Everything above was built seam by seam and covered by tests, but nothing had ever run all of it at once against a real server in a real browser. It does now, and it works: a scratch `Flower.Server` over a 12-track library, a tab opened at `#admin=<token>`, and the whole chain visible in the server log in the designed order — `GET /api/localsend/v2/info` (the fingerprint), `GET /api/flower/v1/library` (the catalog), `POST /api/flower/v1/stream-tickets` and `GET /rest/stream`. The tab rendered the catalog, played a track on a double-click, and auto-advanced into the next one — whose ticket, the log shows, had already been minted while the first was still playing. `page=settings` opens the settings overlay against the live server; a tab with no token at all reports 0 songs via `EmptyLibraryImporter` instead of sending a browser sandbox looking for a music folder.

**The one bug it found was invisible to every test we had, and could only ever have been found this way: the trimmer was eating the track list's columns.** `#`, `Duration`, `Plays`, `Added` and `Last Played` rendered blank in a tab while `Title`/`Artist`/`Album`/`Genre` were fine. The split is exactly the binding style: `TrackRowControl.BuildCellContent` binds the working columns through `Track.*`, whose properties are referenced directly all over the codebase, and the blank ones by *string* to `TrackRowViewModel.DurationDisplay` and its siblings, which nothing references any other way. ILLink therefore had no reason to believe those getters were used and dropped them. Only `Flower.Web` trims (`WasmBuildNative`), so desktop, iOS and Android were all unaffected and no unit test could see it — the property exists and returns the right string right up until the linker deletes it.

The method carried an `[UnconditionalSuppressMessage]` asserting the paths were "kept alive by direct references elsewhere", which is precisely the thing that was not true. That is now two `[DynamicDependency]` roots instead, declared on `BuildCellContent` itself because the dependency is genuinely that method's — it is what turns a column id into a property name.

Worth remembering as a class of bug rather than an incident: any reflection-shaped lookup that works on the desktop can still be trimmed out from under the browser head, and only running the browser head will say so.

### Album art in the browser — done

The hand-run left a tab showing a placeholder icon on every row, because `AlbumArtLoader` asked `PeerTrackResolver` where to fetch art from and a tab has no peers. Fixed the same way playback was, one layer up: `ICoverArtUrlResolver`, with `PeerCoverArtUrlResolver` for the app and `OriginCoverArtUrlResolver` for a tab, so the loader no longer knows which head it is running on.

It is *not* the same as seam 4, which is what this doc used to predict. Streaming needs a ticket because an `<audio>` element issues the request itself and cannot carry a credential; art is fetched by `AlbumArtLoader`'s own `HttpClient`, which sends the session header like any other call. So there is nothing to mint here and the resolver is synchronous — the asynchrony in `IStreamUrlResolver` exists for the ticket alone.

What the server needed was a door a tab can open. `/rest/getCoverArt` authenticates with a Subsonic credential or a device signature, neither of which a tab has, so `GET /api/flower/v1/cover-art` now serves the same bytes from behind the gate seam 3 already opened for `GET /library`. Deliberately the *same handler* rather than a second answer to "what is this album's art" — `SubsonicEndpoints.GetCoverArt` is internal now and both routes call it.

Verified in a real tab against a real server: rows show their covers and the Albums grid paints its tiles.

### Playlists in the browser — done

Read-only, and one-way on purpose. `PlaylistSyncService` — the path a desktop takes — is a negotiation: the two sides elect an initiator by comparing fingerprints, compare `UpdatedAt`, raise conflicts for a human, and push the merged result back. None of that fits a tab, which has no durable identity to be a party to a merge (its whole database lives in a sandbox a refresh may reset), no second device to elect against, and nothing of its own to contribute — its playlists *are* the server's. So `OriginPlaylistImporter` reads `GET /api/flower/v1/playlists` and stops.

`IPlaylistImporter` is a second interface rather than another method on `IMusicImporter`, because on a desktop the two questions have different answers on the same host: tracks come from disk, playlists come from its own database, and only one of them is ever remote. Optional in the container — only the browser registers one, and a head without one keeps the playlists it loaded from its own store.

Resolution is the interesting half, not the fetch. A playlist on the wire names its tracks by title/artist/album/duration rather than by id or path (see `Track.SyncKey` — a path means nothing on another device), so `PlaylistSyncMapper.ToPlaylist` matches them against the library that was just imported and silently drops anything this head does not have. For a tab those two normally agree exactly, since its library *is* the origin's catalog; a disagreement means a rescan caught the server mid-edit.

One thing the hand-run caught that the design did not: it must be `Library.ReplacePlaylists`, not `ResetPlaylists`. Reset exists to replay the on-disk set into a `Library` before any window is created and deliberately announces nothing, so calling it from the background rescan fetched the playlists and left them invisible — the sidebar had already been built and nothing told it to rebuild. Replace fires `PlaylistsUpdated`, and no-ops when the set came back identical, which is every rescan after the first.

Editing a playlist in a tab now does reach the server — see the next section, which was written after this one.

### A tab that writes — done

The first browser path that is not read-only. `OriginPlaylistWriter` pushes the whole playlist set to `POST /api/flower/v1/playlists/apply`, which already sat behind the same gate the tab's reads go through, so no new endpoint and no new authority were needed — only a client for one that existed.

Wholesale replacement is the honest shape here rather than a shortcut. The peer path means "the initiator resolved every conflict, adopt the result"; for a tab there is nothing to resolve, because a tab is the only editor of a set that is not its own. Which is also why this is still not `PlaylistSyncService`: no election, no `UpdatedAt` comparison, no conflict UI. The tab edits the server's playlists, and says so.

Two things had to be right that a single fire-and-forget POST would have got wrong, and both are in `OriginPlaylistWriter` rather than at the call site:

- **Echo.** `ReplacePlaylists` raises `Library.PlaylistsChanged` exactly as a user's own edit does, and nothing in that event says which of the two it was — so the rescan calls `NoteOriginState` with the fetched set *before* installing it, and a push whose manifest matches what the origin is known to hold is skipped. Without it every rescan would hand the server its own playlists back.
- **Ordering.** A drag-reorder raises one change per move, and each push carries the whole manifest, so overlapping POSTs mean the last to *arrive* wins — not the last sent. One push at a time, with a burst collapsing to its final state.

`PlaylistsChanged` is the subscription, not `PlaylistsUpdated`: the write path needs exactly the case that one deliberately skips (a local edit), and it is already the event that means "the on-disk copy is stale" — for a head whose playlists live on a server, the server's copy *is* the on-disk copy.

Hand-run against a real server and a real tab: right-click a track → Add To Playlist → New Playlist, then rename it. Two `apply` posts (create, then rename-commit — not one per keystroke), the server logged both, `GET /playlists` served the result back, and reloading the tab produced no echo push. It also fixed the log line those writes land on: `ApplyPlaylists` read the request's own `X-Flower-Fingerprint`, which a tab never sends, so every browser write was recorded as "pushed by null". The gate now stashes the fingerprint it actually authenticated, and a tab's write is attributed to the session's device.

Still out of scope, and worth saying because "a tab can write" sounds broader than it is: playlists only. Play counts, ratings and last-played still change in a tab and stay there.

### Browsing a tab like a jukebox — done

The plan's last browser item was "full jukebox browse/search/queue", which turned
out to be mostly already true and worth checking rather than assuming. A hand-run
against a real server and a real tab exercised each half: the sidebar's Recently
Added / History / Songs / Albums / Artists, the filter box, the Artists
drill-down, playing a track, and Next/Previous. Browse, search and queue all
worked as they do on the desktop — they are shared UI over a library the tab
already has in full, so there was nothing browser-specific left to build.

What the run did find was **the now-playing panel showing a blank square while
every row showed its cover**. The cause was a second, independent album-art
implementation: `CurrentlyPlayingControlViewModel` did its own embedded-tag and
cover/folder-file lookup rather than going through `AlbumArtLoader`, which the
track list and `TrackInfoWindow` use and which already knows how to fetch a
placeholder's art from the origin (see "Album art in the browser" above). Two
copies of one question, and only one of them had been taught about the server.

The interesting part is *why* it was blank, because the obvious answer is wrong.
It is not that a placeholder has no `Path`: by the time a track is playing,
`PlaylistControlViewModel.ResolveForPlaybackAsync` has cloned it and put the
minted stream URL in `Path`. So the panel bailed on its "a `Path` containing
`://` is not a file" guard — correct as far as it went, since no filesystem read
can satisfy that path, but it concluded "no art" where the right conclusion is
"art, and on the origin". That test now lives in `AlbumArtLoader.IsLocalFile` and
routes such a track down the remote road instead of the local one, which is right
for every caller rather than just this one.

Two things fell out of moving art onto the shared loader:

- **Ordering.** Art is a network round trip now, so pressing Next twice can land
  the first track's art after the second's. Guarded with a generation counter,
  the same way `PlaylistControlViewModel` guards a stream URL in flight.
- **Ownership.** The panel used to dispose the previous bitmap when replacing it,
  which was correct when each was a private decode it had made itself. The
  loader's bitmaps are cached and shared with every row showing that album, so
  disposing one would blank the list; the panel now only holds a reference.

Also fixed while in there: the subtitle rendered a bare `()` after the album for a
track with no year. Possible on any head, but caught here, where every row comes
from a server whose own import may not have had a year to give.

**Out of scope when this section was written, and built straight after:** play
counts and last-played used to change in a tab and stay there. A tab whose
*listening* reaches the server was the next real browser feature, and it was a
different shape from the playlist writer — per-event increments, not a set to
replace wholesale. See "A tab that listens" below.

### A tab that listens — done

The last browser item: a play in a tab changed a counter in the tab and stayed
there, lost at the next refresh (a tab's filesystem is an in-memory WASM one) and
invisible to every other client of the same server. `IPlayReporter` /
`OriginPlayReporter` is the third road out of a tab, after `IMusicImporter` in and
`IPlaylistWriter` back, and it rests on the same premise as the writer: a tab has
no durable identity and nothing of its own to contribute, so a play of the
server's track is the server's play.

**Why events and not a count.** The obvious instrument was the existing
`Track.RemotePlayCounts` G-Counter, which two desktops already merge by per-key
max. It is the wrong one here, and interestingly so: max-merge only converges
because each device's own total never regresses. A tab's does — it starts at zero
on every refresh — so max-merge would settle on "whatever the highest single
session happened to reach" and silently discard everything after it. A counter
needs durable storage to state a total from, and the tab is the one participant
that has none. So the tab reports increments as they happen and the server, which
does have storage, keeps the running total.

**The two halves stay separate on the wire.** Flower deliberately stamps
`LastPlayedAt` when a track starts and bumps `PlayCount` when it ends naturally
(see `Track.LastPlayedAt`), so a skip is History without being a listen.
Collapsing them into one scrobble-shaped event — which is what `/rest/scrobble`
does, reasonably, for a single request — would have made a tab's History and the
server's disagree in *content*: a skipped track would appear in one and not the
other. `Library.TrackChange` is a `[Flags]` enum carried on
`TrackChanged`, so the reporter can tell them apart and `Library.RecordPlay`
can apply whichever halves a request names.

Three things the reporter has to get right that a POST-per-play would not:

- **Addressing.** The server knows the track by `OriginTrackId`, not by the local
  `Guid` a tab minted for its own placeholder. A track carrying no origin id came
  from somewhere else and is dropped rather than guessed at.
- **Backlog.** Unlike a rejected playlist edit, a swallowed play is not still on
  screen for the user to make again — so this one *does* retry, where
  `OriginPlaylistWriter` deliberately does not. Events queue, one POST is in
  flight at a time, and a failed batch goes back to the front to leave with the
  next play. Bounded at 500 events, oldest dropped first.
- **Duplicates, which that retry creates.** A batch the server applied but whose
  response never arrived is re-sent, and an increment applied twice is wrong in a
  way a wholesale replacement never is. Each event carries an id;
  `PlayReportService` drops one it has already applied. Deliberately not
  `NonceReplayGuard` despite the identical shape — that one's retention is tied to
  the signature validity window, and sharing it would make either window wrong for
  the other.

**The bug the hand-run found first was on the near side.** A tab's plays were not
reaching the tab's own library either. `Library.ResolveCurrent` matched the played
track against the library *by path*, and the track that comes back through
`IncrementPlayCount` on a streaming head is the clone carrying a minted stream URL
— which matches nothing. Every play a tab made was counted onto an object thrown
away at the next track change. It now resolves by `Id` first (which `Clone()`
preserves for exactly this kind of reason) and falls back to path, which is still
needed: a rescan mints brand-new `Id`s, so a caller holding a pre-rescan reference
can only be matched by the file it names.

**And the return leg, which is what makes any of it visible.** Reporting worked
and a fresh tab still showed an empty Plays column, because the server stored the
counts and never served them. `SubsonicMapper.ToChild` — then the server's copy of a
mapper the app also had — simply never filled `Child.PlayCounts`, where the app's
`LibraryOpenSubsonicMapper.ToChild` always had. Now it does, under this server's
own fingerprint. (Both are since one function: the app's copy had no callers left
and was deleted, and what survives is `LibraryDtoMapper.ToTrackDto` in
`Flower.Core`, `Child` having become `TrackDto`.) `Child` also gained
`LastPlayed`, the History counterpart to the `DateAdded` field beside it; without it a receiving head's History is empty until
it plays something itself, which for a tab means after every refresh. The `/rest`
browse endpoints deliberately pass no fingerprint and send no counts — that is
what a third-party Subsonic client expects, and a Flower client pulls its catalog
through the bulk route anyway.

Hand-run against a real server and a real tab: playing a track logged `Applied 1
of 1 play event(s)` and wrote `last_played_at` with `play_count` still 0; letting
it run to the end bumped the count to 1 and stamped the auto-advanced next track
as started. The tab's own Plays and Last Played columns filled in — which before
the identity fix they never did — and a fresh tab against the same server showed
Last Played carried back in the manifest.

**Still out of scope, and now honestly so: ratings.** There is no rating on
`Track` and no UI that sets one, so there is nothing for a tab to report.
`Starred`/`StarredAt` exists and `/rest/star` sets it, but no client-side UI does,
so it is a server-side flag with no browser story to build yet. When a liked-songs
view lands, starring travels this same road.

### A paired device that listens, stars and configures — done

The tab's counterpart, and a gap that survived it: a play on a paired *desktop
or phone* also stayed where it happened. `IPlayReporter` is registered in the
browser branch alone, and nothing else ever reported anything — `/rest/scrobble`
exists on the server and `OpenSubsonicClient.ScrobbleAsync` exists on the client,
but no caller ever joined them. So the owner's own desktop, listening to its own
server's library all evening, left the server's counts at zero and every other
listener seeing zero with it. The sync is one-directional by design (a Client
pulls from its Server, a Server never pulls — see `SyncRolePolicy`), so there was
no return leg at all for something the client learns.

**Totals, not events — the opposite call from the tab, for the reason the tab's
own section names.** A tab reports increments because it holds no durable counter
to state a total from. A desktop or a phone does, so it states one:
`POST /api/flower/v1/track-state` carries `(OriginTrackId, count)` pairs, and the
server merges each into `Track.RemotePlayCounts` under the fingerprint the
request *signature* proved. That is the same G-Counter two desktops used to
exchange through each other's manifests back when both ends served one, and it
brings its properties along: per-key max means re-sending after a failed push, or
after a restart, converges rather than double-counting, so there is no event id
to allocate and nothing for the server to remember having applied.

**And it keeps the play attributed.** A tab's plays land in the server's own
`PlayCount` because a tab is nobody. A paired device is somebody, so its count
stays under its own name — which is also what makes the count safe to keep
locally *and* report: the client's own tally and the server's are different
entries in the same map, and `Track.TotalPlayCount` sums them without either one
absorbing the other.

**What gets reported** is anything carrying an `OriginTrackId` with a non-zero
`PlayCount` — plays counted in Flower, the tally the manifest sends under a
device's own name. (Music.app's count rode in the same sum until October 2026
and travels apart now; see "Music.app's count travels on its own".) Deliberately no distinction between a placeholder, a
downloaded file, and a local file of this device's own that the server also has:
`MergeSyncedTracks` stamps the server's id onto a local match too, and a play of
that song is a play of that song in that shared library.

**It rides the log-push tick, not the catalog sync.** A play does not move the
catalog — a play's `TrackChanged` deliberately does not schedule a sync (that was
ARCHITECTURE-REVIEW Tier 1.1) — so counts pushed only from `SyncWithAsync` would
sit on the device until something else happened to change the library. The push
looks only at tracks a `Library.TrackChanged` marked since the last one got
through, so joining the five-second tick costs nothing when nothing happened. A
resume position does not wait for it: it is pushed as soon as playback stops,
and a desktop gives that push two seconds on quit. What it does need is a
per-peer memory of the highest total already acknowledged, or a library with
thousands of played tracks re-states all of them every tick. In memory only, like
`_lastSeenTokens`: a restart re-sends, the far side takes the max, and nothing is
wrong for having said the same true thing twice.

**Not gated on a setting**, unlike the log push beside it. A log snapshot carries
exception text and absolute paths off the device and so ships off by default; a
play count is the shared library working as intended, and it is the same number
the pairing screen already showed the user they were joining.

**The count was the easy half.** `Library.CarryForwardMutableState` is THE list
of what a rescan must not reset — which is the same list as "what this device
knows about the track that reading the file cannot tell you" — and the count is
one line of it. `LastPlayedAt`, `Starred`/`StarredAt` and the four Track Info
playback options are the rest, and all four had exactly the same problem: they
moved on the client and died there. Starring was worse than invisible, in fact —
`Child.Starred` has crossed the wire from this server since the day it was
written, and `LibrarySyncMapper.ToPlaceholderTrack` simply never read it, so a
star was unknown in *both* directions.

**Why the rest of it is admin-only.** A count is additive and attributed: it is
filed under the reporter's own fingerprint and adds to a total, so a housemate's
phone saying "I played this twice" is a fact about the phone and cannot damage
anyone else's answer. The other fields are single-valued on the library's own
copy of the track, so writing one is not adding a fact, it is *replacing the
library's answer* — an owner's act. So the split is per-field rather than
per-route: `POST /track-state` stays open to any paired device, reads adminness
from `TrustedPeerStore` and hands it to the merge, and a non-admin's report still
lands — its count does — while the rest is dropped rather than refused. A 403
would have been wrong, because the count in the same body is legitimate.

**Three fields, three merge rules**, because one blanket last-writer-wins fits
none of them:

- `LastPlayedAt` moves **forward only**. It is a high-water mark of "when was
  this last put on, anywhere"; a device reporting an older session is not news.
- The playback options **ride with it**, exactly as `MergePlaybackOptions` has
  them ride in the pulling direction. A resume position is not a counter and
  going backwards in a file is normal, so the device that played the track most
  recently is the one whose idea of where it got to is worth having, and the
  three settings stay internally consistent with it.
- `Starred` is **taken as stated**. Unstarring nulls `StarredAt`, so there is no
  timestamp surviving the operation to order by, and a value that moves *down* is
  not something max can carry.

That last one is only safe because of what changed on the client, which is the
part worth recording. **A count's baseline is what this device successfully
sent; owner state's baseline is what the server last said.** Every catalog pull
seeds `_knownServerState` from the served manifest — recorded, deliberately not
applied, since `MergeSyncedTracks` decides what a pull may overwrite and this
does not widen it — and the push then reports a field only when the local answer
*differs from the server's own*. Which for an unchanged track is never. So a
restart re-states nothing, and cannot walk back a star some other client set in
the meantime; and a track with no seed at all (nothing pulled yet this session)
is left alone, because with nothing to compare against there is no way to tell a
local change from a local value. `StarredAt` is outside the comparison for the
same reason it is outside the wire: two devices agreeing on the star must not be
made to disagree forever over the second it was clicked at.

**Still one-directional for these fields on the way back down.** The pull now
carries `Starred` onto a newly-created placeholder, but `MergeSyncedTracks`
still leaves an already-known track's `LastPlayedAt` and `Starred` alone —
overwriting them would race the push it just seeded. With one admin device that
converges fine; with two, each keeps its own history and the server holds the
newest of what it was told. Closing it properly means the pull consulting the
same baseline the push does, and it is the obvious next piece of this.

### Tailscale, and the proxy the guide creates — done

The remote-access path is written up for a user in `docs/SELF-HOSTING.md`
(deliberately a user-facing guide, not a design record - the reasoning stays
here). "Document, don't automate" held: there is no Tailscale integration in the
server and there should not be one. `sudo tailscale up`, `sudo tailscale serve
--bg 4533`, and the server is reachable from anywhere over WireGuard with a real
Let's Encrypt certificate that Tailscale provisions and renews. `LanGuard`'s
CGNAT allowance, built in Phase 4, was already the only code this needed.

Writing it turned up one thing that was not just documentation. **`tailscale
serve` terminates TLS and proxies onward over loopback**, so with it enabled
every request reaches Kestrel from `127.0.0.1`. Three per-IP rate limiters
(pair-redeem, sync, `/rest` auth) and the `LanGuard` gate itself all read
`Connection.RemoteIpAddress`, so on the deployment the guide recommends they
would all have collapsed into a single bucket shared by the whole tailnet: five
bad pairing attempts from one phone locks out every other device for a minute,
and every log line attributes everything to loopback. Nothing looks broken,
which is what makes it worth a test rather than a sentence.

So `FlowerServerOptions.TrustedProxies` (CIDRs, empty by default) drives
`UseForwardedHeaders` ahead of the `LanGuard` middleware. Three things about the
shape:

- **Empty by default, and no header believed from anyone.** `X-Forwarded-For` is
  written by whoever sent the request. Believing it unconditionally would hand
  every caller a free way to choose its own source address - past the allow-list
  and out of whatever bucket it had just exhausted. ASP.NET's own defaults trust
  loopback out of the box, so `KnownIPNetworks`/`KnownProxies` are cleared before
  the configured entries go in: on a box where anything else is listening,
  loopback is reachable by every local process.
- **The guard still applies to the substituted address**, which is what makes
  the feature safe to have at all. A proxy in front of the server is not a way
  around the gate; it moves the gate to the address that actually matters. A
  deployment genuinely fronting public traffic has to widen `AllowedCidrs` and
  mean it.
- **Not in `ServerSettingsDto`, unlike `AllowedCidrs`.** This is a fact about how
  the server was deployed rather than a preference, and the browser page served
  *through* the proxy is the last thing that should be able to redefine who the
  proxy is. Same reasoning as `WebUiPath`; same consequence, that it is read once
  at startup.

`Flower.Server.Tests/ForwardedHeaderTests.cs` covers it, and the fixture trusts
`203.0.113.0/24` alongside loopback on purpose: loopback is an address `LanGuard`
admits anyway, so a test proxying only from there cannot tell "the forwarded
address was substituted" from "nothing happened at all". Three of the five tests
were confirmed to fail with the middleware disabled, including the rate-limit
one; the other two guard against over-trust, which only the middleware's presence
could break.

**The client-side leg was missing, and this section originally did not notice.**
"Document, don't automate" was right about Tailscale itself, but it quietly
assumed a client could be pointed at the resulting address. It could not:
`PairedServerReachability` resolved the paired server out of
`NetworkDiscoveryService.KnownDevices`, which only mDNS populates, so
reachability was *defined* as "discovered on this link right now" and nothing
persisted an address to fall back on. The setup guide written alongside this
told the reader to "add the server by address instead", a feature that did not
exist. See `REMOTE-ACCESS.md`, which owns that half: the server reports its
own addresses in the `/info` handshake, the client remembers them for its paired
server, and reachability becomes a ranked probe (mDNS, then LAN, then tailnet).

**Not verified against a real tailnet.** There is no Tailscale on this machine,
so the proxy behaviour is exercised through the test host rather than through
tailscaled. The claim that `tailscale serve` forwards `X-Forwarded-For` over
loopback is from its documented behaviour, not from an observed request - worth
confirming on a real deployment, since the guide tells people to configure for it.

### Suggested build order

1. **Done.** Extract `Flower.Core` (mechanical git-mv + reference fixups; confirm `Flower.Tests` still passes unchanged).
2. **Done.** Scaffold `Flower.Server`: SQLite schema (originally EF Core, since moved onto `Flower.Core`'s shared layer), importer wired up, OpenSubsonic endpoints working against a real Navidrome-compatible client. See "`Flower.Server` v1" below.
3. **Done.** Pairing-code endpoint + admin auth + `LanGuard` CGNAT allowance + rate limiting on the redeem route — get a real device pairing against a real headless instance before building UI on top of it. See "Pairing-code endpoint, admin auth, `LanGuard` — done" above.
4. Scaffold `Flower.Web`. **Done so far:** existing Views/ViewModels building and rendering in-browser (see "`Flower.Web` scaffolding — rendering milestone done" above), real audio playback via `WebAudioManager`, and the admin settings screen with the pairing-code "Add device" button, served by `Flower.Server` itself (see "The server's settings page in the browser — done" above). `RemoteLibraryImporter` itself, over the bulk `/api/flower/v1/library` manifest, is built and shared with the desktop (see "The browser's library" above — *not* the Subsonic-shaped importer earlier revisions of this doc called for). The server-side gate that lets a browser tab reach it without a signing key is built too (seam 3). Stream-ticket playback (seam 4), the `/info` fingerprint read and the browser DI wiring (seam 5) are built too, so a browser tab now has a real library and plays from it. The first end-to-end run against a real server and a real tab has now happened — see "The first browser hand-run" above. Album art and playlists followed it (see those two sections). Letting a tab write followed (see "A tab that writes" above), so the browser head is no longer read-only. Browse/search/queue was then checked end to end in a real tab and found already working, bar the now-playing album art (see "Browsing a tab like a jukebox" above). A tab's play counts and last-played now reach the server, and come back out in its manifest, through `IPlayReporter` (see "A tab that listens" above) — which leaves ratings, and only because no rating exists to report yet.
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
a URL handed directly to the decoder via `OpenSubsonicClient.BuildUrl`) over
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

**Also done in this pass** (all of it on the app's own `SyncHttpServer`, since
removed - what survives is what `Flower.Server` had already adopted alongside
it, noted per item): a declarative route table
(`Route`/`AuthMode`/`RateLimitCategory`) replaced the old `if`/`else if`
dispatch chain; rate limiting (`RateLimiter`, fixed-window, IP-keyed for
pre-trust endpoints, fingerprint-keyed post-trust); LAN-only enforcement
(`LanGuard`, hard reject on any non-private/loopback `RemoteEndPoint` -
closes `docs/todo.txt`'s "only stream on local network," since the wildcard
`http://+:{port}/` bind has no other network-layer boundary); persisted
pairing denials (`TrustedPeerStore.DenyAsync`/`denied-peers.json`,
surfaced in `TrustedDevicesView` with a "Forget refusal" action - removed
in October 2026, since nothing has denied anything since the live approval
prompt went with peer-to-peer); a
server-initiated unpair notification (`POST /api/flower/v1/unpair-notify`,
`PeerUnpairNotifier`, fire-and-forget) so a revoked peer can learn about it
proactively instead of only via a later 403/poll; and a 20 MB request-body
cap (`RequestBodyReader.ReadWithCapAsync`).

### 403 means revoked; a bad signature is 401

Both peer servers of the day (the app's `SyncHttpServer`, since removed, and
`Flower.Server`'s `SyncEndpoints`) answered *every* trusted-peer check failure
with a 403, and
`LibrarySyncService`/`PlaylistSyncService` read a 403 off a sync route as
"this server has revoked me" and responded by unpairing this device for
good. Those two facts together lost a real pairing: the client signed a
`GET /playlists` at 05:12:07, the machine suspended with the request in
flight, the bytes landed at 05:28:59 - seventeen minutes past
`SignatureVerifier.ClockSkewWindow` - and the still-perfectly-trusted peer
was dropped, needing a fresh pairing code to get back.

So the two answers are now kept apart at the source, in
`PeerSignatureAuth.AuthenticateTrustedPeer` (`PeerAuthFailure`):
**`NotTrusted`** - no key on file for the claimed fingerprint - is a durable
statement about the caller and is the only one reported as **403**;
**`BadSignature`** - a key *is* on file, but this request's signature was
missing, malformed, stale or replayed - is a **401** and means nothing
beyond "this attempt failed, try again". Only 403 may unpair anything.

**Explicitly still deferred**: TLS/transport encryption. `HttpListener`'s
HTTPS support off Windows remains a long-standing gap
(`dotnet/runtime#19752`); the signing scheme above closes the impersonation
hole without it, but traffic (including the signed headers themselves) is
still plain HTTP - eavesdropping on the same LAN is an accepted residual
risk, same "same-LAN threat model" framing as before, revisit if sync ever
needs to leave a trusted LAN.

## Ingest: an owner's device keeps the server's files in step — done

Until October 2026 sync moved metadata up and files down. A song that existed on one device only was a song no other device could play, and the only way music reached the server was somebody copying it onto the server's disk. What closes that is all for a device the server made an **admin** and nobody else — changing what files a server has is an owner's act, and every route involved is `/api/admin`. `LibraryMirrorService` is the client half; `LibraryIngest` (`Flower.Core`, so `SimulatedFlowerServer` answers through the server's own code) is the server's.

**New songs go up, to the same place.** A file of the device's own that the server lacks is uploaded to the same path below the server's first library folder that it has below the device's — `TrackDto.RelativePath` in the other direction. That makes this the one place a *path* arrives from the wire, where every other route was careful to take catalog ids only, so the path is taken apart and rebuilt: no `.`/`..` segment, no hidden folder (a scan would walk past it), only a type the importer takes, and the result must still be inside the folder. A name taken by a different file in the library is a 409, and the device stops asking about that file.

- **In pieces, because of the trust boundary rather than despite it.** A signed request is buffered whole before its signature is checked, and a lossless track is larger than anything this server buffers. Streaming a body would have needed a second, weaker way through the gate; 4 MB pieces are ordinary signed requests. They are staged under the data directory, keyed by the file's own SHA-256, so an upload cut off by a phone going to sleep resumes, a half-sent file never sits where a scan looks, and the assembled file is held to its hash before it is moved into place.
- **"The server lacks it" has to be current.** A file with no origin is either one the server does not have or one nobody has compared with the server's catalog yet, and they look identical. `LibrarySyncService` forgets its `If-None-Match` tokens whenever the library changes outside its own merge, so a rescan's new files are matched (`SyncedTrackMatcher`) by a real pull before any is offered, and an upload round stops if the library moves under it. The server refuses uploads and moves while it is itself mid-scan, for the mirror-image reason.
- **Never offered:** a file downloaded from the server (`IsLocallyDownloaded` — the server's song, lent), and a file the server once listed and no longer does without saying why (`Track.WithdrawnByOrigin`) — a server whose music folder failed to mount must not be answered with an upload of everything.
- **Its own rate budget** on the admin surface (3000/min against 120), for the reason art and playback each have one — and before buffering a body on that plane the gate checks that the *claimed* fingerprint is an admin's, so megabytes are only ever held for a caller that at least knows which devices administer the server. The signature still decides.
- **Only on the server's own network, unless told otherwise.** `AppSettings.UploadWhenAwayFromHome`, off by default: reached over a tailnet or a public address (`NetworkDiscoveryService.ReachRank`), a device holds its files until it is home. Deletions and moves are a few hundred bytes and go over anything.

**Changed files go up again.** Tags live in the file, so the way a tag edit or new artwork reaches the server — and through it every other device — is the file. `Track.OriginFileStamp` is what the device's file looked like (length, modification time) when the server last had the same bytes; one that no longer matches is re-sent with `ReplacesTrackId`, and the server swaps it in under the same song: old file moved aside, new one read back by the importer, old one let go only then. A song both ends held before they met is stamped on first sight and *not* sent — the baseline is "the same", so pairing never re-uploads a library on the strength of old modification times. An identical file costs one request: the server's checksum answers "already have exactly this".

**And then every other copy is replaced.** A replacement is dated on the server (`Track.FileReplacedAt`, the third date of the kind after tags and artwork) and the date served with the catalog. A device whose copy of the song — a download, or a file of its own; an owner's device or a guest's — is of an older version downloads the file again and puts it where the copy was (`LibrarySyncService.ApplyNewerFilesAsync`), then reads it back as a scan would, so the song keeps its id and everything known about it and takes the new file's tags, length and format. The date is handed back to the device that sent the file, which therefore does not fetch its own upload. Files are megabytes, so this keeps the uploads' rule — the server's own network unless `UploadWhenAwayFromHome` — and what is held back is remembered across syncs, because the merge that found it will not run again while the catalog is unchanged. A copy with a change of its own still waiting to go up is not overwritten: it is itself the newer version.

**A tag edit travels as a tag edit — device, server, every other device.** The paragraph above is for a file changed by anything; an edit made *in Flower* does not wait for it. `Library.NotifyTracksChanged` stamps `Track.TagsEditedAt` on the songs Track Info edited; `LibraryMirrorService.PushTagEditsAsync` sends the server the values (`POST /api/admin/library/tags`, a `TrackTagsDto` per song — a few hundred bytes, so it goes over any connection, which is what makes an edit on a phone on a train work at all); `TrackTags.ApplyEdits` writes them into the server's file first and its library second. The catalog then carries `TagsEditedAt` and, for edited songs only, the whole tag set, and every device compares the date with its own:

- **The newest edit wins**, whichever device made it, and "never edited" is older than any edit — so a song nobody has touched keeps whatever each copy's file says, and pairing overwrites nothing.
- **It works for a song the device has no file of.** Track Info used to accept an edit to a placeholder and silently drop it. It now takes one wherever there is somewhere for it to live: a file here, or the paired server when this device administers it and can reach it (`MainViewModel.CanEditTagsOf`). A phone's library is mostly such songs.
- **Sideways means into the files.** A placeholder is refreshed as before. A device with a *file* of the song — a download, or a copy of its own, an owner's device or a guest's — has the tags written into that file (`LibrarySyncService.ApplyNewerTags`), file first for the reason the server does it that way: the file is what the next scan reads.
- **Editing the server's songs is an owner's act** (`SyncRolePolicy.MayEditSong`). The library is the server's and what a device holds of it is a copy, so an edit to one of its songs is an edit for everybody — an administrator's to make, whether or not the device has a file of the song. A guest edits only songs that are purely its own, which go nowhere, and both Track Info screens show a server's song's tags without offering to change them. Adminness is remembered (`AppSettings.PairedServerGrantsAdmin`) so the answer is the same on a plane. An owner's edit reaches a guest like anyone else.
- **An edit is not a new version.** Editing a tag changes the file, and a changed file is otherwise something to upload — which would now make every device download the whole file for one corrected title. So once the server has taken the edit as values, the editing device re-takes its file's stamp (`LibraryMirrorService.AcceptAsSent`), and a device that writes received tags into its own copy does the same. Only a change made outside Flower goes up as a file.
- **No echo.** Writing tags into an owner's own copy changes the file, and a changed file is otherwise something to upload; the stamp is re-taken with the write. A replacement upload whose tags differ is dated by the server and the date handed back, so the sender does not receive its own edit as news. `LibraryMirrorScenarioTests` holds both down by counting requests after things settle.

**Artwork travels the same way, with the picture fetched rather than carried.** `Track.ArtEditedAt` is `TagsEditedAt`'s twin: stamped by `NotifyTracksChanged(Artwork)`, sent up by `PushArtEditsAsync` with the picture read out of the device's own file (`PUT`/`DELETE /api/admin/library/artwork`, one request per distinct picture naming every song that now carries it — an album's cover is one image and a dozen files), served as a date in the catalog. A device whose copy is older fetches the picture from the ordinary cover-art route and writes it into its file; one that only streams the song drops its cached cover. The older album-wide `PUT /api/admin/cover-art`, which is what changing the cover of a merely-streamed song uses, dates the files it writes too — until it did, a new cover reached whoever streamed the album and nobody who had downloaded it. A replacement upload whose embedded picture differs is dated the same way.

**Moves follow.** `Library.UpdateTracks` already recognised a moved file as the same song; it now records where it was (`Track.MovedFromPath`), and the device asks the server to move its copy to the same relative path (`POST /api/admin/library/move`). The record is of a *change*, which is the point: two machines that were each handed an album long ago may file it differently, and comparing layouts would have every admin device reorganising the server to its own taste, and two of them fighting about it. A renamed music folder, or iOS handing the app a new container, changes every path and no relative one, and is not a move.

And then sideways: a pull that finds the server's relative path for a song changed since the last one hands the song back (`SyncedFileWork.Moved`), and `LibrarySyncService.ApplyMoves` moves this device's copy the same way — when that copy sat at the same relative place the server's did, which is true of every download and of a library that mirrors the server's layout. A copy filed differently stays where its owner put it; "the same move" means nothing from a different start. Following a move is not making one: the track's path is updated directly, so no `MovedFromPath` is recorded and nothing is told back.

**Deletions are removals, and removals are not deletions.** A file that goes from an admin device's disk leaves a record marked as owed to the origin; ahead of each pull, `ReportVanishedAsync` turns those into `POST /api/admin/library/remove` *without* the files. The server takes the song out of its library and leaves the file on its disk, set aside from scans (the existing exclusion list). Nothing a sync does deletes a file anyone owns. The deleting is manual and separate: **Removed Songs → Delete Files…**, in the server's settings and each desktop's, backed by `LibraryRemoval.DeleteRemovedFiles` and `POST /api/admin/library/removed/delete`, which touch only paths already on that list.

- **Removed is a statement, not an inference.** The catalog carries `Removed` — the ids of songs taken out on purpose (`RemovedTrack.Deliberate`). An admin device drops its *own* copy of a song named there: out of its library, file set aside like the server's. A song that is merely absent — a music folder that did not mount lists nothing at all — costs no device anything; its copy stays and stops being vouched for. A listener's own file stays either way, and a *downloaded* copy of a removed song is deleted on any device, since it was only ever the server's song on loan (and a phone has no Removed Songs screen to clean it up from).
- **A scan not finding a file is weak evidence**, and what it is weak evidence *of* is usually not a deletion: a drive that is not plugged in, a share that did not mount, a folder taken out of the settings. Acted on, any of those would empty the server's library and every device's with it. So a missing file is reported only when its library folder is still there and still has something in it. Anything else is left alone, and the songs come back from the next pull as the server's placeholders *under the ids they had* — an unplugged drive turns a library of files into a library of streams, and every playlist is none the wiser.
- **A file set aside gives way.** Uploading a different file where a removed one is still sitting replaces it (to the trash where there is one): it was waiting to be cleaned up, not being kept.

**A removed song is kept on record.** `RemovedTrack` (table `removed_tracks`, a JSON row per song): written by `Library.RemoveTracks`, by a scan that stops finding a file, and by a pull that names a song as removed — on both hosts — and claimed by a returning file, first by the path it had, then by what it is (`SyncKey`, never for an untitled track). What comes back is `CarryForwardMutableState`'s list minus the sync origin: the id, Date Added, plays, star, options. Kept indefinitely, whether or not the file was deleted; "re-added later" has no deadline. When a song returns to the server's catalog, a device that had set its own copy aside takes it off the list again, and its next scan makes the placeholder a local file under the id it always had.

This fixed something worse than the feature it was built for. A server whose music folder was missing for one scan used to come back with every song added today, played never, and under an id no client had heard of.

**Date Added is the oldest any admin device knows.** It rides in `TrackStateDto` as owner state and the server only moves it backwards (`Library.ApplyReportedOwnerState`); an admin device's pull keeps the older of its own and the server's (`MergeSyncedTracks`' `ownersDevice`), where every other device still mirrors the server. A low-water mark, the way `LastPlayedAt` is a high-water one — and the rule `ITunesDateAddedImporter` already applied to Music.app's record. No baseline is needed, unlike the star: two dates have an order.

**Not done, deliberately or not yet:**

- A removal still treats a guest's own file differently from an edit: an owner's edits reach it, an owner's removal does not take it out of the guest's library.
- **The browser head cannot edit tags yet — wanted, not built.** Every song in a tab is the server's, and the tab has no mirror service to send an edit with; it needs Track Info's save to call `POST /api/admin/library/tags` directly, the way its artwork change already calls the album-wide cover route. The server half is done.
- An *untitled* file that is moved is not recognised as the same song (its key is its length and nothing else), so it goes as a removal and an upload, and loses its plays.
- A device that has never synced a copy cannot be told from one that was just given the song: a second admin device holding a file the first one deleted *before the second ever synced it* will upload it, and the server will take it back as the song it was.
- Phones have no Removed Songs screen. A phone's *own* imported file that is set aside stays on its disk with no way to delete or restore it from the phone.
- The clean-up is all-or-nothing per machine, and it deletes rather than trashes.
- "Not on the server's network" is judged by the address the server is reached at, not by whether the link is metered: a phone on home Wi-Fi reaching the server through its public address counts as away.

## Music.app history from an admin device — done (October 2026)

**The problem.** A device paired to a server never reads Music.app. The rule is in three places, all saying the same thing: `App.axaml.cs`'s startup rescan (`importsFromMusicApp` requires no paired server), `LocalSettingsBackend.SaveAsync`, and Settings, where `SettingsViewModel.CanManageLibrary` greys both switches. The reason given is that "its library is the server's, which runs these imports itself". That holds only for a server running on the Mac that has the Music.app library. The deployment this project actually has is the other kind: `Flower.Server` in Docker on a Linux box (lambda), with its music on a NAS. It has no Music.app and never will. So the owner's Mac, which has fifteen years of play counts and Date Added in Music.app, is barred from reading them, and the server cannot. The history reaches nobody, and the switches are greyed with no reason shown (the panel explains a greyed switch only when no Music.app library is found).

**Most of the road is already built.** That is why this plan is small:

- **The importers match by what a song is, not where it is.** `ITunesPlayCountImporter` and `ITunesDateAddedImporter` try the file path first, then `Track.SyncKey` (title, artist, album, rounded duration), then the same key without the duration when only one candidate has it. A song this Mac only streams has no path but has everything else. Measured against this owner's real data (the August export in the app's data folder and `flower.db`, read-only): 15,009 of 16,114 songs match exactly, and 437 more by title, artist and album alone, which is 96%.
- **Play counts already travelled as this device's own tally.** `UnreportedTrackState` sent `PlayCount + ImportedPlayCount` for every song with an `OriginTrackId`, and the server filed it under the reporting device's fingerprint in `RemotePlayCounts`, a max-merged G-counter (see "A paired device that listens, stars and configures"). That turned out to be the wrong shape for an imported count; see "Music.app's count travels on its own" below.
- **Date Added already travels as owner state.** An admin device's `DateAdded` rides in `TrackStateDto`, and the server only moves it backwards (`Library.ApplyReportedOwnerState`), which is the same oldest-wins rule `ITunesDateAddedImporter` applies locally. An admin device's pull keeps the older of its own and the server's.
- **An import is already pushed.** Both importers end in one `Library.NotifyLibraryChanged`, and `PeerSyncCoordinator` answers any library-wide change with `MarkAllTrackStateChanged`, so the next five-second tick restates every song. The baseline for owner state needs a pull this session, which startup does before the import runs.

So the change is the gate, not the plumbing.

### Steps

1. **One rule for who may import, in one place.** Something like `SyncRolePolicy.MayImportFromMusicApp(settings)`: true for an unpaired device and for a device paired as an admin (`AppSettings.PairedServerGrantsAdmin`). The three sites above call it instead of each testing `PairedServerFingerprint` themselves. *Adopting the Music.app folder* stays barred on a paired device. Taking a folder into the library is managing it, and this Mac's library is the server's. Only the two per-song imports open up. In Settings that means splitting `CanSyncFromITunes` off `CanManageLibrary`.

2. **Never count the same Music.app plays twice.** If the server itself imports play counts from a Music.app library, that is, if it runs on the Mac that holds the library, the same plays would arrive once as the server's own figures and again as the device's tally. The server's admin settings already say whether it does: `IntegrateWithITunes`, `SyncPlayCountFromITunes` and `AppleMusicFolder` are all in the snapshot `GET /api/admin/settings` returns. The device reads them when it syncs, remembers the answer (`AppSettings`, like `PairedServerGrantsAdmin`, so it is the same answer offline), and skips the *play count* import while the server does its own. Date Added needs no such guard: two oldest-wins sources of one date converge.

3. **Settings says what the switches do now, and why they are off when they are.** On a paired admin Mac the two switches are live, worded for where the data goes: play counts "add this Mac's Music.app plays to its plays on Flower", Date Added "dates songs on Flower by when Music.app first had them". When they are disabled, the line under them gives the reason, which is the gap that prompted this plan. The reasons are: a guest device ("Only an administrator's device can bring Music.app history to Flower"), a server that imports its own ("Flower reads play counts from its own Music.app library"), and no library found (the existing message). Turning a switch on runs the import at once, as it does on an unpaired Mac.

4. **Prove the counts survive a pull and a download.** `Library` carries `ImportedPlayCount` forward across a placeholder being re-merged and across a placeholder becoming a downloaded file (`Library.cs`'s carry-forward sites). Each of those gets a test, because a pull that reset the field would make the device's tally drop, and a max-merge on the server hides that only until the device's real plays move past it.

5. **Update the record.** `CLAUDE.md`'s "A first desktop launch…" paragraph says neither import runs on a paired device, and the comments at the three gate sites say why. All of them change with step 1.

### Tests

- The rule: unpaired → yes; paired admin → yes; paired guest → no; folder adoption on any paired device → no.
- Startup on a paired admin device with both switches on runs both imports, and the next track-state push carries the imported totals and the older Date Added (`MainViewModelHarness` with a `SimulatedFlowerServer`, and a synthetic Music.app XML via `ApplyFromXmlFile`).
- A server whose settings say it imports play counts itself: the device skips its play count import and still runs Date Added.
- `ImportedPlayCount` on a placeholder survives a catalog pull and survives the song being downloaded.
- Settings: the switch states and the reason line for each of the four cases above.

**Done.** Where it differed from the steps below:

- **The device learns whether the server imports from `/info`, not from the admin settings.** `importsMusicAppPlays` rides the answer a paired device already polls every five seconds, told to verified administrators only (`DiscoveryEndpoints.ImportsMusicAppPlays`: both switches on and a Music.app library found, the switches checked first so a container never looks). The client remembers it as `AppSettings.PairedServerImportsMusicAppPlays` beside `PairedServerGrantsAdmin`, and forgets both on unpairing. Reading `GET /api/admin/settings` would have been a second request for one bit, and the catalog could not carry it: the catalog is cached against the library's change token, and flipping a server switch does not move that.
- **The rule is `SyncRolePolicy.MayImportFromMusicApp` and `MayImportPlayCountsFromMusicApp`**, called from the startup rescan, `LocalSettingsBackend.SaveAsync`, and through `MainViewModel` for Settings. `SettingsViewModel.CanSyncFromITunes` (Date Added) and `CanSyncPlayCountFromITunes` now say who may import; `CanIntegrateWithITunes`, taking the folder in, is unchanged.
- **The Library tab is no longer disabled as a whole on a paired device.** It was one `IsEnabled` on the grid, which no child can override, so the folder rows below the Music.app ones carry it each instead. The note under the switches (`ITunesImportNote`) says where the history goes, or why the switches are off.
- **Step 4 found nothing to fix.** A placeholder's `ImportedPlayCount` already survives a pull and a placeholder becoming a file; `SyncScenarioTests` and `LibraryTests` now hold both down, the first end to end from a synthetic Music.app export to the server's counts and dates.

### Decided

- **Admin devices only.** A guest's Music.app plays would legitimately be the guest's, filed under its own fingerprint, but its Date Added is owner state the server drops, so the switch would half work. Revisit if a guest ever asks.
- **Two admin Macs with one Music.app history count it once** — decided first the other way and then reversed, see the next section.
- **No undo on the server.** A count reported to a G-counter cannot be taken back; turning the switch off afterwards stops further imports but leaves what was sent. The same is true of every play today, and the imported number only ever restates Music.app's own.

### Music.app's count travels on its own — done (October 2026)

The first version sent `PlayCount + ImportedPlayCount` as the device's tally, as the code always had, and told the owner to switch play counts on in one Mac only, because two Macs sharing one Music.app history (iCloud's Sync Library) would each report the same count and the server would sum them. That was reversed the same week: it asked the owner to remember a rule the software could keep.

- **Two numbers on the wire.** `TrackStateDto.Count` is plays counted in Flower on that device, unchanged in its rules. `ImportedCount` is Music.app's count as read on that device, and is owner state: admin devices only, dropped from anybody else.
- **One number in the library.** The server raises `Track.ImportedPlayCount` to the highest any admin device reports and never lowers it (`Library.ApplyReportedOwnerState`). Highest rather than summed is right for Macs sharing one history and undercounts two Macs with separate libraries — the rarer case for one owner, and the one a library ID in Music.app's export could tell apart if it ever matters (unverified whether Macs syncing through iCloud report the same one).
- **Served apart, so nobody counts it twice.** The catalog's `PlayCounts` used to give the server's own tally as `PlayCount + ImportedPlayCount`. With the imported count now coming *from* a device, folding it back in would hand that device its own Music.app plays as the server's, on top of its local copy. So `PlayCounts` carries Flower plays only and `TrackDto.ImportedPlayCount` carries the library's count, which a pull takes as the higher of it and the device's own (`MergeSyncedTracks`). Every device, a guest's included, ends up showing the same total.
- **No migration.** Checked against the one real deployment before changing the wire: the owner's Mac had no imported counts stored, so nothing the server held mixed the two.

The Settings note now says where the history goes and that the server keeps the highest count across Macs.

**What this does not do:** it does not add songs. A Music.app song the server lacks is the Ingest section's business (an admin device uploads its own files), and this Mac has no library folders of its own (`LibraryPaths` is empty here), so it has nothing to upload. The unmatched songs are songs Music.app has and Flower's catalog does not, or that are tagged differently in the two. This section used to put them at 668, a figure no log supports: the import logs 15,954 of 16,293 played tracks matched. Counted properly in the next section, nearly all of them are Apple Music streams with no file to send. The next section is what closes the rest.

## A paired device has two sources of files, and no folders — steps 1–3 done (October 2026)

**The problem.** A paired device cannot add library folders (`SettingsViewModel.CanManageLibrary`), but whatever folders it had before pairing go on being scanned and mirrored up, so the server's layout is whatever those folders happened to be. Meanwhile the one folder on a Mac where new music actually arrives — Music.app's — is the one a paired device is barred from taking in (`SyncRolePolicy`: "taking the Music.app folder into the library is managing the library, which a paired device does not do"). So the owner's Mac cannot bring a newly bought album to the server at all, and the only route left is copying files onto the NAS by hand. And downloads land at the root of `~/Music` (`LibraryDownloadService.ResolveDownloadFolder` is `SpecialFolder.MyMusic`), beside Music.app's own library and anything else kept there: on the owner's Mac, `~/Music/Sonic Youth/` and `~/Music/Emily A. Sprague/` sit next to `~/Music/Music/` and `~/Music/iTunes/`.

**The design.** On a device paired to a server, files come from exactly two places, and neither is a folder the user picks:

- **Music.app's library**, read and mirrored up — an administrator's device only, behind a switch beside the two history imports ("Add songs from Music.app to Flower"). It reverses the line in `SyncRolePolicy` quoted above, deliberately: Music.app is not a folder anyone curates for Flower, it is where music comes in on a Mac, and an owner whose library lives on a NAS has no other way to get a new album there.
- **Flower's own folder** — `~/Music/Flower` on a desktop, the existing app-private folders on the phones — which holds downloads *and* imports. A song dropped on the window or imported on a paired device is copied there under `<album artist>/<album>/` (`LibraryDrop`'s layout), not into Music.app's folder even when that one is synced.

An unpaired device is unchanged: library folders, `LibraryDrop` copying into the first of them.

**The Music.app source adds and changes; it never removes.** Music.app deletes local files on its own — Optimize Storage, Remove Download on an iCloud-matched song — so a file gone from its folder says nothing about whether the owner wants the song. Ingest's guard against a false removal (the library folder must still be there and not empty) does not help here: the folder is there and full. So from this source a new file goes up, a changed file goes up again, a moved file is moved, and a vanished file is dropped from this device's library and from nothing else. Taking a song off the server is something done in Flower, never inferred from a file.

**Only the music in it.** The source is `<media folder>/Music` (on the owner's Mac, `~/Music/Music/Media.localized/Music`), not the media folder Music.app reports. That leaves out `Automatically Add to Music.localized` — whose files Music.app moves on as it takes them in, so uploading from there would send every song twice — along with Podcasts, Movies, TV Shows and anything else Music.app keeps beside the music. Protected `.m4p` files are already outside the importer's extensions; a test holds both down with a synthetic media folder.

**An import becomes a download once the server has it.** Until the upload is confirmed the file is the device's own, offered up like any other. Once the server holds it (`Library.RecordOrigin`), it is marked `IsLocallyDownloaded`, and from then on it is indistinguishable from a song downloaded from the server: the server's song, lent. Deleting it frees space and removes the song for nobody. The folder therefore never holds a lasting mix of "mine" and "lent" that a flag has to keep apart, and it inherits downloads' "never rescanned, carried forward" rule unchanged — the only files it holds that a carry-forward does not cover are imports still waiting to go up. A guest cannot upload, so a guest's imports stay its own and go nowhere, which is fine.

**Flower's folder is Flower's.** Creating it writes a marker file (`.flower`). A `~/Music/Flower` that already exists with content and no marker is someone else's folder: the device says so once and asks before taking it over, since Flower adds to it, moves within it and deletes downloads from it. Independently of that question, a download or import never overwrites a file the library does not know: `ResolveDestination` checks only the library's own paths today, and should also fall back to the id-named path when something unknown is already on disk there.

**What this Mac already has moves.** The downloads already at the root of `~/Music` are moved into `~/Music/Flower` at the same relative paths, once — the library knows every one (`IsLocallyDownloaded` tracks' `Path`), so this is a move of known files, not a scan, and nothing unknown in `~/Music` is touched. No compatibility promise asks for it, but the files are the owner's and re-downloading them is gigabytes.

### Renames: Flower never renames a file because its tags changed

A file's path is where it was put; its tags are what it is. Flower does not rename a file when Track Info edits its artist or album — not on the server, not on a device. The server's layout is the owner's (on a NAS other tools may read), a rename on the server becomes a move on every device holding a copy, and renaming on edit turns every typo fix into a cascade of moves. The catalog shows tags, so a song whose folder name is out of date looks right everywhere it is played.

**TODO, later: an opt-in naming scheme.** A server setting, off by default, that names every file from its tags by a pattern (something like `{album artist}/{album}/{disc}-{track} {title}`), so that folders and filenames are uniform across the whole library. With it on, the rule above inverts: a tag edit renames the file on the server, devices follow through the existing sideways move (`ApplyMoves`), and an upload lands at the name the pattern gives rather than the path it had on the device. It belongs to the server, because the layout is the server's and two devices with different patterns would fight over it. Switching it on is a one-time rename of the whole library, which wants a preview of what moves first. Off, nothing changes from what this section says.

Music.app does rename, though: with "Keep Music Media folder organised" on, editing an album in Music.app moves its files. Two things follow:

- **A rename that also changes the tags is not recognised today.** `Library.UpdateTracks` finds a moved file by `SyncKey`, which is title, artist, album and duration — exactly what the edit changed. The old path reads as vanished (harmless, under add-only) and the new one as a new song, which goes up as a duplicate. The fix is to recognise a moved file by what a tag edit and a rename both leave alone: its audio. When a scan has files that vanished and new files it cannot place by `SyncKey`, each vanished one is paired with the new ones of the same duration, and a pair whose decoded PCM hashes identically is the same file — a move and a tag edit, sent up as both. The hash is kept on the track once taken, so the vanished side costs nothing to compare.
  - **Exact, not perceptual.** The question is "is this the same file with different tags", not "is this the same recording", so an acoustic fingerprint (Chromaprint) would be the wrong tool: it would call a remaster, a re-encode or a clean edit the same song. Tags do not touch the audio and the decoder is deterministic, so the decoded samples of a retagged file are bit-identical. A radio edit is a different duration and is never even a candidate; a clean edit of the same length differs in its samples and fails the hash.
  - **The whole track, only for candidates.** A hash of the first seconds would pass a clean edit whose first bleep is a minute in. Decoding the whole file is a fraction of a second for a song, so it costs a few seconds when Music.app refiles an album — and it runs only for pairs a scan could not settle by key, which most scans have none of.
  - **Why not the file's identity on disk.** An inode (or NTFS's file id) survives a rename, but not a move to another volume, which is a copy into the other drive's file table and a delete; FAT/exFAT ids change on any move; and on SMB and NFS whether ids are stable is up to the server. The audio is the same wherever the file is, which matters most for the server's own folders, on a network share more often than not.
- **A move from Music.app moves the server's copy only when the two agreed on where it was** — the upward twin of `LibrarySyncService.ApplyMoves`' sideways rule. A song the server already had and filed its own way is not refiled to Music.app's taste because the owner retitled an album on the Mac. `LibraryMirrorService`'s move round has no such guard today; it moves the server's copy to the device's new relative path whatever the server's was.

### Replacing a song's file with a better one

The same song, a better file: an MP3 swapped for the FLAC, a 128 kbps rip for a 256 kbps one. The song keeps its id, Date Added, plays, star and every playlist it is in; the server's file is swapped for the new one; and every device holding a copy downloads the new version. Most of that road exists — `ReplacesTrackId`, `LibraryIngest.Replace`, `Track.FileReplacedAt`, `ApplyNewerFilesAsync` — but all of it assumes the replacement is the same *kind* of file, and an upgrade almost never is. Read from the code, not yet tried: a FLAC put where an MP3 was is seen by the device's scan as the MP3 moved (same tags, so same `SyncKey`); the server refuses the move ("A move cannot change what kind of file a song is"), and the device does not ask again; then the changed file goes up as a replacement, which `Replace` writes to the song's *current* path — FLAC bytes under `.mp3`, which the importer then either refuses or misreads.

**The extension is part of the replacement.** The server writes the new file at the old path with the new file's extension, sets the old one aside, and serves the new `OriginFileExtension` and `RelativePath` with the catalog; a device replacing its copy does the same, so a download never ends up as FLAC named `.mp3`. If the new name is taken by a different file, the replacement is refused like an upload's conflict.

**The old file is set aside, not deleted.** `Replace` deletes it today once the new one reads back. An upgrade that turns out to be a bad rip, the wrong master or a mislabelled file should be undoable, and "nothing a sync does deletes a file anyone owns" is the rule everywhere else. So the old file joins the server's set-aside files, listed under Removed Songs as replaced, and goes with the rest at *Delete Files…*.

**Two ways in, the explicit one first.**

- **Replace File…**, in Track Info and the context menu, on an administrator's device: pick a file, see the two side by side ("MP3 192 kbps → FLAC 16-bit/44.1 kHz", and the two durations), confirm. It works for a song the device only streams, because it uploads with `ReplacesTrackId` directly, and it needs no detection at all. A duration more than a few seconds off, or a new file that is worse, is said plainly before confirming, not refused — the owner may know something Flower does not.
- **Detected in a scan.** A vanished file and a new one with the same tags and a duration within a couple of seconds — not equal after rounding, because an MP3's duration and a FLAC's of the same master often round differently — is a replacement when each has exactly one candidate. The exact audio hash above cannot help: a re-encode is different samples by definition, and whether this is "the same recording" is the question an acoustic fingerprint answers. The heuristic goes without one, because a pair only forms when a file vanished and another appeared in the same scan, which is what replacing a file *is*; a radio edit is minutes off, and keeping both versions forms no pair. On the server's own scans this is partly there already: a removed song is reclaimed by `SyncKey` (`RemovedTrack`), subject to the same rounding.

**Never a downgrade by detection.** What is better is ordered simply: lossless over lossy; within lossless, bit depth then sample rate; within lossy, bitrate. A detected replacement that is worse is not sent up — an owner converting their Mac's music to AAC to save space must not cost the server its FLACs. The device keeps its smaller file, and the track records that it is a deliberate lesser copy, so it is not offered again on every round. Replace File… can still downgrade, after saying so.

**Every copy follows, a phone's too.** A phone that downloaded the MP3 downloads the FLAC, on the home network only (the existing rule for files), at three to five times the size. That is right for the song, and may not be right for the phone; a per-device "keep downloads small" preference, or transcoding on the server, is a separate feature and is not designed here.

**TODO, later: transcode lossless files for a device that asks.** A per-device option, with streaming and downloading set separately, that has the server send a lossless file as a lossy one (AAC or Opus, at a chosen bitrate) and pass lossy files through untouched. Questions to settle before building it:

- **The encoder goes into FFAudio.NET** (decided). It is a decoding façade today; encoding is added there, alongside the decoder, within the LGPL-only constraint its FFmpeg builds keep — not a second FFmpeg dependency in Flower. The server is the only caller, so it is needed on the platforms a server runs on first.
- **Length and seeking** — left until it is built. Every stream reaches its length through a ranged GET and seeks with ranges (`SeekableHttpStream`), and a transcode produced on the fly has neither; transcoding to a cached file on the server first is one answer, not yet chosen.
- **A transcoded download is never uploaded** (decided), and lives apart from real downloads: in a cache folder of its own rather than Flower's folder, so it is never mistaken for a file of the song, never offered up as the device's own or as a replacement, and can be cleared as a cache. A newer version of the song (`FileReplacedAt`) or a tag edit reaches it as a new transcode.

### Folders a device had before pairing

Under this design a paired device has no library folders, and one that pairs with some in `LibraryPaths` needs an answer for them. Not scanning them is the clean one, but a scan that stops visiting a folder makes every file in it read as gone, which is exactly the case Ingest's removal guard and `RemovedTrack` exist for, and it would turn local files into streams. The proposal: a folder kept from before pairing is treated like the Music.app source — added to and changed, never removed from — and Settings lets it be taken off the list but not put back. That makes it one concept rather than three. The owner's Mac has no such folders, so this is for a second device, and it is fine to decide it when that device exists.

### Steps

1. **`~/Music/Flower` — done.** `FlowerFolder` owns it: `ClaimAsync` creates and marks it (`.flower`), and a folder by that name that already holds something unmarked is asked about over the main window (`AskToTakeOver`, wired in `App.axaml.cs`). A yes marks it; a no puts Flower's folder beside it as `Flower 2` and remembers that in `AppSettings.FlowerFolderPath`, so the question is asked once. With nobody to ask, the folder is left alone and the download fails rather than write into it. `LibraryDownloadService` downloads through it, and `ResolveDestination` falls back to the id-named path when any file at all is already at the destination. `MoveOldDownloadsAsync` runs before the startup scan and moves every `IsLocallyDownloaded` file under `~/Music` but outside the folder to the same relative path inside it. `FlowerFolder.Override` replaced `LibraryDownloadService.DownloadFolderOverride`, and the test suite sets it for the whole run (`AssemblySetup`).
2. **Imports on a paired device go there — done.** `MainViewModel.AddDroppedSongsAsync` claims Flower's folder on a paired device and hands it to `LibraryDrop` as the folder to copy into. The phones needed nothing: iOS's Documents folder was already both the download folder and the scan root, and Android's songs come from MediaStore, which is not a folder Flower owns (step 6's question, not this one). Lending is `LibraryMirrorService.LendImports` → `Library.LendFiles`, run at the start of every upload round and after each upload. It covers a song the server already had and a pull matched, as well as one this device uploaded: either way the server holds it, and leaving a merge-matched import as "this device's own" would have made deleting it a removal from the server.
3. **The Music.app source — done.** "Add songs from Music.app to Flower" in Settings > Library, shown on a paired Mac with a Music.app library and switchable on an administrator's (`SyncRolePolicy.MayAddSongsFromMusicApp`; `AppSettings.AddSongsFromMusicApp`, off by default). The scan root is `<media folder>/Music` (`DeviceLibraryFolders.MusicAppSource`), left out when a library folder from before pairing already covers it. Add-only is decided by where the file was rather than by a mark on the track: `ReportVanishedAsync` settles, and never reports, a removed song whose path is under `DeviceLibraryFolders.AddOnlyRoots` — Music.app's music, and Flower's own folder. The next pull brings the song back as a placeholder under its old id. `SyncRolePolicy`'s comment changed with it, and so did `CLAUDE.md`.

   One piece of wiring all three needed: the folders a device scans are no longer `AppSettings.LibraryPaths`, they are `DeviceLibraryFolders.ScanPaths`. The startup scan, rescans, the folder watcher, the mirror's relative paths and Removed Songs' clean-up all ask it. `MainViewModel.FollowScanFolders` rescans and re-watches when the answer changes: after pairing, unpairing, an admin grant changing (which now raises `PairingChanged`), the Music.app switch, and the first drop that creates Flower's folder.

   **Not done here, and worth knowing before switching it on:** step 4's guard. A Music.app source behaves like any library folder on a move, so Music.app refiling an album ("Keep Music Media folder organised") moves the server's copy to Music.app's new layout too. Turning the switch on also offers the server every Music.app song it lacks. On the owner's Mac that turned out to be almost nothing, checked on 2026-10-21 against Music.app's export and the Mac's catalog with the play-count import's own matching. 528 of 16,673 Music.app tracks match nothing in Flower. 524 of those are Apple Music subscription streams with no file on the Mac, one more is a remote entry, and one is a file in the Trash. That leaves two Music.app records, both for one file (`François Pérusse/2 Minutes Radio/Ambiance 02.mp3`). The server already holds this Mac's files. A song that only *looks* missing usually sits at a relative path the server already holds: the server refuses a different file there as a conflict, and takes identical bytes as the file it already has (`LibraryIngest`). Neither way does it end up with a second copy.
4. **Moves by audio.** A decoded-PCM hash on the track, taken lazily, consulted by `Library.UpdateTracks` for vanished/new pairs of the same duration that `SyncKey` could not match — on the server's scans too, not only the Music.app source; the upward move guarded on the two relative paths having agreed.
5. **Replacing with a better file.** The extension change on the server and in `ApplyNewerFilesAsync`; the old file set aside instead of deleted; Replace File… on both heads; then scan detection with a duration tolerance and the no-downgrade rule.
6. **Pre-pairing folders**, as decided above, when there is a device with any.

### Tests

- A file vanishing from the Music.app source drops it from this device's library and leaves the server's song and every other device's copy alone.
- A file in `Automatically Add to Music.localized`, a Podcasts file and an `.m4p` are never scanned or offered.
- An import on a paired device lands in Flower's folder under `<album artist>/<album>/`, uploads, then reads as a download; deleting it afterwards sends no removal.
- A guest's import stays local and is never offered.
- A download whose destination is occupied by an unknown file takes the id-named fallback; an existing unmarked `~/Music/Flower` with content is not written to before the question is answered.
- Music.app renaming a file and changing its album in one go is recognised as a move plus a tag edit, not a new song; the server's copy moves only when its relative path was the Mac's old one.
- A vanished file and a new one of the same duration whose samples differ late in the track (synthetic WAVs, identical but for a stretch near the end) are not paired: the new one is a new song.
- An MP3 replaced by a FLAC of the same song, by Replace File… and by a scan: the song keeps its id and plays, the server holds the FLAC under the old name with `.flac` and the MP3 set aside, and a second device's download is replaced by the FLAC under the new extension.
- A detected FLAC → AAC replacement is not sent up and not offered again; the server keeps the FLAC.

## Status summary

All numbered steps through Phase 4 are **done**: the private-file-library iOS design settled (see "Architecture prerequisite" above); WiFi/LAN discovery + LocalSend-style transfer; `UIFileSharingEnabled` for USB; Bluetooth/programmatic-USB deliberately not built; playlist metadata sync; the OpenSubsonic client; the full Phase 3 stack (trust gate, embedded host, merge logic, mobile download UI); and Phase 4's cryptographic identity/signed-request hardening (route table, rate limiting, LAN-only enforcement, persisted denials, server-initiated unpair, body size cap). `Flower.Server` build-order steps 1 (extracting `Flower.Core`), 2 (scaffolding `Flower.Server` itself - SQLite schema, since moved onto `Flower.Core`'s shared layer, importer wiring, the full OpenSubsonic endpoint set, verified end-to-end against a real `OpenSubsonicClient`), and 3 (admin-issued pairing-code endpoint, admin bearer-token auth, `LanGuard` CGNAT allowance + configurable extra CIDRs, rate limiting on the redeem route) are also **done** (see "Project structure", "`Flower.Server` v1", and "Pairing-code endpoint, admin auth, `LanGuard`" above).

Build order step 4's first two parts - `Flower.Web` scaffolding (existing Views/ViewModels rendering in-browser) and real audio playback (`WebAudioManager`) - are also **done** (see "`Flower.Web` scaffolding — rendering milestone done" and "`WebAudioManager` — real browser playback, done" above). Two real architectural findings along the way: .NET-for-WASM has no asymmetric crypto support at all, so `MainViewModel`'s P2P sync dependencies (`DeviceSigningKey` and everything built on it) are now nullable/defaulted rather than hard requirements, gated on `OperatingSystem.IsBrowser()`; and the browser audio path couldn't reuse `IAudioSink`/`GaplessCoordinator` at all (native decode has no WASM build either), so it's a separate `IAudioManager` implementation driving a plain `<audio>` element instead.

Build order step 4 is now **done in full**: on top of scaffolding and playback, a browser tab has a real library (`RemoteLibraryImporter` over the bulk manifest, shared with the desktop), album art, playlists, stream-ticket playback, browse/search/queue, the ability to write its playlist edits back, and — last — its own listening reaching the server through `IPlayReporter` and coming back out in the manifest. See the per-feature sections above.

**Remaining work:**

- **"A paired device has two sources of files, and no folders"** (above) — steps 1–3 done, 4–6 open: moves recognised by their audio, replacing a song's file with a better one, and folders a device had before pairing. The two TODOs there (an opt-in naming scheme, transcoding for a device that asks) are later still.
- **Real-device verification** still owed: the Android download path, and end-to-end sync against a real second device rather than a test harness.
- **The browser's device key, in a real tab.** The server half is verified (see "The browser is a device, for real" below); the DOM half is not — a tab pairing from the fragment, its IndexedDB key surviving a reload, the settings overlay opening on `page=settings`.
- **Ratings.** Nothing on `Track` holds a rating and no UI sets one. Stars are the one judgement that exists, and they already travel: Track Info sets them and the library merge carries `Starred`/`StarredAt` both ways. A rating would take the same road if one is ever wanted.
- **Jellyfin as a second backend**, on the `IMusicImporter` seam — optional and unstarted (see "Optional, additive: Jellyfin client support").

Done since earlier revisions of this list, and dropped from it: **Docker packaging** (`docker/`: the image, published to GHCR on `v*` tags, with Caddy, cloudflared and non-Linux overrides; host networking on Linux so mDNS works, a bridge only where host networking binds to a VM); **a public domain without a VPN**, which went to Caddy in front of the server rather than LettuceEncrypt inside it, so the ACME challenge never meets `LanGuard`; and the **`IMusicImporter` settings choice**, which existed to fold the OpenSubsonic client in and went with it.

### The browser is a device, for real — done

The derived-authority design above always said it "does not close off giving the browser its
own key later; it removes the dependency on doing so first." `OPEN-INTERNET-REVIEW.md` finding
7 is what made later become now: a 60-minute full-admin bearer token in a URL is tolerable
while `LanGuard` makes it unusable from off the LAN, and a tunnel is precisely the thing that
stops making that true. Narrowing it was available and was the wrong move — it had already
widened once, and every narrowing was a smaller version of the same shape.

**The module.** `Flower.Web/wwwroot/webcrypto.js` generates an ECDSA P-256 keypair with
`extractable: false` and stores it in IndexedDB as a `CryptoKey`, through structured clone,
so it never exists in serializable form at all. The private half is a handle the page can sign
with and nothing — including that module — can read out, which is a *stronger* storage
guarantee than the file-backed key on the desktop. `BrowserSigningKey` is the `[JSImport]`
wrapper, same shape as `BrowserLocation`/`WebAudioManager`; `BrowserPeerCredentials` is the
`IPeerCredentials` that uses it.

**No bridging code, as the table above promised.** `exportKey("raw")` is `0x04 || X || Y`,
byte-for-byte `DeviceKeyStore.PublicKeyRaw`; a WebCrypto ECDSA signature is raw `r‖s`, which is
`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`. `SignatureVerifier` accepts a tab's
signature with no new code path, no second auth mode, and no server-side branch. Pinned by
`BrowserSignatureFormatTests`, a fixed vector generated by running the real module — the one
thing about this that no managed test could otherwise reach.

**Pairing is the same mechanism as everything else.** The tab redeems a single-use code at
`POST /api/flower/v1/pair-redeem` with a self-signed request, exactly as
`PeerPairingService.RedeemPairingCodeAsync` does. The code arrives in the URL fragment
(`#pair=<code>&page=settings`), is spent within a second of the page loading, and is worthless
afterwards. `page=settings` is separate from the code on purpose: an ordinary listener pairs a
tab to get a jukebox, not a settings screen. After that first visit the tab needs nothing in
its URL ever again — its key is the credential, so a returning tab gets its library from the
bare address. Per browser profile, because the key is: each browser is its own row in the
device list, revoked on its own, and clearing site data un-pairs it.

**Deleted, not deprecated** (`CLAUDE.md`, "No Users Yet"): `AdminSessionService`,
`AdminSessionCredentials`, `PeerOrSessionAuth`, `POST /api/admin/sessions`, the
`X-Flower-Admin-Session` header, and `RegisterBrowserServices`' fork on whether the page
arrived with a credential. There is no bearer credential left in the project. Seam 3's widening
is undone: `SyncEndpoints` and `StreamTicketEndpoints` gate on
`DeviceSignatureAuth.AuthenticateTrustedPeer` and nothing else.

**The cost, which seam 3 predicted exactly.** `crypto.subtle` is asynchronous, so
`IPeerCredentials.Authorize` became `AuthorizeAsync` and `AddPeerCredentials` became
`AddPeerCredentialsAsync` — twelve call sites, plus `OpenSubsonicClient.BuildUrl`,
`GetStreamUrl`, `GetDownloadUrl` and `GetCoverArtUrl`, which are now tasks. Every head but the
browser still completes them synchronously (`SignedDeviceCredentials` returns a finished task),
so `PlaylistControlViewModel.Play`'s synchronous-start path is unaffected.
`ICoverArtUrlResolver` stayed synchronous — it builds a URL and does not sign.

**The requirement, stated plainly: a secure context.** Browsers expose `crypto.subtle` only
over HTTPS and on `localhost`. A tab opened at `http://192.168.1.x:4533` cannot hold a key and
cannot pair; it logs an explicit error naming the requirement and shows an empty library. That
is a real regression for plain-HTTP LAN browsing and it is the browser's rule, not a choice
made here. It also points the same way everything else does: every remote transport under
consideration terminates TLS, and `OPEN-INTERNET-REVIEW.md` finding 6 already makes TLS a hard
gate on the mapped-port path.

**Stream tickets stay.** What cannot authenticate itself is the `<audio>` element, not the tab
that owns it — so the tab signs the mint request and the element plays on the ticket. One
track, fifteen minutes, attributed to the minting device, exactly as before.

**Verified** against a live server by driving the real `webcrypto.js`: key generated, printed
pairing code redeemed (200, `isAdmin: true`), then signed access to `GET /api/flower/v1/library`,
`GET /api/admin/settings` and `POST /api/flower/v1/stream-tickets`, all 200. The same server
answered 401 to an `X-Flower-Admin-Session` on `/api/admin`, 403 on the sync surface, and 404
for `POST /api/admin/sessions`. `BrowserDeviceAccessTests` covers the same ground server-side.
What has not been run is the DOM half — a real tab, IndexedDB surviving a reload, the settings
overlay opening on `page=settings`.
