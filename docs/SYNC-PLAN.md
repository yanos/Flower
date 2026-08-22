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

- **Flower.Desktop hosts the OpenSubsonic API itself, in-process, with no database** — a thin mapping layer over the `Library` already loaded in memory. Unlike a standalone `Flower.Server`, which needs SQLite because it's headless — though both ended up on the same `Flower.Core` persistence layer in the end, see the Tier 4.1 note below.
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

**Confirmed real problem, changed after real-world testing:** the original per-album (`getAlbumList2`+`getAlbum`) catalog fetch produced 1000+ HTTP connections against a 1,397-album library, causing real network/battery cost on iOS. `LibrarySyncService` now uses a bespoke bulk endpoint instead — `GET /api/flower/v1/library` returning the whole manifest in one response, same shape as the playlist endpoint. The standard `/rest/getAlbumList2`/`getAlbum` endpoints are unchanged for real OpenSubsonic interop; only Flower-to-Flower bulk sync moved off them.

That endpoint is now conditional: it serves `Library.ChangeToken` as its `ETag` and answers `304` to a matching `If-None-Match`, and the serialized manifest is cached against the token it was built from (the album-art hashes it embeds cost ~1,400 TagLib file opens to compute). The same token is advertised on `/info`, which is what finally lets a Client notice a *server-side* change: sync previously fired only on first mDNS contact or a debounced local change, so a track added on the Server went unnoticed for as long as both apps stayed running. The ~5s `/info` poll every Client already runs now carries the token, so a change on either side converges within one poll. See ARCHITECTURE-REVIEW §1.4.

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
a device keypair and a one-time pairing code. The only thing that can't join that scheme is a
third-party Subsonic client, and only because the protocol it implements is published and fixed.

An earlier revision of this section had three paths, with the browser UI on WebAuthn passkeys as
its own middle tier. That was built on a false premise — see the security bullet above — and is
now folded into path A.

**Status — server side done, browser client not started.** Built and tested:
`TrustedPeer.IsAdmin` and the admin-granting pairing code that sets it; `/api/admin` gated by
`DeviceSignatureAuth.VerifyTrustedPeer` + `IsAdmin` instead of a login; the `PairingInvite`
(`flower://pair?host=…&code=…&fp=…`) shared type and the `fp=` server-key pin; the startup
bootstrap code printed to stdout; path B end to end (`SubsonicCredentialStore`, the admin
routes that mint/list/revoke, `SubsonicAuth` over per-client credentials, and the `apiKey`
form); and `StreamTicketService` with its mint route and `/rest` redemption.

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
`SyncInfoResponseDto` wire shape with its own `SyncProtocolJsonContext`. `SyncHttpServer` and
`NetworkDiscoveryService` now reference those constants instead of owning private copies, so the
two implementations cannot drift. `Flower.iOS`'s `BonjourMdnsBackend` needed no change (same
namespace).

The server side is then small: `MdnsAdvertiser`, an `IHostedLifecycleService` that advertises in
`StartedAsync` (the bound port is only known once Kestrel has started) and unadvertises on
shutdown so clients prune the row immediately; and `DiscoveryEndpoints`, serving the same DTO the
app serves, with `IsServer` always true and `trustsCaller` answered from `TrustedPeerStore` for
the ~5s poll every client already runs. Both failure modes are warnings, not startup failures -
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
not loopback and still advertises, which is the normal path. The app-as-peer `SyncHttpServer`
needs no equivalent guard - it always binds `http://+:{port}/`.

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

**Not built:** the browser half of path A — `Flower.Web` generating and storing its own
keypair, and the pairing/admin screens that use it. That work waits on build-order step 4's
admin UI, which doesn't exist yet. One open question to settle there, flagged because it
changes the shape of the client code: whether .NET-for-WebAssembly's own `ECDsa` works in the
browser runtime (in which case `DeviceSigningKey` is reusable as-is and only key *storage*
needs a browser backend) or whether it needs a JS-interop `crypto.subtle` module in the
`webaudio.js` mould. The non-extractable-key property argues for the interop module either
way, but this needs a real WASM build to settle rather than an assumption.

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

**Pairing, one mechanism for all of them.** Today's `SyncHttpServer.PeerApprovalRequested` flow
holds an incoming request open for 60 seconds waiting on a human to click Approve, and fails
closed if nobody's listening — fine when the admin is at the machine, a bad fit for a headless
box nobody's watching. `Flower.Server` replaces it with an **admin-issued, one-time pairing
code**, proactive instead of reactive:

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

