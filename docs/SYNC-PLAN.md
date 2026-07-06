# Sync & Self-Hosting — Investigation & Plan

## Goal

Two related goals, unified by one design decision below: sync music (files +
metadata) between the Flower desktop app (Windows/macOS/Linux) and the
Flower phone app (iOS/Android) across all six desktop×phone permutations;
and support a "self-hosting" model where a server (the user's own NAS/home
box/VPS, or another Flower app on the same network) holds a canonical music
library that other Flower apps stream/sync against.

## Architecture prerequisite (decided)

Flower's iOS app will own all its music files itself: its own sandboxed
Documents-folder library, imported/read via TagLib, exactly like desktop's
`Importer` scans `~/Music`. **No integration with Apple's Music app or
`MPMediaLibrary`/`MPMediaItem`.**

This supersedes the iOS import approach floated in `CROSS-PLATFORM-PLAN.md`
item #3 (which proposed `MPMediaLibrary`/`MPMediaQuery`) — that section needs
updating to a private-file-library importer instead. It also means
`Track.Path` stays a plain filesystem path on iOS, no `NSUrl`/`assetURL`
special-casing needed.

**Why this decision matters:** `MPMediaLibrary` gives no way to write files
into it from outside the Music app (no write access, DRM-restricted), which
would make syncing *to* iOS impossible under any transport. Owning files
directly unblocks both USB (via `UIFileSharingEnabled`) and WiFi sync.

---

## The unifying decision: one OpenSubsonic client, three interchangeable servers

This is the design decision that shapes everything below, so it comes before
the transport-by-transport investigation: peer-to-peer WiFi sync (this doc)
and self-hosted-server support were originally scoped as two separate
features with two separate protocols. They're now one client protocol with
three interchangeable things on the other end.

The self-hosted music server space already has a mature de facto standard:
the **Subsonic API**, whose actively-maintained open successor spec is
**OpenSubsonic** (full backwards compatibility with classic Subsonic API
v1.16.1, plus JSON-first responses, better versioning, and API-key auth).
Building a client for it means **zero server code from Flower** to get
self-hosting working at all — users point Flower at any server they already
run or can spin up in one Docker container.

- **Target server: Navidrome** (Go, GPLv3, Docker-first, actively
  maintained, low resource use) as the primary target — it drives much of
  OpenSubsonic's evolution, so building against it/OpenSubsonic compliance
  covers the rest (Gonic, Airsonic-Advanced, Ampache's compat layer) for
  free.
- **Feature coverage confirmed sufficient**: artist/album/song browsing,
  `/stream` with HTTP byte-range support (seeking works), full playlist
  CRUD, starred/favorites, cover art, search, scrobble/now-playing. No gap
  found for a standard player client.
- **Auth**: classic `t = md5(password + salt)` + `s = salt` per request
  (fine over HTTPS, which any real deployment should terminate via a reverse
  proxy anyway), or OpenSubsonic's newer API-key auth extension — simple
  enough to hand-roll, no heavy security library needed.
- **No usable .NET client library exists** — the two that exist
  (`SubsonicSharp`, a dead pre-.NET-Standard PCL; `wuffSonic`, self-described
  WIP) are unusable. Not a blocker: this is a thin REST+JSON API. Hand-roll a
  typed client directly in the existing `Flower` project (no `Flower.Core`
  split needed yet — see "Sequencing" below) — auth token generation,
  ~10-15 endpoints (browse, stream, playlist CRUD, search, cover art, star,
  scrobble), DTOs for the JSON envelope. **Small-medium effort, days not
  weeks**, and it's the same client code on desktop and mobile since it's
  just HTTP. **Done**: `OpenSubsonicClient`/`OpenSubsonicContracts.cs`
  (`Flower/Services/`) — classic `token=md5(password+salt)` auth, the
  ID3-tag-based browsing endpoints (`getArtists`/`getArtist`/
  `getAlbumList2`/`getAlbum`/`getSong`, not the older folder-based
  `getIndexes`), `search3`, playlist CRUD, `star`/`unstar`, `scrobble`, and
  URL builders for the binary `stream`/`download`/`getCoverArt` endpoints
  (callers fetch bytes themselves rather than the client buffering audio into
  memory). Unit tested against a fake `HttpMessageHandler` (request
  construction, response parsing, the `status:"failed"` error path) — no
  live server needed. Not yet wired into any UI, Settings, or an
  `IMusicImporter` backend — that's step 8 below, once Phase 3's trust gate
  and embedded host exist to actually point it at.

**The insight that reshapes both halves of this doc:** once Flower speaks
OpenSubsonic as a *client*, it doesn't matter whether the server on the
other end is a third-party Navidrome/Jellyfin instance, a first-party
headless `Flower.Server`, or **another Flower app on the same network
hosting the protocol itself, embedded, with no separate server process at
all**. All three look identical from the client's point of view — same
endpoints, same DTOs, same code. That last option is what turns "Phase 3"
below (full library sync + on-demand audio download between a desktop and a
phone) into the same client as the self-hosting story, rather than a second,
bespoke protocol:

- **Flower.Desktop can host the OpenSubsonic API itself, in-process, with no
  database.** Unlike a standalone `Flower.Server` (further below), which
  needs SQLite/EF Core because it's headless with no pre-existing state,
  Flower.Desktop already has `Library` loaded in memory the moment the app
  is open — the embedded host is a thin mapping layer from that in-memory
  model to OpenSubsonic's JSON shapes, not a new persistence layer.
- **Mobile's sync client and the self-hosting client become the exact same
  code.** A phone talking to its own desktop over mDNS-discovered WiFi and a
  phone talking to a user's Navidrome box over the internet are just two
  different base URLs to the same OpenSubsonic client — not two features to
  build and maintain.

### How "always available to sync with" actually gets stronger over time

An embedded-in-the-GUI server only serves while Flower.Desktop happens to be
open — fine for "sync while both apps are open," not yet "self-hosting" in
the always-on/remote-access sense. Rather than jumping straight to a full
server install, there's a natural staged path, each stage a strict superset
of platform/OS surface area over the last:

1. **Tray/menu-bar mode.** Closing the main window hides it instead of
   quitting the process; the embedded OpenSubsonic host keeps serving as
   long as the app is running in the background. No OS-level registration —
   just how most background-sync utilities already behave (and how
   `SyncHttpServer` already behaves today, just without a tray icon to make
   "still running" visible/quittable).
2. **Auto-start on login** (macOS `LaunchAgent`, Windows startup entry,
   systemd `--user` unit). Gets to "always on whenever I'm logged in"
   without the user manually launching Flower first. Real but small,
   per-OS plumbing — an installer step, not new sync logic.
3. **A true headless daemon** — runs under a system account, survives
   logout, starts at boot. At this point it isn't really "Flower.Desktop
   running a server on the side" anymore; it's `Flower.Server` (further
   below) deployed locally instead of on a NAS/VPS. `Flower.CLI` (see
   `CLI-PLAN.md`) is already the headless, no-Avalonia entry point in this
   repo, making it the natural process to register as an OS service, rather
   than trying to make the GUI app itself run headless.

