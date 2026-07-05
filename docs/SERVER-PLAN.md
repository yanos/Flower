# Self-Hosted Server Sync — Investigation & Plan

## Goal

Support a "self-hosting" model: a server (run by the user, on their own NAS/
home box/VPS) holds a canonical music library, and Flower desktop/mobile
apps sync/stream against it — over LAN or over the internet if the user
exposes it. This is a different feature from the P2P WiFi sync in
`SYNC-PLAN.md` (no central server, desktop↔phone directly) — the two are
complementary: WiFi sync needs nothing but two Flower apps on the same
network; server sync needs a server to be running somewhere, but then works
from anywhere, and is the natural fit for a large library one device can't
hold a full local copy of.

## Recommendation: don't invent a sync protocol — become a Subsonic/OpenSubsonic client first

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
  WIP) are unusable. Not a blocker: this is a thin REST+JSON API. Hand-roll
  a typed client in `Flower.Core` (see "Project structure" below) — auth
  token generation, ~10-15
  endpoints (browse, stream, playlist CRUD, search, cover art, star,
  scrobble), DTOs for the JSON envelope. **Small-medium effort, days not
  weeks**, and it's the same client code on desktop and mobile since it's
  just HTTP.

## Phase 2 (optional, additive): Jellyfin client support

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

## Phase 3 (optional, larger effort): first-party `Flower.Server`

For users who want a pure-Flower self-hosted server rather than running
Navidrome separately. Biggest lever here: **have `Flower.Server` speak
OpenSubsonic itself**, not a bespoke protocol — Flower's own client code
from Phase 1 then works against it with zero extra client work, *and* any of
the many existing polished third-party Subsonic mobile clients work with it
for free too.

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
- **A new OpenSubsonic wire-contract module** (`OpenSubsonicContracts.cs` -
  DTOs for the `/rest/getSong`, `/rest/getAlbumList`, etc. JSON envelope) +
  pure `Track ↔ SubsonicSongDto` mapping functions, built as part of this
  work rather than extracted from existing code (there's no Subsonic client
  yet - Phase 1 above). Sharing this three ways (client-side deserialize on
  desktop/mobile, server-side serialize in `Flower.Server`) means both ends
  agree on one set of field names by construction, not by convention.
  **Deliberately excludes `Track.Path`** from the DTO, same reasoning
  already applied to `PlaylistSyncTrackDto` in `SYNC-PLAN.md`'s Phase 2: a
  filesystem path means something different on every device (here, it would
  leak the *server's* local disk layout to any client), so the DTO carries
  a `streamUrl`/opaque id instead.

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
  `Flower`.** These implement the P2P WiFi sync from `SYNC-PLAN.md` - a
  different feature from `Flower.Server` (mDNS-discovered peer, not a
  standing self-hosted server), and not something `Flower.Server` itself
  participates in.

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
2. Move `Models/`, `Importer/` from `Flower/` into `Flower.Core/` (`git mv`,
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
5. Build the OpenSubsonic contracts + client (Phase 1) directly against
   `Flower.Core`, so it exists before `Flower.Server` does and desktop/
   mobile can already talk to Navidrome while `Flower.Server` is still
   being built.
6. Only then scaffold `Flower.Server` (`dotnet new webapi`), referencing
   `Flower.Core` for `Track`/`Importer`/the Subsonic contracts.

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
  *listener* in the P2P WiFi plan (that finding was about Flower running a
  server *inside* the app; here mobile Flower is a client making outbound
  requests to page in audio while playing, which is the normal "any
  streaming music app" case and works fine backgrounded).
- **Bulk library sync/download** (e.g. "download my whole library for
  offline use") is subject to the same foreground-execution constraints
  already noted in `SYNC-PLAN.md` — a large download queue should be
  expected to pause when the app is fully backgrounded with nothing
  actively playing, same as most download-manager-style iOS apps.

## Suggested execution order

1. Extract `Flower.Core` (`Models`/`Importer` only) out of `Flower` -
   prerequisite for everything below, and a pure mechanical move with no
   behavior change (step 4 of the project-structure section above is the
   regression check: `Flower.Tests` passes unchanged).
2. Build the OpenSubsonic client + wire contracts against `Flower.Core`
   (browse, stream, playlist CRUD, search, cover art, star, scrobble) —
   small effort, unlocks self-hosting against
   Navidrome/Gonic/Airsonic-Advanced/Ampache immediately with no server work.
3. Fold it into the `IMusicImporter` abstraction alongside local import, so
   switching between "local library" and "server library" is a settings
   choice, not a different app mode.
4. Add Jellyfin as a second optional `IMusicImporter` backend via
   `Jellyfin.Sdk` — cheap once the abstraction exists, real value for users
   who already run Jellyfin.
5. Only then scaffold `Flower.Server` (Phase 3) — biggest effort, and
   explicitly speak OpenSubsonic from it so step 2's client code and
   third-party Subsonic apps both work against it with no extra effort.