**Stays in `Flower`:** `LibraryStore`/`PlaylistStore` (thin wrappers now — their storage moved to the shared `Flower.Core/Persistence/Sql/` repositories in Tier 4.1, which is what the parenthetical here originally argued was impossible while they were JSON); `AppSettingsStore`/`ColumnVisibilityStore`/`DeviceIdentityStore`/`PlaylistSyncStateStore` (client-only concerns); `LibraryOpenSubsonicMapper` (Avalonia `Bitmap`-coupled via `AlbumArtLoader`, see above); `TrackListBuilder`/`TrackRowViewModel` (holds an Avalonia `Bitmap`); `SyncHttpServer`/`NetworkDiscoveryService`/`PlaylistSyncService` (P2P sync is a different feature from `Flower.Server`).

**Reuse boundary:** `Importer.ImportAsync()` produces shared `Flower.Core.Track`s → the shared SQLite schema and repositories (`Flower.Core/Persistence/Sql/`) → server-internal mapping to shared `SubsonicSongDto` → HTTP/JSON → OpenSubsonic client maps back to its own `Flower.Core.Track`. One deliberate seam remains, `SubsonicSongDto`, which keeps the Path-can't-cross-the-wire rule out of the shared model.

> **The `TrackEntity` seam is gone — see ARCHITECTURE-REVIEW.md Tier 4.1.** It was drawn while the client was JSON and the server was EF Core, so "keep SQLite/EF concerns out of the shared model" was keeping a real asymmetry out. Both sides are now on one raw-SQLite layer, where the seam would only duplicate SQL, so the schema, the migration runner, the row mapper and the write path are shared. The server does not keep a library of its own at all: it runs on the same resident `Flower.Core` `Library`, and both hosts answer OpenSubsonic browse requests out of that library's `Snapshot` — so the album-grouping rule has one implementation (`SubsonicIdentity.AlbumIdFor`) instead of two that only tests held to the same answer. Both write through on the spot as well, and playlists went the same way once `Playlist` gained Subsonic's `comment`/`is_public`/`created_at` — the server reads `library.Playlists` directly. The server has no `Library` wrapper of its own — it resolves the shared type straight out of DI, and both hosts hand it the same `TrackRepository` and `PlaylistRepository` as its `ITrackStore`/`IPlaylistStore`, so every write — stats, stars, a finished download, a rescan, a sync merge, a playlist rename — is issued by the code that applies the change rather than by whichever caller remembered to save afterwards. Nothing stays server-side: `Flower.Server/Data/` is gone entirely. `SubsonicSongDto` is untouched and stays a seam — the wire shape is a published protocol, which is a different argument entirely.

**Mechanical steps — done:** new `Flower.Core` classlib → `git mv` `Models/`/`Importer/`/the OpenSubsonic contracts+client/pairing-trust primitives in, along with their package refs → `Flower.csproj` gets a `ProjectReference` → confirmed `Flower.Tests` passes unchanged (359/359, plus the pre-existing `RequiresLibVLC` suite bar one flaky timing test that passes in isolation, unrelated to this move). **Next:** scaffold `Flower.Server` (`dotnet new webapi`) referencing `Flower.Core`.

### `Flower.Server` v1 — done