Stages 1-2 are cheap platform plumbing on top of what's already planned.
Stage 3 is real OS-service-installer work (permissions, interaction with
auto-update, clean uninstall) and shouldn't be built speculatively — it's
the same server code either way, so there's no urgency to build the
"install yourself as a service" wrapper until a concrete user actually wants
that over just running Flower normally.

### Sequencing: don't split `Flower.Core` out until there's a concrete reason to

The "Project structure" section further below describes extracting
`Flower.Core` (`Models`/`Importer`) as a prerequisite for `Flower.Server`.
**That extraction is deferred, not step one.** The OpenSubsonic wire
contracts + client, and Flower.Desktop's embedded host, need no project
split at all — built directly in the existing `Flower` project, which
already has everything needed (`Track`, `Library`, `Importer`) with no
Avalonia/LibVLC boundary to cross, since the embedded host lives inside the
same process as the rest of the desktop app. The split only earns its cost
once there's an actual reason to run this code somewhere that *can't*
reference Avalonia/LibVLC — i.e., once stage 3 above (a real headless
daemon) or a standalone NAS/VPS `Flower.Server` is actually wanted. At that
point, do the extraction described below and pick whichever hosting model
(embedded/tray/login-item/headless) fits, informed by how the embedded
version actually got used rather than guessed upfront.

---

## Recommendation: build WiFi/LAN sync, skip Bluetooth, treat USB as secondary

## 1. WiFi/LAN sync — the transport to actually build

**Why this one:** desktop and mobile are already the same .NET/Avalonia
codebase, so this is the only transport where Flower talks to Flower with no
OS-vendor device protocol and no native interop.

