# Desktop ↔ Phone Sync — Investigation & Plan

## Goal

Sync music (files + metadata) between the Flower desktop app (Windows/macOS/
Linux) and the Flower phone app (iOS/Android), across all six desktop×phone
permutations. Covers three candidate transports — USB cable, WiFi/LAN,
Bluetooth — and what it would take to build each.

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

### Data model: a track can now be known without being local

`Track.Path` is already nullable, so the model needs no new field: a track
with `Path == null` is metadata Flower knows about (via sync) but whose
audio isn't on this device yet. `Track.SyncKey` (already built for playlist
sync) is exactly the right identity for this — no schema change beyond how
`Library` chooses to populate itself.

One addition is needed: a track manifest entry has to say *which device
currently has the file*, so a later download request goes to the right
peer instead of guessing. Add `OriginDeviceFingerprint` to the wire DTO
(below) - set to the responding device's own fingerprint when a track is
`Path != null` on its end. A device re-broadcasting a track it only knows
about (`Path == null` locally too) passes through whatever origin it last
saw, not its own fingerprint - see "multi-hop" gap below.

### Protocol additions

New DTOs alongside `PlaylistSyncTrackDto` in `PlaylistSyncContracts.cs` (or
a new `LibrarySyncContracts.cs` - these are richer than the playlist ones,
which only carry enough to compute a `SyncKey`):

```csharp
public sealed record LibrarySyncTrackDto(
    string? Title, string? Subtitle, string? Artists, string? AlbumArtists,
    string? Album, string? Year, uint TrackNumber, uint DiscNumber,
    string? Genre, TimeSpan Duration, string OriginDeviceFingerprint);

public sealed record LibrarySyncManifestDto(string DeviceFingerprint, List<LibrarySyncTrackDto> Tracks);
```

Two new endpoints on `SyncHttpServer`, following the existing
`/api/flower/v1/...` convention:

- `GET /api/flower/v1/library` - this device's full track manifest (every
  track it knows about, whether local or itself only a placeholder).
- `GET /api/flower/v1/tracks/{syncKeyHash}/audio` - streams the raw audio
  bytes for one track this device has locally (404 if `Path == null` for
  that key on this end). `syncKeyHash` is a SHA-256 of `Track.SyncKey`
  rather than the raw key in the URL, since the key is
  `title|artists|album|durationSeconds` and any of those can contain `/` or
  other characters `HttpListener`'s route matching would mangle.

### Trust: this is where plain-HTTP-with-no-auth stops being acceptable

Phase 1/2 deliberately deferred trust ("plain HTTP for now... defers TLS +
fingerprint trust to when file transfer is actually built" - see above).
That's now. Handing over playlist *names* to any device that happens to
mDNS-discover you is low stakes; handing over the audio files themselves
on request is not - add a pairing gate before this phase ships, not after:

- New `TrustedPeerStore` (`trusted-peers.json`, same shape as
  `DeviceIdentityStore`): a set of approved peer fingerprints.
- First contact from an unrecognized fingerprint on *any*
  `/api/flower/v1/*` endpoint (this reaches the existing playlist endpoints
  too, retroactively closing that same gap) surfaces an approve/deny prompt
  on the receiving device - same interaction shape as Bluetooth pairing or
  AirDrop's "Accept". Deny (or ignore) → `403`.
- Once approved, no further prompts for that fingerprint. Revoking is a
  manual "forget this device" action in Settings (new UI, small).
- Still plain HTTP, not HTTPS - `HttpListener`'s cross-platform HTTPS gap
  (`dotnet/runtime#19752`) hasn't changed. Trust here means *authorization*
  (who's allowed to ask), not *encryption in transit* - acceptable for a
  same-LAN transport where the threat model is "a stranger's phone also on
  this WiFi", not a hostile network path. Revisit if Flower ever syncs over
  anything other than a local network.

### Merge behavior

On receiving a peer's `/api/flower/v1/library` manifest:

1. Match each entry against the local library by `SyncKey`.
2. Already present (local or placeholder) → no change, unless the incoming
   entry is `Path`-backed on the peer and the local copy is only a
   placeholder - then update `OriginDeviceFingerprint` to point at the peer
   that actually has it (a device shouldn't need the *original* source once
   it hears about a live copy closer at hand, e.g. its own desktop instead
   of the phone that first told it about the track).
3. Not present locally → insert a placeholder `Track` (`Path = null`,
   metadata copied from the DTO, `OriginDeviceFingerprint` from the DTO).
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
  `GET http://{peer}/api/flower/v1/tracks/{syncKeyHash}/audio` → stream to
  a file in this platform's normal import location (iOS Documents folder /
  Android app-private storage - same places `Importer`/
  `AndroidMediaStoreImporter` already write to) → set `Track.Path` → persist
  via `LibraryStore.SaveAsync` → `Library.UpdateTracks` fires so every open
  view picks it up.
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
- **Multi-hop provenance** (device C learned about a track from B, who
  learned it from A, who's since gone offline - does C's manifest still
  point at A?). v1's merge rule in step 2 above already re-points
  `OriginDeviceFingerprint` at whichever *reachable* device most recently
  claimed to have the file, which handles the common case; a device
  that's itself only a placeholder holder should not claim origin of a
  track it doesn't have, which the "passes through whatever origin it last
  saw" rule above already covers - flagged here as the one part of the
  merge logic most worth a second look once this is actually built and
  tested against three-plus devices, not just two.

**Effort:** Medium - reuses all of Phase 1/2's discovery, HTTP server, and
sync-session-orchestration machinery; the genuinely new pieces are the
trust/pairing gate, the audio-streaming endpoint, and the mobile row UI.
**Risk:** Low-Medium, concentrated in the trust gate (getting the
default-deny posture right matters more here than for metadata-only sync)
and in the mobile storage/import-path plumbing on each platform.

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
6. Phase 3 above: the trust/pairing gate first (it retroactively closes a
   gap in the already-shipped playlist endpoints too, so it's worth doing
   even in isolation), then the library-manifest endpoint + merge, then the
   audio-streaming endpoint, then the mobile download-button UI last (it's
   the only piece with nothing to build against until the rest exists).