New `net10.0` `Microsoft.NET.Sdk.Web` project (`Flower.Server/`), referencing `Flower.Core` only (no Avalonia/LibVLC). Minimal API + Kestrel, plain HTTP, binds `0.0.0.0:4533` by default (`appsettings.json`'s `Urls` key - override via `ASPNETCORE_URLS`/`Urls` env var same as any ASP.NET Core app).

- **Schema — as built, then superseded.** Originally EF Core/SQLite in `Flower.Server/Data/`: a `TrackEntity` restating `Track`'s fields, `PlaylistEntity`/`PlaylistTrackEntity`, `FlowerDbContext` with `PRAGMA journal_mode=WAL` and `Default Timeout=30`, `IDbContextFactory<FlowerDbContext>` per request, `EnsureCreatedAsync()` instead of formal migrations. **That whole layer is gone** — `Flower.Server/Data/` is empty and the server shares `Flower.Core`'s schema, migration runner, row mapper and write path with the client, running on the same resident `Library`. See the Tier 4.1 note above for the full account. The one design decision that survived the move intact: no separate Artist/Album tables — artist and album ids are deterministic hashes of the normalized name (`SubsonicIdentity`, same normalize-then-hash shape as `Track.SyncKey`), so browsing groups rows by these instead of needing an upsert-reconciled Artist/Album table just to hand out stable ids.
- **Importer wiring (`LibraryImportService`):** runs once at startup, reusing `Flower.Core`'s own `Importer.ImportAsync` unchanged (per the "Reuse boundary" note) against `Flower:LibraryPaths` from config, upserting `TrackEntity` rows matched by `Path` and removing rows for files no longer present - same carry-forward shape as `Library.UpdateTracks`, just against SQLite instead of an in-memory list. No rescan-on-demand endpoint yet (deferred - step 3's admin UI is the natural place to trigger one).
- **OpenSubsonic endpoints (`SubsonicEndpoints`):** `ping`, `getArtists`, `getArtist`, `getAlbum`, `getAlbumList2` (alphabetical/by-artist/newest/random), `getSong`, `search3`, `getPlaylists`/`getPlaylist`/`createPlaylist`/`updatePlaylist`/`deletePlaylist`, `star`/`unstar`, `scrobble`, `stream`/`download` (`Results.File(..., enableRangeProcessing: true)`), `getCoverArt` (embedded tag picture, falling back to a `cover.*`/`folder.*` file next to the track - originally a private copy of `AlbumArtLoader.TryGetLocalArtBytes`'s logic on the grounds that it lived in the Avalonia-coupled `Flower` project, since **un**duplicated: the lookup needs no Avalonia at all and now lives in `Flower.Core`'s `LocalAlbumArtReader`, shared by all three callers. The two copies had already drifted - the server's accepted only three image extensions to the client's eight - see ARCHITECTURE-REVIEW Tier 2.2). Responses are built from `Flower.Core`'s own `OpenSubsonicContracts.cs` types directly (`SubsonicResults`), so the wire shape is guaranteed to match what `OpenSubsonicClient` already parses - reflection-based JSON (not source-generated), since this project isn't trimmed/AOT the way mobile is. GET-only and `f=json`-only for v1 (matches Flower's own client and every real Subsonic client is fine defaulting to json); real multi-client XML support is deferred, not designed.
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

- **No native audio at all, not just conditioned out of the project file:** `LibVLCSharp`/`Miniaudio-CS` ship no WASM build, so rather than fight MSBuild `Condition`s to strip them from `Flower.csproj` (referenced by every platform), `App.axaml.cs`'s `Bootstrap()` just never constructs `VlcNativeSetup`/`LibVLC`/`MiniaudioSink`/`GaplessAudioManager` on `OperatingSystem.IsBrowser()` - it registers a new no-op `NullAudioManager : IAudioManager` (`Flower/Manager/`) instead. Real playback (`WebAudioSink`, Web Audio API interop) is still a later pass; this milestone is deliberately UI-rendering-only, per the build order below.
- **No P2P sync stack either, discovered empirically, not anticipated in the original plan:** `SyncHttpServer` (raw `HttpListener`) and `NetworkDiscoveryService` (mDNS multicast UDP) are trivially unavailable in a browser sandbox, but the real blocker runs deeper - **.NET-for-WASM's crypto backend has no asymmetric crypto support at all** (verified directly: `ECDsa.Create()`/`RSA.Create()` both throw `PlatformNotSupportedException` for every curve/key size tried; only symmetric crypto and hashing work), so `DeviceSigningKey` - and everything built on it (`SyncHttpServer`, `NetworkDiscoveryService`, `PlaylistSyncService`, `LibrarySyncService`, `LibraryDownloadService`, `PeerPairingService`, `PeerUnpairNotifier`, `PairedServerReachability`, `PeerTrackResolver`) - simply cannot be constructed there, and `MainViewModel` (needed by `MobileMainViewModel`, which is what actually renders on browser via the existing `ISingleViewApplicationLifetime` path Android/iOS already use) hard-required all of them as non-null constructor parameters. Fixed by making those ten parameters nullable *and* defaulted (`= null`, trailing) on `MainViewModel`'s constructor - confirmed empirically that a bare nullable-typed parameter with no default is **not** enough for `Microsoft.Extensions.DependencyInjection`'s constructor-selection to pick this constructor over `MainViewModel`'s existing parameterless one when the type isn't registered (nullable-reference annotations aren't consulted for this; only a real default value is). `App.axaml.cs` skips constructing the whole chain on `IsBrowser()` and only conditionally registers each one in the DI container (`services.AddSingleton(instance)` throws on a null instance, so unlike the constructor parameter, these are just left unregistered on browser rather than "registered as null"). `PeerLibrary` (`MainViewModel`'s live peer-browse VM, built from `DeviceIdentity`/`DeviceSigningKey`) is now nullable too, guarded at its few call sites (`MainView.axaml.cs` - desktop-only, never reached on browser anyway). Every other platform gets the exact same non-null instances as before; `Flower.Tests`' `MainViewModelSidebarNavigationTests` still constructs a real `MainViewModel` with everything non-null (positional args reordered to match).
- **Verified rendering, not just build success:** headless Chrome (via Playwright) loading the served `dotnet run` output shows `MobileMainView` actually rendering - header, settings gear, the correct "No Music Yet / Add a library folder in Settings to get started" empty-library state, and the full bottom tab bar (Recent/Songs/Albums/Artists/Playlists/Search) - with zero console/page errors. Confirmed via full-solution platform build too: Desktop, iOS-simulator (`RuntimeIdentifier=iossimulator-arm64`, matching CI), and Android all still build clean; all 366 fast tests still pass.
- **Since built:** the admin settings screen, including the "Add device" pairing-code button, and an answer to the browser-auth question above that does not need a browser-side key at all - see "The server's settings page in the browser" below.
- **Not yet built:** the library itself. The browser head starts with an empty `Library` and honestly reports 0 songs, because `App.axaml.cs` skips the startup rescan entirely on `IsBrowser()` and no remote importer has replaced it — see "The browser's library" below for the design, which turns out not to be Subsonic-shaped at all. A paired browser session's `/rest/*` auth is still the classic Subsonic credential scheme; the admin-session token covers `/api/admin` only, deliberately, since it is a bearer token and the streaming surface already has `StreamTicketService` for callers that cannot sign.

### The server's settings page in the browser — done

The admin settings screen build-order step 4 called for, built as **the same Avalonia view and view-model the desktop app's Settings window uses**, not a second implementation of it.

**The shared screen.** `SettingsWindow` used to be the settings screen: a `Window` whose code-behind read and wrote `MainViewModel` directly, control by control. It is now a ~20-line frame around `Flower/Views/SettingsPanel.axaml`, a `UserControl` driven by a new `SettingsViewModel` over an `ISettingsBackend`. Two backends implement it — `LocalSettingsBackend` (this device: the same property sets, stores and unawaited rescan as before) and `RemoteServerSettingsBackend` (a Flower server, over its admin API) — and a `SettingsCapabilities` record decides which tabs and controls appear, so administering a headless server runs the identical XAML rather than a drifting copy. `TrustedDevicesView` is gone, absorbed into the panel's Devices tab.

- **The draft is the point.** The old window applied the alias, the theme and every checkbox the moment they were touched, and only treated the folder list as cancellable — so "Cancel" quietly kept most of what had just changed. `SettingsViewModel` holds edits until Save. Nothing about the remote case could have worked the old way anyway: every write there is a request, and firing one per keystroke is not an option.
- **Forget-this-device is confirmed inline**, not in a `ConfirmDialogWindow`. Avalonia.Browser is single-view; there is no `Window` to own a dialog, which is what made the old `TopLevel.GetTopLevel(this) is not Window` guard silently turn Forget into a no-op there.
- **Rendered as a full-page overlay in the browser** (`MainView.ShowSettingsOverlay`) and as a dialog on desktop, chosen by whether there is a `Window` at all.
- **The iTunes/Music.app switches administer the server, not the browser.** They were originally capability-off for the remote backend, on the reasoning that a browser tab has no Music.app - but the machine being configured usually does: the common self-hosted case is a Mac already holding the Music.app library the server is meant to serve. The server now adopts Music.app's own media folder as a library path on the first scan that finds it - persisting it to `flower-server.json` exactly as `AppSettingsStore.Load` persists it to `settings.json`, so it is visible and removable in the folder list - and applies both per-track imports after each scan. All three switches are disabled with a note when there is no folder to find (`GET /api/admin/settings` reports `appleMusicFolder: null`), which is every non-Mac host: a switch that can only ever do nothing is worse than one that says why. `SyncDateAddedFromITunes` now defaults on in the app too - it used to be opt-in to avoid reordering anyone's Recently Added on update, which with no released users protects nobody and left the more truthful of the two dates switched off by default.
- **Which is shared with the app, not reimplemented beside it.** Three things came out of that feature having a client half and a server half. `MusicLibrarySettings` (Flower.Core) holds the settings both hosts have - the library folders and the three iTunes switches - and `AppSettings` and `FlowerServerOptions` both derive from it, so those are declared, defaulted and documented once; what is left in each is genuinely host-specific (window geometry and column widths on one side, a data directory and mDNS/LanGuard settings on the other, populated by mechanisms that don't unify either - a JSON file this process rewrites versus an `IConfiguration` stack). `ITunesIntegration` (Flower.Core) holds what those switches *mean*: whether there is a media folder to adopt (`ResolveMediaFolderToAdopt`, called by both `AppSettingsStore.Load` and the server's rescan), which of the two imports should run, and the one-line description of where they read from. And `ServerSettingsDto`/`ServerSettingsUpdateDto` moved to Flower.Core beside `LibrarySyncContracts`, replacing the server's own matching pair - every operator-editable setting was a field in both, so adding one meant adding it twice. The rest of the `/api/admin` surface is still a matching pair of records on purpose: neither side has had to edit those in step with the other.
- **The master switch adopts the folder but never un-adopts it.** Unchecking "Use iTunes/Music.app library" used to also drop Music.app's media folder from the library folders, on the reasoning that the switch owned the folder it had added. For the common case - a library that *is* Music.app's folder and nothing else - that made declining two metadata imports silently empty the whole library, since a scan of no folders finds no tracks. The switch now governs only what Flower does *to* Music.app: adopt the folder when turned on, run the two per-track imports, and stop offering the folder again when turned off. Taking the folder back out is Remove Folder's job, right below it. Both are needed to drop Music.app entirely, which is why they are two controls.
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

**The client's way in.** The desktop device-detail pane gained a **"Server Settings…"** button for a selected headless server it has already paired with (`MainViewModel.CanOpenSelectedServerSettings`). It signs a session mint against that server and opens the returned URL in the OS browser. Deliberately *not* also gated on this device being an admin — nothing client-side can know that without asking, and a button that says "paired, but not an administrator" inline is a better answer than a hidden one.

**New admin routes**, all on the existing signature gate: `GET`/`PUT /api/admin/settings` (written to `flower-server.json` through `ServerSettingsWriter`, a read-modify-write over a `JsonNode` so the seeded documentation keys and anything an operator added survive), `GET /api/admin/library` + `POST /api/admin/library/rescan` (`LibraryRescanCoordinator`, on its own DI scope because the request that starts a 16k-track scan is answered long before it finishes), and `GET /api/admin/logs` off `InMemoryLogStore`.

- **The "admin requests take no body" rule had to go**, and could: minimal APIs bind parameters *before* endpoint filters run, so a body-bound parameter would consume the stream before the signature check could hash it. No handler binds a body; the filter buffers and rewinds it, exactly as `SyncEndpoints` already does.
- **`PUT /settings` answers with the merged result, not a re-read.** Re-reading looks more honest but is not: `flower-server.json` is watched with `reloadOnChange`, and that watcher is debounced, so `CurrentValue` right after the write is usually still the old value — the page would show the change revert and then quietly reappear. It also reports `RestartRequired` for the fields `MdnsAdvertiser` reads once at startup.
- **`LibraryImportService` and the `LanGuard` middleware moved to `IOptionsMonitor`.** Both held an `IOptions<>` snapshot bound once for the life of the process, so a folder added in the browser would have been ignored by the very rescan the browser triggers next, and a CIDR added to get *back in* would not have applied until a restart.
- **A real pre-existing bug, found by the first test written against it:** every "paired but not an admin" refusal was a 500, not a 403. `Results.Forbid()` runs ASP.NET Core's authentication forbid handler, and this app registers no authentication scheme at all — it authenticates by device signature — so it threw rather than answering.

### `WebAudioManager` — real browser playback, done

Real audio now plays in `Flower.Web`, closing the "browser playback" gap flagged above. Turned out not to be a `WebAudioSink` (the originally-planned `IAudioSink` implementation feeding `GaplessRingBuffer`) - that shape doesn't work in a browser at all: `GaplessCoordinator`/`TrackDecoder` decode via LibVLC, which has no WASM build either, so an `IAudioSink` alone would have nothing to consume even with a working render side. Built a browser-only `IAudioManager` instead (`Flower/Manager/WebAudioManager.cs`), bypassing `IAudioSink`/`GaplessCoordinator`/`GaplessRingBuffer` entirely and driving a single reused `HTMLAudioElement` (`Flower.Web/wwwroot/webaudio.js`) via `[JSImport]` - the browser's own decoder handles whatever format the `<audio>` element supports directly from the track's URL, the same as a plain HTML `<audio src>` tag, so there's no PCM pipeline to build at all on this platform.

- **`[JSImport]`/`System.Runtime.InteropServices.JavaScript` compiles fine in `Flower.csproj`'s shared non-browser `net10.0` TFM** (verified directly, mirroring the earlier `ECDsa`-on-WASM check) - the reference assembly ships in the standard `net10.0` ref pack, so `WebAudioManager` needed no platform-conditional compilation, same as the rest of `Flower.csproj`.
- **Module import path pitfall, found and fixed:** `JSHost.ImportAsync(name, "./webaudio.js")` (relative) 404'd - the dotnet WASM loader resolves relative import specifiers against `/_framework/`, not `wwwroot`'s own root. Fixed with a site-root-relative path (`"/webaudio.js"`).
- **No `[JSExport]`/JS→C# callbacks** - `WebAudioManager` polls `getCurrentTime`/`getDuration`/`getPaused`/`getEnded` on a 250ms `Timer`, the same pattern `GaplessAudioManager` already uses for its own `PositionChanged` polling, rather than wiring the `<audio>` element's native `timeupdate`/`ended` events back into C#. Simpler, and the staleness cost (up to one poll interval) is the same tradeoff already accepted elsewhere in the codebase.
- **Accepted v1 scope, matching the plan's own "gaplessness may lag" call:** no gapless handover (`SetUpcoming` is a no-op - a future pass could preload the next track into a second hidden `<audio>` element), no in-browser EQ (`ApplyEqualizer` is a no-op).
- **Verified end-to-end against a real synthetic WAV, not just "no exceptions":** headless Chrome (Playwright) driving a temporary diagnostic hook confirmed real decode+playback - position advancing in step with wall-clock time, `Pause()`/`Resume()` actually freezing/resuming playback (not just flipping a flag), `Position` (seek) jumping the reported time correctly, `Volume` accepted without error, and `EndReached` firing exactly once at track end. Diagnostic hook and test asset were removed after verification; `WebAudioManager` itself ships clean.
- Full-platform regression check after the change: Desktop, iOS-simulator (`RuntimeIdentifier=iossimulator-arm64`), Android, and `Flower.Web` all still build with 0 errors; all 366 fast `Flower.Tests` still pass.

### The browser's library: a shared `RemoteLibraryImporter` — designed, not built

The browser's empty library was never a missing *client*. `OpenSubsonicClient`, `LibrarySyncMapper.ToPlaceholderTrack` (a Subsonic `Child` → a `Path == null` Flower `Track`) and the bulk manifest endpoint all exist already and are already shared with the desktop. What is missing is an `IMusicImporter` face on the pull the desktop already performs, and one credential a browser can actually present. The aim here is that the browser-specific code ends up being roughly one credential class and a stream-URL step — everything else is either shared as-is or a refactor of existing desktop code that pays for itself.

**It is not Subsonic-shaped, despite what this doc used to say.** Earlier revisions named the missing piece `SubsonicLibraryImporter`, which contradicts a decision taken later and recorded in `LibrarySyncService`'s own doc comment: the desktop deliberately stopped browsing over `getAlbumList2`/`getAlbum`, because one request per album meant hundreds or thousands of connections in a burst against a real library — observed in practice as heavy iOS `nw_connection` log churn — and moved to Flower's own `GET /api/flower/v1/library` (whole catalog, one request, `ETag`/304). The browser wants that same endpoint, so the class to build is a **`RemoteLibraryImporter`** over the bulk manifest. A genuine `SubsonicLibraryImporter` is still worth building later, but only for third-party servers (Navidrome, Jellyfin) that have no such endpoint — never for talking to `Flower.Server`, which has one.

**Seam 1 — `IPeerCredentials`, the only real browser fork.** Three places build the same signed identity headers today: `LibrarySyncService.SyncWithAsync` inline, `PlaylistSyncService.AddSignedIdentityHeaders`, and `PeerOpenSubsonicClientFactory.Create` as a `PeerIdentityParamsBuilder` delegate. Collapse them onto one interface with the shape `OpenSubsonicClient` already accepts — `Authorize(method, path, query, body)` returning the key/value pairs to attach — with two implementations. `SignedDeviceCredentials` is the existing body moved verbatim (desktop, mobile, CLI); `AdminSessionCredentials` returns a single `X-Flower-Session` header and is about ten lines. That second class is essentially the entire browser-specific auth surface, and the interface is the seam that makes swapping it for a real browser keypair later a local change rather than a sweep.

**Seam 2 — `RemoteLibraryImporter : IMusicImporter`** (`Flower.Core/Importer/`), constructed over a base URL, an `IPeerCredentials`, the origin server's fingerprint and this device's own. Its body is lifted, not rewritten, from `SyncWithAsync`: the GET, the `If-None-Match`/304 handling, the `LibrarySyncManifestDto` deserialize, then `ToPlaceholderTrack` per song. It exposes both `ImportAsync` (the `IMusicImporter` contract, ETag ignored) and a `FetchAsync(ifNoneMatch)` returning not-modified/ETag/tracks. `LibrarySyncService` then keeps only what is genuinely its own — the per-peer `_lastSeenTokens` cache, `PeerTrustRejected`, `SyncRolePolicy`, and the additive merge into `Library` — and calls `FetchAsync` instead of hand-rolling its own request. Desktop behaviour is unchanged and there is one HTTP path where there were two.

Two constructor arguments carry the browser's asymmetry. `ownFingerprint` is empty there: it exists only so `ToPlaceholderTrack` can filter this device's own echo out of an incoming `RemotePlayCounts`, and an empty string matches nothing. `originFingerprint` is the *server's*, which the browser has no other way to know — it must read it from `/info` (`SyncProtocol.InfoPath`) at startup. Placeholder tracks without it are unplayable, so this is a real prerequisite and not a detail.

**Seam 3 — the server accepts the session token on two more routes.** `SyncEndpoints`' auth filter gates on `DeviceSignatureAuth.AuthenticateTrustedPeer`; add a fallback that resolves a valid `X-Flower-Session` to its minting fingerprint via `AdminSessionService`, and the same fallback on `POST /api/flower/v1/stream-tickets`. No new endpoints, and the browser can then both read the catalog and mint playable URLs.

This **widens the admin-session bearer past `/api/admin`**, which `AdminSessionService`'s own doc comment records as a deliberate boundary, so it is a decision and not a detail. The case for it: both additions are read-and-play rather than administration, and `LanGuard` still keeps the token unusable from off the LAN. The case against is that it is still a bearer token, and it is now a bearer token for the catalog. The principled alternative is the non-extractable WebCrypto keypair the "browser is a device" design sketches, which is **not** a drop-in: `DeviceSigningKey.Sign` is synchronous and `crypto.subtle` is asynchronous, so adopting it re-shapes every signing call site. Recommendation: take the bearer fallback now, keep the keypair as the follow-up, and note that a 60-minute session minted by the desktop's "Server Settings…" button is thin for a jukebox tab left open all evening — session lifetime or renewal will need revisiting once the browser is a player rather than a settings screen.

**Seam 4 — playback, where sharing genuinely runs out.** `WebAudioManager.Play` sets the `<audio>` element's source to `track.Path ?? string.Empty`, which is always empty for a placeholder track. It needs to mint a stream ticket and use the returned URL. It cannot reuse `PeerTrackResolver`, which resolves a `DiscoveredDevice` through mDNS discovery — the browser's server is simply its own origin. The awkward part is that `Play` is synchronous while minting is a network call, so this wants either an `IStreamUrlResolver` seam (desktop implementation wrapping `PeerTrackResolver`, browser implementation wrapping the ticket call) or a pre-resolve step in `PlaylistControlViewModel`. This is the part of the work most likely to cost more than it looks.

**Seam 5 — startup wiring.** The `if (!OperatingSystem.IsBrowser())` guard around the startup rescan in `App.axaml.cs` goes away; the block runs whatever `IMusicImporter` is registered, with only the two iTunes calls staying behind a "is this a local importer?" check. The browser registers `RemoteLibraryImporter`, desktop keeps the file-scanning `Importer` — and per the original intent below, which now finally has a second implementation to make it real, "local files" vs. "self-hosted server" becomes a settings choice via `IMusicImporter` rather than a special-cased second code path. This supersedes `CROSS-PLATFORM-PLAN.md` item #3's original `IMusicSource` proposal, which shipped instead as `IMusicImporter`.

**Known to remain afterwards:** album art, since `AlbumArtLoader` reaches a peer through `PeerTrackResolver` too and will need the same treatment as seam 4 before art renders in the browser; and playlists, which are less work than they look because `SyncEndpoints` already serves `GET /api/flower/v1/playlists` over the very gate seam 3 opens.

### Suggested build order

1. **Done.** Extract `Flower.Core` (mechanical git-mv + reference fixups; confirm `Flower.Tests` still passes unchanged).
2. **Done.** Scaffold `Flower.Server`: SQLite schema (originally EF Core, since moved onto `Flower.Core`'s shared layer), importer wired up, OpenSubsonic endpoints working against a real Navidrome-compatible client. See "`Flower.Server` v1" below.
3. **Done.** Pairing-code endpoint + admin auth + `LanGuard` CGNAT allowance + rate limiting on the redeem route — get a real device pairing against a real headless instance before building UI on top of it. See "Pairing-code endpoint, admin auth, `LanGuard` — done" above.
4. Scaffold `Flower.Web`. **Done so far:** existing Views/ViewModels building and rendering in-browser (see "`Flower.Web` scaffolding — rendering milestone done" above), real audio playback via `WebAudioManager`, and the admin settings screen with the pairing-code "Add device" button, served by `Flower.Server` itself (see "The server's settings page in the browser — done" above). **Still open:** a `RemoteLibraryImporter : IMusicImporter` over the bulk `/api/flower/v1/library` manifest so the library isn't always empty (designed in "The browser's library" above — *not* the Subsonic-shaped importer earlier revisions of this doc called for), then full jukebox browse/search/queue, which needs a populated library to be worth testing against.
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

### 403 means revoked; a bad signature is 401

Both peer servers (`SyncHttpServer` and `Flower.Server`'s `SyncEndpoints`)
used to answer *every* trusted-peer check failure with a 403, and
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

## Status summary

All numbered steps through Phase 4 are **done**: `CROSS-PLATFORM-PLAN.md` item #3 updated to the private-file-library iOS design; WiFi/LAN discovery + LocalSend-style transfer; `UIFileSharingEnabled` for USB; Bluetooth/programmatic-USB deliberately not built; playlist metadata sync; the OpenSubsonic client; the full Phase 3 stack (trust gate, embedded host, merge logic, mobile download UI); and Phase 4's cryptographic identity/signed-request hardening (route table, rate limiting, LAN-only enforcement, persisted denials, server-initiated unpair, body size cap). `Flower.Server` build-order steps 1 (extracting `Flower.Core`), 2 (scaffolding `Flower.Server` itself - SQLite schema, since moved onto `Flower.Core`'s shared layer, importer wiring, the full OpenSubsonic endpoint set, verified end-to-end against a real `OpenSubsonicClient`), and 3 (admin-issued pairing-code endpoint, admin bearer-token auth, `LanGuard` CGNAT allowance + configurable extra CIDRs, rate limiting on the redeem route) are also **done** (see "Project structure", "`Flower.Server` v1", and "Pairing-code endpoint, admin auth, `LanGuard`" above).

Build order step 4's first two parts - `Flower.Web` scaffolding (existing Views/ViewModels rendering in-browser) and real audio playback (`WebAudioManager`) - are also **done** (see "`Flower.Web` scaffolding — rendering milestone done" and "`WebAudioManager` — real browser playback, done" above). Two real architectural findings along the way: .NET-for-WASM has no asymmetric crypto support at all, so `MainViewModel`'s P2P sync dependencies (`DeviceSigningKey` and everything built on it) are now nullable/defaulted rather than hard requirements, gated on `OperatingSystem.IsBrowser()`; and the browser audio path couldn't reuse `IAudioSink`/`GaplessCoordinator` at all (LibVLC-backed decode has no WASM build either), so it's a separate `IAudioManager` implementation driving a plain `<audio>` element instead.

**Remaining work:** fold the client into the `IMusicImporter` abstraction as a user-facing settings choice; add Jellyfin as a second `IMusicImporter` backend; real-device Android download-path verification and end-to-end testing against a real peer; and, now the next initiative, the rest of build order step 4 - a `RemoteLibraryImporter` so the browser library isn't always empty, reached through a shared `IPeerCredentials` seam and an admin-session fallback on the sync and stream-ticket routes (designed in full under "The browser's library" above; note this supersedes the `SubsonicLibraryImporter` earlier revisions called for, which was the wrong shape - a true Subsonic importer is only needed for third-party servers), plus stream-ticket playback, album art and the `/rest/*` auth question for third-party clients - before Docker packaging + Tailscale/LettuceEncrypt docs (step 5).