**Plan:**
1. **Discovery** — ~~`Zeroconf` (NuGet) for mDNS~~ **built, using
   `Makaretu.Dns.Multicast.New` instead**: `Zeroconf` turned out to be
   browse/resolve-only (no API to advertise a service), confirmed via its
   README — useless for making a Flower instance discoverable to others.
   `Makaretu.Dns.Multicast.New`'s `ServiceDiscovery` does both advertise and
   browse. Verified working macOS ↔ iOS Simulator (distinct instance names
   after tagging with a platform suffix, since the Simulator shares the host
   Mac's hostname). Gotchas confirmed in practice:
   - Android needs a `MulticastLock` acquired or discovery silently fails —
     implemented (`PlatformMulticastLock`/`AndroidMulticastLockHolder`), but
     **not yet verified on a real device** (deferred, no Android device
     currently available for testing).
   - iOS 14+ needs `NSBonjourServices` (e.g. `_flowersync._tcp`) +
     `NSLocalNetworkUsageDescription` in `Info.plist`, which triggers a
     one-time local-network permission prompt — implemented and confirmed
     working via the iOS Simulator.
2. **Transfer protocol** — don't invent one: reimplement
   [LocalSend's open protocol](https://github.com/localsend/protocol)
   (plain HTTPS+JSON, self-signed on-device certs, mDNS on port 53317 with an
   HTTP fallback when mDNS is blocked). It's transport-agnostic and maps
   cleanly onto C#. **Phase 1 done**: device identity exchange
   (`GET /api/localsend/v2/info`) + a Devices sidebar section on desktop,
   deliberately over plain HTTP rather than HTTPS for now — see "Server"
   below for why. **Phase 2 done**: playlist metadata sync (no audio files
   yet — that's still the later phase below). What shipped:
   - `Playlist` gained a stable `Id` (Guid, generated once) and an `UpdatedAt`
     timestamp bumped on every mutation (rename/add/remove/reorder track),
     so renaming a playlist is never mistaken for creating a different one,
     and both sides have something to diff against. Persisted in
     `playlists.json`; pre-sync records missing an Id get one generated on load.
   - `Track.SyncKey` (Title+Artists+Album+Duration fingerprint) matches tracks
     across devices, since `Track.Path` is a local filesystem path and never
     means the same thing on two devices. A synced playlist can only reference
     tracks already present in both libraries — tracks the peer has that this
     device doesn't are silently dropped (again: no file transfer yet).
   - Two new endpoints on `SyncHttpServer`: `GET /api/flower/v1/playlists`
     (this device's manifest) and `POST /api/flower/v1/playlists/apply`
     (adopt a peer-merged manifest wholesale, no local merge logic).
   - `PlaylistSyncPlanner` (pure, unit tested) does a three-way merge per
     playlist against a persisted last-synced baseline
     (`PlaylistSyncStateStore`, per remote-device-fingerprint): if only one
     side changed since the baseline, that side wins automatically; if both
     did (or there's no baseline and the content differs), it's a real
     conflict.
   - `PlaylistSyncService` runs the actual session: on `DeviceDiscovered`,
     exactly one side (deterministic ordinal compare of the two devices'
     fingerprints) fetches the peer's manifest, runs the planner, and for any
     conflict raises an event that the UI turns into a modal
     (`PlaylistConflictWindow`) asking the user which version to keep — "Keep
     This Device" vs. "Keep {PeerAlias}'s Version". Once every playlist is
     resolved, it saves locally and POSTs the fully-merged manifest back so
     the peer adopts the same result without re-running its own conflict
     logic (avoids two independent, possibly-contradictory resolutions for
     one sync session).
   - Known gaps, deliberately deferred rather than half-built: no playlist
     *deletion* sync (there's no delete-playlist UI at all yet); no retry if
     the peer drops mid-session (each side's own state is still internally
     consistent, so it just converges next time both are up); sync only
     triggers on discovery, not on a later local edit while still connected.
3. **Server** — ~~embed a small Kestrel or `HttpListener` endpoint~~
   **`HttpListener`, not Kestrel**: Kestrel/ASP.NET Core hosting is not
   available on iOS/Android targets at all ("no runtime pack for
   Microsoft.AspNetCore.App" for those RIDs) without unsupported hacks.
   `HttpListener`'s managed implementation works on all four platforms, but
   its HTTPS support outside Windows is a long-standing gap (open
   `dotnet/runtime` issue #19752) — so phase 1 uses plain HTTP and defers
   TLS + fingerprint trust to when file transfer is actually built.
4. **Critical iOS constraint** — iOS suspends the process (and its listener)
   within seconds of backgrounding. Sync must happen with both apps open in
   the foreground ("open both apps to sync"), not as a silent background
   job. Android tolerates this better but battery optimization can still
   throttle a backgrounded socket.
5. Rejected alternatives (checked as "just use this instead" candidates):
   Syncthing (no real iOS client — background daemons get killed; only a
   paid protocol-compatible app exists), KDE Connect (has an iOS port now,
   but same foreground-only ceiling, and it's Qt/ObjC, not embeddable in
   .NET), Resilio Sync (same iOS ceiling), Dukto (iOS support appears dead).
   All are useful only as prior art, not as dependencies.

**Effort:** Medium–Large — mDNS discovery, first-time pairing UX (PIN/QR),
HTTP transfer endpoint, deciding what needs to move (diffing), conflict
handling. **Risk:** Low–Medium — no native interop per OS, the only
platform-specific risk is iOS's foreground-only execution window.

---

## 2. USB — keep it cheap and manual, don't build a programmatic library

**Per-permutation feasibility (given Flower now owns its iOS files):**

| | Android phone | iOS phone |
|---|---|---|
| **Windows** | Easy — `MediaDevices` NuGet (WPD wrapper) | Easy manual (Files tab via iTunes/Apple Devices), moderate programmatic (stale `imobiledevice-net`) |
| **macOS** | Hard — no native Finder MTP support anymore; would need OpenMTP or `libmtp` interop | Easy manual (Finder's "Files" tab), moderate programmatic |
| **Linux** | Easy on GNOME (`gvfs-mtp` auto-mounts as a plain path), moderate on KDE | Moderate — no Finder-equivalent GUI, programmatic-only |

**Plan:**
1. Ship `UIFileSharingEnabled` + `LSSupportsOpeningDocumentsInPlace` on iOS —
   this is free (no code beyond an Info.plist entry) and gives users a
   drag-and-drop sync path via Finder/iTunes' "Files" tab straight into
   Flower's own Documents-folder library.
2. For Android, no Flower-side code is needed either — MTP file transfer
   from Windows Explorer / Linux GNOME file manager already works once the
   user picks "File Transfer" mode on the phone; document this as the
   supported flow rather than building a "sync now" button.
3. Do **not** build a "one-click sync" button backed by a programmatic
   USB library. See the effort analysis below — the cost is disproportionate
   to a transport this can already do manually for free.

### Why not write a from-scratch USB library covering all six cells?

Investigated and rejected. It would not produce one unified library — the
hard parts stay hard, and the easy parts are already free:

- **Windows MTP**: writing a raw-`libusb` MTP client doesn't remove the
  dependency on Windows Portable Devices (WPD) — Android MTP devices are
  claimed by WPD automatically, and `libusb` can only get at the device after
  the user manually reassigns its driver via Zadig/WinUSB, which
  simultaneously breaks Explorer/Photos' own access to the phone. Not
  shippable. Confirmed no prior art exists for driver-free raw MTP on
  Windows — Microsoft's own docs treat WPD as the only supported path.
- **macOS/Linux MTP**: this is the one piece actually worth writing from
  scratch — no competing OS driver stack claims the device — but it's
  already cheap without a custom library (Linux GNOME via `gvfs`; only macOS
  is a genuine gap, ~1-3 weeks of `libusb`-based MTP/PTP client work).
- **iOS AFC**: a real from-scratch client means reimplementing
  `usbmuxd` + `lockdownd` + the pairing/TLS handshake + AFC. Two paths:
  - Port `libimobiledevice`'s logic — faster, but that project is
    **LGPL-2.1-or-later**; translating its protocol logic into Flower's own
    code is a derivative work, not covered by LGPL's dynamic-linking
    exception, and needs real legal review before proceeding this way.
  - Clean-room reimplementation from protocol knowledge alone — slower,
    fewer licensing questions, higher bug risk.
  - Either way: **confirmed ongoing maintenance risk, not hypothetical** —
    iOS 17 broke `usbmuxd`/`lockdownd` pairing/trust behavior in
    `libimobiledevice`/`ifuse` within the last 2-3 years. Apple changes this
    stack periodically; a homegrown client inherits that indefinitely.

**Effort if built anyway:** Large — months for the iOS piece alone (initial
build + inevitable rework after Apple protocol changes), weeks for the
mac/Linux MTP piece, nothing gained on Windows. **Risk:** Medium-High,
concentrated entirely in the iOS side (protocol churn + licensing exposure).

**Verdict:** skip it. The pieces worth writing from scratch are the cheap
ones; the expensive, risky piece (iOS AFC) trades a stale-but-working
dependency for an indefinite maintenance burden with legal strings attached —
for a transport (cable-bound, manual Android mode-switch) that's strictly
worse than WiFi sync. Put the engineering effort into WiFi instead.

---

## 3. Bluetooth — dropped entirely

Third-party iOS apps have no supported path to bulk Bluetooth file transfer
with an arbitrary desktop computer — only `ExternalAccessory` (requires MFi
accessory certification, not applicable to a PC) or `CoreBluetooth` BLE,
which isn't a file-transfer protocol. Confirmed BLE real-world throughput is
~12-28 KB/s — a single 5 MB song would take 3-7 minutes.

Android Bluetooth Classic (RFCOMM) is faster (~1-3 MB/s) and reachable from
.NET-for-Android, and `32feet.NET`/`InTheHand.Net.Bluetooth` covers
Windows/macOS/Linux/Android/iOS in one API — but since iOS is blocked
outright, this can never be a unified cross-platform solution, and it's
~50-100x slower than WiFi even where it does work.

**Verdict:** not worth building. At most keep in mind for tiny payloads
(playlist diff, metadata) between two already-paired Android/desktop
devices — never as the transport for audio files, and not on iOS at all.

---

## Phase 3 — Full library sync and on-demand audio download

**Goal:** the piece Phase 2 explicitly deferred. Today a synced playlist can
only reference tracks already present on *both* devices — anything the peer
has that this device doesn't is silently dropped (see
`PlaylistSyncMapper.ResolveTracks`). This phase makes a peer's whole library
*known* everywhere (metadata only), and lets the user pull the actual audio
for any one track on demand — which is what the mobile "download" button
needs to show/act on.

**Protocol: OpenSubsonic, not a bespoke Flower format.** Per "The unifying
decision" above, library browsing and audio download don't need a
Flower-specific protocol any more than playlist metadata sync strictly did
— they need *a* protocol, and OpenSubsonic already is one, with a client
Flower needs to build anyway to talk to Navidrome/Jellyfin/a self-hosted
`Flower.Server`. Building it once and reusing it here means:
- `getIndexes`/`getArtists`/`getAlbumList`/`getSong` for library browsing.
- `stream`/`download` for audio download.
- The data model, trust gate, merge behavior, and mobile UI sections below
  are Flower-specific logic that sits on top of whichever wire protocol
  carries the bytes, unaffected by the protocol choice.
- **Still runs on today's `HttpListener`-based `SyncHttpServer`, not
  Kestrel** — either device (phone or desktop) can be the one holding a
  file (see `OriginDeviceFingerprint` below), and Kestrel isn't available on
  iOS/Android at all (see the "Server" note under WiFi/LAN sync above).
  Kestrel only enters the picture for the optional, desktop/NAS-only
  standalone `Flower.Server` further below — a different, later, optional
  component, not this phase.
- **Auth is still Flower's own trust/pairing gate below, not OpenSubsonic's
  username/token scheme** — a paired peer is already identified by
  fingerprint (see "Trust" below), so there's no separate Subsonic
  credential to manage between two Flower devices. The same client code
  handles both: real Subsonic auth when talking to a third-party server,
  fingerprint-based trust when talking to another Flower device.
- **`Track.SyncKey`-based matching is still required client-side** — an
  OpenSubsonic `getSong` id is only stable within one server's own scan, so
  matching "this song from peer X" against "do I already have this
  locally" still can't rely on the id alone across two independently-scanned
  libraries. This is the same reasoning the wire DTOs further below (in the
  first-party `Flower.Server` design) deliberately keep `Track.Path` out of
  the wire shape for too.

### Data model: a track can now be known without being local

`Track.Path` is already nullable, so the model needs no new field: a track
with `Path == null` is metadata Flower knows about (via sync) but whose
audio isn't on this device yet. `Track.SyncKey` (already built for playlist
sync) is exactly the right identity for this — no schema change beyond how
`Library` chooses to populate itself.

One addition is needed: something has to say *which device currently has
the file*, so a later download request goes to the right peer instead of
guessing. This is derived client-side (see "Protocol" below): a
`OriginDeviceFingerprint` (or equivalent) tracked per placeholder `Track`
locally, updated whenever a live copy is heard about closer at hand (see
"Merge behavior" below).

### Protocol: query each peer's own OpenSubsonic-shaped endpoints directly

Each currently-discovered peer is asked for its own catalog directly
(`getIndexes`/`getArtists`/`getAlbumList`/`getSong`), the same shape a real
Navidrome server would answer with, just served by today's
`HttpListener`-based `SyncHttpServer` instead of a third-party server:

- **No wire-level `OriginDeviceFingerprint` needed at all.** Since the
  client calls one specific peer's endpoint directly (rather than receiving
  one manifest that might describe third parties), it already knows which
  peer answered and stamps that as the origin locally when merging the
  response into its own `Library` - see "Merge behavior" below.
- **This also avoids any "multi-hop provenance" risk** (a device relaying
  what it heard about a track from a third device, secondhand): a real
  OpenSubsonic server - and Flower's own embedded one - only ever reports
  tracks it *actually has* locally, so there's nothing for one peer to relay
  on another's behalf. A device wanting the full known universe of tracks
  across all reachable peers queries each one directly instead of trusting
  any single manifest to describe third parties.
- **Audio download**: standard OpenSubsonic `stream`/`download` (by the
  responding peer's own internal song id). Matching which local `Track` a
  downloaded file corresponds to still goes through `Track.SyncKey`,
  computed client-side from the response's title/artist/album/duration
  fields - an OpenSubsonic song id is only stable within one peer's own
  scan, so it can't be used as the cross-device identity by itself.

### Trust: this is where plain-HTTP-with-no-auth stops being acceptable

Phase 1/2 deliberately deferred trust ("plain HTTP for now... defers TLS +
fingerprint trust to when file transfer is actually built" - see above).
That's now. Handing over playlist *names* to any device that happens to
mDNS-discover you is low stakes; handing over the audio files themselves
on request is not - add a pairing gate before this phase ships, not after:

- New `TrustedPeerStore` (`trusted-peers.json`, same shape as
  `DeviceIdentityStore`): a set of approved peer fingerprints.
- First contact from an unrecognized fingerprint on *any* of these
  OpenSubsonic-shaped endpoints (this reaches the existing `/api/flower/v1/*`
  playlist endpoints too, retroactively closing that same gap) surfaces an
  approve/deny prompt on the receiving device - same interaction shape as
  Bluetooth pairing or AirDrop's "Accept". Deny (or ignore) → `403`. This
  replaces OpenSubsonic's own username/token auth for peer-to-peer requests
  specifically - a paired fingerprint already establishes who's asking, so
  there's no separate Subsonic credential to manage between two Flower
  devices (real third-party servers still get real Subsonic auth from the
  same client code).
- Once approved, no further prompts for that fingerprint. Revoking is a
  manual "forget this device" action in Settings (new UI, small).
- Still plain HTTP, not HTTPS - `HttpListener`'s cross-platform HTTPS gap
  (`dotnet/runtime#19752`) hasn't changed. Trust here means *authorization*
  (who's allowed to ask), not *encryption in transit* - acceptable for a
  same-LAN transport where the threat model is "a stranger's phone also on
  this WiFi", not a hostile network path. Revisit if Flower ever syncs over
  anything other than a local network.

**Done**: `TrustedPeerStore` (`Flower/Persistence/`) persists approved
`(Fingerprint, Alias, ApprovedAt)` entries; denials are never persisted, so
an ignored/denied peer is simply re-prompted on its next request rather than
being remembered as blocked. `SyncHttpServer.AuthorizeAsync` gates every
`/api/flower/v1/*` path (`/api/localsend/v2/info` stays open, since a peer
has to learn our fingerprint via it before trust can even be evaluated);
the caller identifies itself via `X-Flower-Fingerprint`/`X-Flower-Alias`
headers, which `PlaylistSyncService` now sends on both the manifest GET and
the `/apply` POST. An unrecognized fingerprint raises
`PeerApprovalRequested`, forwarded through `MainViewModel` to `MainView`,
which reuses the existing generic `ConfirmDialogWindow` (Cancel - its
default/Escape action - doubles as deny) rather than a bespoke dialog.
Concurrent requests from the same not-yet-decided fingerprint share one
prompt; an unanswered prompt denies by default after 60s; no UI listening
(e.g. no `MainView` attached) also fails closed - unlike the playlist
conflict prompt's "keep local" default, granting sync access is a security
decision with no safe implicit default. Revoking is `TrustedDevicesWindow`
("Trusted Devices…" in the app menu, next to Rebuild Database/Open App Data
Location - kept out of the already-decluttered Settings dialog rather than
added back into it). Unit tested: `TrustedPeerStore` approve/revoke/replace
round-trips (`StoreRoundTripTests.cs`). Not yet built: the actual library
sync/merge logic and mobile download UI below - this was just the gate they
sit behind.

### Merge behavior

On receiving a peer's OpenSubsonic-shaped catalog response
(`getIndexes`/`getAlbumList`/`getSong`):

1. Match each entry against the local library by `SyncKey`.
2. Already present (local or placeholder) → no change, unless the peer's
   entry is backed by a real file on its end and the local copy is only a
   placeholder - then update the locally-tracked `OriginDeviceFingerprint`
   to this peer (a device shouldn't need the *original* source once it
   hears about a live copy closer at hand, e.g. its own desktop instead of
   the phone that first told it about the track).
3. Not present locally → insert a placeholder `Track` (`Path = null`,
   metadata copied from the response, `OriginDeviceFingerprint` set to
   whichever peer was just queried).
4. Never delete a local, `Path`-backed track because a peer doesn't mention
   it - the peer not having a track is not evidence this device shouldn't
   either.

This is symmetric/bidirectional and reuses the exact discovery-triggered
flow `PlaylistSyncService` already establishes (same initiator-election
rule, same "runs once per discovery event" trigger) - a new
`LibrarySyncService` alongside it, not a replacement.

**Immediate side benefit, no extra code:** `PlaylistSyncMapper.ResolveTracks`
already matches against "whatever's in the local library" - once that
library contains placeholders, a synced playlist referencing a
not-yet-downloaded track stops being silently dropped and instead resolves
to the placeholder, which the UI then renders exactly like any other
not-yet-downloaded row (see below). No change needed in
`PlaylistSyncMapper` itself.

### Mobile UI: the download button

Scope: mobile only for v1, per the ask - desktop typically has the spare
storage to not need this, and doesn't currently have any "not fully local"
track concept to render. (Extending the same treatment to desktop later is
just reusing the same `Track.Path == null` check in its own row template -
not a new mechanism.)

- A row for a `Path == null` track renders dimmed (reduced opacity, same
  idea as the currently-playing/disabled-state treatment elsewhere) with a
  download icon (`MaterialIconKind.Download` or similar) in place of
  whatever action affordance a normal row has.
- Tap behavior for such a row: only the download icon is actionable. Tapping
  elsewhere on the row (or attempting play) does nothing in v1 - once
  downloaded, the row becomes a normal, fully-interactive one automatically
  (it's the same `TrackRowViewModel`/binding, just re-evaluated once `Path`
  is set). No separate "downloading" data model needed beyond an
  in-progress flag the icon swaps to a spinner for.
- Download flow: resolve the peer via `OriginDeviceFingerprint` against
  currently-discovered devices (`NetworkDiscoveryService`). Not currently
  discovered (peer offline/out of range) → icon shows a disabled/"unavailable"
  state instead of doing nothing silently on tap.
  `GET http://{peer}/rest/stream?id=...` (standard OpenSubsonic - see
  "Protocol" above) → stream to a file in this platform's normal import
  location (iOS Documents folder / Android app-private storage - same
  places `Importer`/`AndroidMediaStoreImporter` already write to) → set
  `Track.Path` → persist via `LibraryStore.SaveAsync` → `Library.UpdateTracks`
  fires so every open view picks it up.
- A track can be added to (or already appear in) a playlist while still a
  placeholder - no gating needed there; it just shows greyed-out in that
  playlist too until downloaded, same as in Songs.

### Deliberately deferred, not designed now

- **Resumable/partial downloads.** A dropped WiFi connection mid-transfer
  means retry from scratch in v1. Real gap for a spotty connection, but
  needs a byte-range protocol addition - do this only if it turns out to
  matter in practice, not preemptively.
- **Multi-source download** (peer A is offline but peer B also has the
  track). v1 only ever asks the one origin recorded on the placeholder.
- **Batch actions** ("download this whole playlist/album", "download
  everything"). Natural follow-on once the single-track path works and is
  tested; building it first risks discovering the single-track plumbing
  was wrong in three places instead of one.
- **Auto-download-on-tap-to-play** (stream-or-fetch-then-play instead of a
  separate button). The ask was specifically a download button; keep the
  explicit affordance and revisit implicit fetch-on-play later if wanted.

**Effort:** Medium - reuses all of Phase 1/2's discovery, HTTP server, and
sync-session-orchestration machinery; the genuinely new pieces are the
trust/pairing gate, the audio-streaming endpoint, and the mobile row UI.
**Risk:** Low-Medium, concentrated in the trust gate (getting the
default-deny posture right matters more here than for metadata-only sync)
and in the mobile storage/import-path plumbing on each platform.

---

## Optional, additive: Jellyfin client support

Many self-hosters already run **Jellyfin** (C#/.NET, MIT-licensed official
`Jellyfin.Sdk` client — net8.0-compatible, actively maintained) for
movies/TV and would rather not run a second server just for music. Worth
adding as a second, optional backend once the Subsonic client exists:

- **No GPL concern**: `Jellyfin.Sdk` is a separate MIT repo from the GPLv2
  server. Flower would only be a network client talking HTTP to an
  independently-run Jellyfin process — classic client/server network use,
  not a derivative work.
- **Not a replacement for Subsonic support**: Jellyfin is video-first and
  ~5-10x heavier than Navidrome (300-800MB RAM idle vs. ~50MB). Community
  consensus is people who want *music only* prefer Navidrome; Jellyfin users
  use it for music because they already run it for other media. Treat this
  as "support the server our users already have," not the primary target.

## Optional, larger effort, later: first-party `Flower.Server`

For users who want a pure-Flower self-hosted server rather than running
Navidrome separately, or who've outgrown the embedded-in-Flower.Desktop
option above (stage 3 of the always-on staging - an always-on box that
isn't tied to a GUI session). Biggest lever here: **have `Flower.Server`
speak OpenSubsonic itself**, not a bespoke protocol — the client from above
then works against it with zero extra client work, *and* any of the many
existing polished third-party Subsonic mobile clients work with it for free
too.

**Recommended stack** (confirmed current best-practice, not stale advice):
- **ASP.NET Core Minimal API + Kestrel** — no IIS, cross-platform, matches
  what self-hosters run (Linux/Docker on NAS/Pi). Range-request streaming
  for seek/scrub needs no custom code: `Results.File(path, contentType,
  enableRangeProcessing: true)`, and Kestrel uses the OS `sendfile` syscall
  so bytes never enter managed memory.
- **SQLite**, single-file DB, no separate DB server — this is exactly what
  Navidrome itself does (closest prior art: single binary, TagLib-based
  scan, SQLite-only). Real gotcha to plan around: EF Core 7+'s SQLite save
  strategy no longer auto-retries on `SQLITE_BUSY`. For a background
  scanner writing while the API reads concurrently, need WAL journal mode
  (EF Core defaults to this on DB creation) + an explicit `busy_timeout` +
  `IDbContextFactory<T>` (one DbContext per request/job — DbContext itself
  isn't thread-safe), not a shared singleton context. WAL mode also requires
  the data directory to be on local storage, not NFS/SMB.
- **Auth**: single admin-set password + long-lived JWT/API tokens per
  client — no OAuth/SSO complexity, matching what Navidrome itself does for
  a small self-hosted single/few-user server.
- **Packaging**: multi-arch Docker image (amd64 + arm64 for Pi/NAS) via
  `dotnet publish -a $TARGETARCH` in a multi-stage Dockerfile, built with
  `docker buildx --platform linux/amd64,linux/arm64` (cross-compile, not
  emulate — much faster). Standard, well-documented pattern for self-hosted
  .NET apps.
- **No transcoding in v1** — stream original files with range support only
  (also Navidrome's default behavior). LibVLC-based server-side transcoding
  in a headless Linux container is a real can of worms (native lib
  packaging, no GPU, format-negotiation logic); revisit only if
  bandwidth-constrained mobile clients demand it later.

**Effort**: since Flower already has `Importer`/TagLib scanning and
`Track`/`Playlist`/`Library` models to adapt rather than build from zero, a
minimal v1 (scan → SQLite → OpenSubsonic-compatible REST endpoints →
single-admin auth → Docker image) is roughly **3-5 weeks** for one engineer
— most of the calendar time is EF Core schema/migrations and the SQLite
concurrency hardening above, not the streaming or Docker pieces, both of
which are fast to get right.

## Project structure: extracting a shared `Flower.Core` library

**This section describes work for whenever `Flower.Server` or stage 3 of
the always-on staging above is actually undertaken — not an early
prerequisite.** See "Sequencing" above for why the split is deferred rather
than done upfront. Kept here as the reference design for that later point,
so the decision doesn't need to be re-derived from scratch then.

`Flower.Server` cannot simply reference today's `Flower` project the way
`Flower.Desktop`/`Flower.Android`/`Flower.iOS` do — `Flower.csproj` pulls in
Avalonia, `LibVLCSharp`, `Material.Icons.Avalonia`, and every ViewModel/View
in the app. A headless ASP.NET Core server has no business linking a UI
framework and its own reference to `VideoLAN.LibVLC.*`, and it *shouldn't*
need to - most of what it actually wants (scanning, the domain model) has no
UI dependency today already. Pull that part out first, rather than
retrofitting it once `Flower.Server` already exists and everything is
tangled.

### What moves into `Flower.Core`

New project, plain `<TargetFrameworks>net10.0;net9.0</TargetFrameworks>` -
the same dual-targeting `Flower.csproj` already uses, for identical
compatibility with `Flower.iOS`'s `net9.0-ios18.0` TFM.

- **`Models/`** — `Track`, `Playlist`/`MainPlaylist`, `Library`. Checked:
  none of these reference Avalonia today (`Track` only uses `System`/
  `System.Text.Json`); this is a pure move, no rewrite.
- **`Importer/`** — `Importer`, `IMusicImporter`, `PlatformMusicImporter`.
  This is the single biggest concrete payoff: `Flower.Server`'s library
  scanner becomes "call the same `Importer.ImportAsync(paths)` desktop
  already calls," not a reimplementation. Brings `TagLibSharp` and
  `plist-cil` (for `TryResolveAppleMusicFolder`) along as `Flower.Core`
  package references.
- **The OpenSubsonic wire-contract module** (`OpenSubsonicContracts.cs` -
  DTOs for the `/rest/getSong`, `/rest/getAlbumList`, etc. JSON envelope) +
  pure `Track ↔ SubsonicSongDto` mapping functions. By this point it already
  exists in `Flower` (built well before any split existed - see
  "Sequencing" above) and used by both the OpenSubsonic client and
  Flower.Desktop's embedded host; this is a move of existing code, not new
  work. Sharing this three ways (client-side deserialize on desktop/mobile,
  server-side serialize in `Flower.Server`) means both ends agree on one set
  of field names by construction, not by convention. **Deliberately excludes
  `Track.Path`** from the DTO, same reasoning already applied to
  `PlaylistSyncTrackDto` in the playlist-sync Phase 2 above: a filesystem
  path means something different on every device (here, it would leak the
  *server's* local disk layout to any client), so the DTO carries a
  `streamUrl`/opaque id instead.

### What does *not* move, and why

- **`LibraryStore`/`PlaylistStore` (JSON persistence) stay in `Flower`.**
  `Flower.Server` doesn't use them - its own persistence is SQLite/EF Core
  (already decided above), because a JSON file that gets fully rewritten on
  every save (today's `LibraryStore.SaveAsync` behavior) doesn't hold up
  under a DB that needs incremental upserts and foreign keys from playlists
  to tracks. These two stores remain the client-only "local cache of
  whatever this device's library is" mechanism, whether populated by local
  import or by syncing against a remote server.
- **`AppSettingsStore`/`ColumnVisibilityStore`/`DeviceIdentityStore`/
  `PlaylistSyncStateStore` stay in `Flower`.** Window geometry, column
  widths, and P2P-sync device fingerprints are client concerns with no
  server analogue; `Flower.Server` gets its own config (admin password,
  scan paths, JWT signing key) via normal ASP.NET Core configuration, not
  by sharing these types.
- **`TrackListBuilder`/`TrackRowViewModel` stay in `Flower`.**
  `TrackRowViewModel.AlbumArt` is an `Avalonia.Media.Imaging.Bitmap` - not
  extractable without either splitting that class in two or teaching Core
  about bitmaps it has no business knowing about. The server doesn't render
  a track list, so it doesn't need this at all.
- **`SyncHttpServer`/`NetworkDiscoveryService`/`PlaylistSyncService` stay in
  `Flower`.** These implement the P2P WiFi sync above - a different feature
  from `Flower.Server` (mDNS-discovered peer, not a standing self-hosted
  server), and not something `Flower.Server` itself participates in.

### The `Track` reuse boundary, precisely

`Flower.Core.Track` is reused for **scanning** (the server calls the exact
same `Importer` desktop does against its own configured paths) but *not*
directly as the EF Core entity or the wire format:

```
disk files → Importer.ImportAsync() → List<Track>           (shared, Flower.Core)
                                          │
                                          ▼ (Flower.Server-internal upsert)
                                    SQLite via EF Core
                                    (Flower.Server's own TrackEntity)
                                          │
                                          ▼ (Flower.Server-internal mapping)
                                  SubsonicSongDto (shared shape, Flower.Core)
                                          │
                                          ▼ HTTP/JSON
                          OpenSubsonic client (Flower, desktop/mobile)
                                          │
                                          ▼ (client-internal mapping)
                                    Flower.Core.Track (back in the app's own Library)
```

Two deliberate seams, both justified by something already learned the hard
way elsewhere in this codebase: `TrackEntity` (server-internal) decouples
SQLite/EF Core migration churn from `Flower.Core`'s public model, the same
way the DB was already kept out of the shared library above; and
`SubsonicSongDto` (not `Track` serialized directly) is the same
Path-can't-cross-the-wire lesson `PlaylistSyncTrackDto` already encodes.

### Mechanical steps

1. `dotnet new classlib -n Flower.Core -f net10.0`, add `net9.0` to
   `TargetFrameworks`, add to `Flower.sln`.
2. Move `Models/`, `Importer/`, **and by this point the OpenSubsonic
   contracts + client too** (built directly in `Flower`, well before this
   extraction happens) from `Flower/` into `Flower.Core/` (`git mv`,
   preserving history); move the `TagLibSharp`/`plist-cil`
   `PackageReference`s down into `Flower.Core.csproj`.
3. `Flower.csproj` adds a `ProjectReference` to `Flower.Core.csproj`; fix
   `using` statements (namespaces can stay `Flower.Models`/
   `Flower.Importer` - moving a folder to a new project doesn't require a
   namespace rename, and one fewer diff to review).
4. Confirm `Flower.Tests` still passes unchanged (it references `Flower`,
   which now transitively pulls in `Flower.Core` - no test file changes
   expected, since nothing about the public API of `Track`/`Library`/
   `Importer` changes, only which `.csproj` compiles them).
5. Scaffold `Flower.Server` (`dotnet new webapi`), referencing
   `Flower.Core` for `Track`/`Importer`/the Subsonic contracts - all three
   already proven working (desktop's embedded host and the client have both
   been in real use well before this point), not new/untested code at this
   stage.

### Extending the importer abstraction (supersedes a stale reference)

`CROSS-PLATFORM-PLAN.md` item #3 describes an `IMusicSource` abstraction
that was the *proposal* at the time; what actually shipped is
`Flower.Importer.IMusicImporter` (desktop's `Importer`, Android's
`AndroidMediaStoreImporter` - see `MOBILE-PLAN.md`). Once `Flower.Core`
exists, add a `SubsonicLibraryImporter : IMusicImporter` there too (and
later `JellyfinLibraryImporter`) that returns the OpenSubsonic
client's synced `List<Track>` instead of scanning a local filesystem, so
"play from local files" and "play from a self-hosted server" are just two
`IMusicImporter` implementations selected in `App.axaml.cs`'s DI
registration, not a special-cased second code path.

## Mobile-specific note: streaming vs. background sync

Don't conflate these — they behave differently on iOS:
- **Active playback while streaming** is fine in the background. iOS
  supports background audio via a standard `AVAudioSession` background mode
  entitlement, independent of whatever killed our own embedded HTTP
  *listener* in the P2P WiFi plan above (that finding was about Flower
  running a server *inside* the app; here mobile Flower is a client making
  outbound requests to page in audio while playing, which is the normal
  "any streaming music app" case and works fine backgrounded).
- **Bulk library sync/download** (e.g. "download my whole library for
  offline use") is subject to the same foreground-execution constraints
  already noted above — a large download queue should be expected to pause
  when the app is fully backgrounded with nothing actively playing, same as
  most download-manager-style iOS apps.

---

## Suggested execution order

1. Update `CROSS-PLATFORM-PLAN.md` item #3 to drop `MPMediaLibrary` in favor
   of the private-file-library iOS importer (prerequisite for everything
   else here, and already blocking mobile import generally).
2. Build WiFi/LAN sync (mDNS discovery + LocalSend-protocol-style HTTP
   transfer) — the one genuinely new feature. **Done.**
3. Enable `UIFileSharingEnabled` on iOS and document the manual Finder/
   Explorer/GNOME drag-and-drop flow for USB — no new code beyond the
   Info.plist entry.
4. Do not build Bluetooth support or a programmatic USB transfer library.
5. Playlist metadata sync (Phase 2 above). **Done.**
6. Build the OpenSubsonic client + wire contracts **directly in `Flower`,
   no `Flower.Core` split yet** (browse, stream, playlist CRUD, search,
   cover art, star, scrobble) — small effort, unlocks self-hosting against
   Navidrome/Gonic/Airsonic-Advanced/Ampache immediately with no server
   work and no project restructuring. **Done.**
7. Phase 3 above (full library sync + on-demand audio download): the
   trust/pairing gate first (it retroactively closes a gap in the
   already-shipped playlist endpoints too, so it's worth doing even in
   isolation), then have Flower.Desktop host step 6's same OpenSubsonic
   contracts in-process, backed directly by its already-loaded `Library`
   (no database), then the merge logic, then the mobile download-button UI
   last (it's the only piece with nothing to build against until the rest
   exists).
8. Fold the client into the `IMusicImporter` abstraction alongside local
   import, so switching between "local library," "another Flower app on
   this network," and "a self-hosted server" is a settings choice, not a
   different app mode.
9. Add Jellyfin as a second optional `IMusicImporter` backend via
   `Jellyfin.Sdk` — cheap once the abstraction exists, real value for users
   who already run Jellyfin.
10. If/when there's concrete demand for either an always-on local daemon
    (stage 3 of the always-on staging above) or a standalone NAS/VPS
    `Flower.Server`: extract `Flower.Core` (`Models`/`Importer`, plus the
    OpenSubsonic contracts that already exist by then) per the "Project
    structure" section, then scaffold `Flower.Server` against it — biggest
    remaining effort, and still not urgent, since steps 6-9 already deliver
    both "sync with my own devices" and "connect to someone else's server"
    with no server code of Flower's own.
