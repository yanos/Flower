# Architecture Review — Findings and Remediation

Whole-codebase review (August 2026) of structure, class design, data structures, algorithms, performance, latent bugs, duplicated sources of truth, and test coverage — read against the roadmap in the other `docs/*.md` files and `todo.txt`.

**Status: Tier 0 implemented. Tier 1 implemented except two deferred 1.5 items. Tier 2.1, 2.2, 2.4 and 2.5 implemented, 2.6 half. Tier 3 implemented. Tier 4.4 implemented. Tier 5 implemented, bar two unreachable-from-a-test corners of 5.6. The rest of Tier 2 and Tier 4 documented, not started.** Unlike the other plan docs, this one is a standing backlog rather than a single initiative — each tier below records its own state, and items should be struck off here as they land rather than moved elsewhere.

## Scale reality check

Measured against the real 16k-track development library, not estimated:

| Fact | Value |
|---|---|
| `library.json` | **17.9 MB**, 16,116 tracks, `WriteIndented = true` — since Tier 1.1, unindented and null-omitting |
| Rewritten in full | was on **every track start** and **every track end**; since Tier 1.1, coalesced behind a 3s debounce |
| `Flower.Server` test coverage | was zero; 70 tests as of Tier 2.1 |
| Event unsubscriptions (`-=`) in `Flower/ViewModels` + `Flower/Services` | 0 |
| Tests at review time | 824 — 754 in `Flower.Tests` (fast filter), 70 in `Flower.Server.Tests` (629 before Tier 5 was finished; 393 before Tier 1, 461 before Tier 3, 478 before Tier 5.2, 500 before Tier 1.4, 510 before Tier 2.1, 524 before Tier 4.4, 545 before Tier 2.2, 568 before Tier 2.4, 579 before Tier 5.3) |

These numbers matter because most findings below are invisible at the ~100-track scale a synthetic test library operates at.

---

## What works — do not disturb

- **Play-count merge is a correct G-Counter CRDT.** `Track.RemotePlayCounts` keyed by device fingerprint, merged per-key by `Math.Max` (`Library.MergeRemotePlayCounts`), with `LibrarySyncMapper.ToPlaceholderTrack` stripping the receiver's own fingerprint out of inbound reports. Idempotent, order-independent, multi-hop safe.
- **The Phase-4 request signing scheme** (`SYNC-PLAN.md`): ECDSA P-256 proof-of-possession over canonical method+path+query+body-hash+timestamp+nonce, ±60s skew, per-fingerprint replay guard. `SignatureVerifier.Verify` burns the nonce *before* verifying, so a forged attempt can't be retried.
- **`Library._lock`** around every `Tracks` read-modify-write, and the resolve-then-mutate-under-lock pattern in `IncrementPlayCount`/`RecordPlayed`.
- **The gapless pipeline's two-LibVLC-core split** and the ring-buffer-*read*-derived position counter. Both were hard-won; the write-ups in `CLAUDE.md` are accurate, and the code matches them.
- **`Flower.Core`'s security primitives** — `RateLimiter`, `SignatureVerifier`, `SignedRequestCanonicalizer`, `NonceReplayGuard`, `LanGuard`, `DeviceSigningKey` are genuinely shared between client and server. That part of the extraction worked.
- **Server concurrency choices**: SQLite WAL + `Default Timeout=30` + `IDbContextFactory<T>` per request.
- **Test *style*** where it exists: `TestSupport/` fakes, synthetic WAV fixtures, `Avalonia.Headless` integration tests, `PlatformDataDirectory` pinning. The patterns are right; there just isn't enough of it in the right places.

---

## Tier 0 — data-loss and correctness bugs — DONE

### 0.1 Non-atomic writes over the only copy of irreplaceable data

Every store wrote straight over the live file with `File.WriteAllText`/`WriteAllTextAsync` — no temp+rename, no backup. A crash or forced quit mid-write truncated the file; `LibraryStore.Load` then caught, logged a warning, and returned an **empty list**, after which the startup rescan repopulated from disk with `DateAdded = now` and `PlayCount = 0`. Net effect: every play count, first-seen date, last-played timestamp and remote-device play count silently and permanently destroyed, and "Recently Added" showing the whole library. None of that data exists anywhere else. `DeviceKeyStore` had the same shape with a worse consequence — a corrupt load regenerated the keypair, permanently breaking trust with every paired peer.

**Fixed** by `Flower.Core/Persistence/AtomicJsonFile.cs`: write to `<name>.tmp`, flush to disk, `File.Replace(tmp, target, target + ".bak")`; on read, fall back to `.bak` (writing the recovered contents back), and quarantine an unreadable file to `.corrupt` rather than silently starting empty. Every store routes through it and holds a write lock — `AppSettingsStore` in particular had none, while `ColumnManager`'s save fires on every pixel of a column-resize drag. `LibraryStore.SaveAsync` also now serializes *inside* the lock, closing a lost-update window where an earlier-queued save carrying a stale snapshot could physically land last.

**Still open:** an unrecoverable file is reported by logging at `Error` (visible in the in-app Log window) rather than a UI banner.

### 0.2 `Track` was a `record` with 40 mutable properties, and value equality was being used as identity

Records synthesize value-based `Equals`/`GetHashCode` over every property. That synthesized equality was what `Playlist.GetNextTrack`/`GetPreviousTrack`/`RemoveTrack`, `PlaylistControlViewModel`'s shuffle re-roll, the Track Info navigation in `MainView`/`AlbumGridRowControl`, and `Library.MergeSyncedTracks`' `HashSet<Track>` all actually used.

Consequences, all real: two tracks with identical tags (untagged rips, "Track 01", CD silence tracks) were indistinguishable to all of those, so next/previous/remove silently acted on whichever came first; every `IndexOf` cost O(n × 40 field comparisons) on a 16k queue; and mutable fields in hash-based collections is a bug the next `Track`-keyed cache would have inherited. `Track` was also `record`-in-name-only — nothing used `with` except two deliberate copies, and every mutation was in place.

**Fixed:** `Track` gained a stable `Guid Id` minted at construction and carried forward across rescans by `Library.CarryForwardMutableState`, became a `sealed class` implementing `IEquatable<Track>` on `Id` alone, and every consumer of the old equality moved to id-based lookup. The two `with` expressions became `Track.Clone()`, which keeps `Id` — that also fixed a bug of its own: the stream-URL copy handed to the audio manager for a not-yet-downloaded track used to compare unequal to the queued placeholder (different `Path`), so `Playlist.GetNextTrack` returned -1 and auto-advance jumped back to the front of the queue.

**Still open:** adding the *same* track to a playlist twice puts one instance in the list twice, so `IndexOf` still resolves to the first occurrence. Telling those apart needs the queue to track a position rather than a track — a `PlaylistControlViewModel` change, not a model one.

### 0.3 Playlists held orphaned `Track` instances after every rescan

`PlaylistStore.Load(library.Tracks)` resolved playlist membership to `Track` object references once, at startup. The startup rescan then replaced `Library.Tracks` with brand-new instances and nothing re-resolved playlists — `TracksUpdated` had exactly two subscribers, neither touching `Library.Playlists`. For the whole session after the first rescan a playlist's tracks were a different object graph from the library's, so play counts incremented on one never appeared in the other. Separately, `PlaylistStore.SaveAsync` persisted only `Path != null` tracks, so **a synced-but-not-downloaded track added to a playlist was silently dropped on save**.

**Fixed:** playlists are re-resolved inside `UpdateTracks` under the lock, keyed by the new stable `Id`; membership persists by `Id` alone (the `Path` fallback was a migration shim, removed in Tier 2.5); placeholder tracks survive.

### 0.4 `PlaylistSyncPlanner` silently resolved delete-vs-edit in favour of delete

Both single-sided branches decided `Delete` whenever a baseline existed, without checking whether the *surviving* side had also changed since that baseline — even though the edit-vs-edit branch twenty lines below did exactly that comparison. A user who edited a playlist offline while the peer deleted it lost the edits with no conflict prompt.

**Fixed:** both branches now compare against the baseline and classify it as `Conflict` when the surviving side also changed.

**Partially open:** the existing conflict prompt is a two-column "yours vs. theirs" dialog, and a delete-vs-edit conflict only has one side to show, so `PlaylistSyncService.ResolveConflictAsync` resolves this shape without asking — in favour of the surviving edit. That converges (the merge is pushed straight back to the peer's `/apply`) and never loses work, but a playlist the user deliberately deleted on one device can come back. A real "they deleted this — keep it?" prompt needs its own UI on both desktop and mobile.

### 0.5 `ColumnManager`'s debounce did not debounce — fixed with a real `CancellationTokenSource`

`ScheduleSave()` was `_pendingSave = SaveAsync();` — a new unawaited `Task.Delay(500)` chain per call, never cancelling the previous, with `_pendingSave` never read. Column `Width` changes call it too, so a resize drag spawned dozens of concurrent saves against an `AppSettingsStore` that had no write lock — the same failure mode `LibraryStore` had already added a `SemaphoreSlim` for.

### 0.6 Descending sort reversed secondary keys — fixed with `OrderByDescending` on the primary key only

`TrackListBuilder.Sort` ended with `asc ? ordered : ordered.Reverse()`. `Enumerable.Reverse` reverses ties too, so sorting by Album descending also reversed disc/track order *within* each album.

### 0.7 Native LibVLC handles leaked on every track change

`ITrackDecoder : IDisposable`, and `TrackDecoder.Dispose()` is what disposes `_media`/`_mediaPlayer`/`_watchdog` — but `Dispose()` was never called anywhere in the pipeline. `GaplessCoordinator` only ever called `Retire()`, which stops the watchdog and offloads `MediaPlayer.Stop()` to a detached `Task.Run`, then returns without disposing. Every track change constructs a fresh decoder, so handles accumulated for the life of the process.

**Fixed:** `Retire()` now disposes the watchdog, `Media` and `MediaPlayer` in its detached task, after `Stop()` returns (`Stop()` is the call that can hang, which is why it was detached in the first place), and `Dispose()` is just the `IDisposable` spelling of `Retire()`. A `SemaphoreSlim` serializes disposal against `PrepareAsync`/`StartDecoding`, since the coordinator can retire a decoder whose `PrepareAsync` is still in flight and disposing a native `Media` mid-`Parse` is a crash, not an exception. Covered by the existing real-LibVLC decode tests.

### 0.8 A leaked 60 fps `DispatcherTimer` per downloading row — fixed by disposing outgoing rows

`TrackRowViewModel.StartSpin()` creates a 16 ms timer; `StopSpin` ran only from the `IsDownloading` setter. `Rows` is replaced wholesale on every rescan, search and track change, so any row spinning at that moment was dropped with its timer still registered — keeping the view-model alive and ticking at 60 fps forever.

### 0.9 `ScheduleContentSync` ran off the UI thread over a collection the UI mutates — fixed by marshalling the whole handler

`MainViewModel`'s `TracksUpdated` handler marshalled `PopulateTracks` but called `ScheduleContentSync()` on the raising thread — which can be a LibVLC callback thread, via `EndReached` → `NotifyTrackChanged`. Its continuation then LINQ'd over `_sidebarItems`, a plain `ObservableCollection<SidebarItem>` the UI thread mutates elsewhere.

### 0.10 Decode failure was indistinguishable from end-of-track

`GaplessCoordinator` funnelled `Faulted` and `Drained` into the same handler, so a corrupt or unsupported file mid-playlist was treated as a natural end: it skipped silently and picked up a play count on the way past.

**Fixed:** the coordinator raises a new `TrackFailed` instead of `EndReached` on a fault (the advance behaviour is unchanged), `IAudioManager.TrackFailed` carries it up, and `PlaylistControlViewModel` skips to the next track without counting a play or stamping `LastPlayedAt` — and never retries the broken file even with repeat on. This is also the seam `AUDIOPHILE-PLAN.md` §3 needs for its "unsupported format" messaging.

**Still open:** `PlaylistControlViewModel.PlaybackFailed` is raised for the UI but nothing consumes it yet, so today a failed track shows up in the Log window and nowhere else.

---

## Tier 1 — performance — DONE (two 1.5 items deferred)

### 1.1 A full 17.9 MB library serialization on every track change — DONE

`PlaylistControlViewModel.Play` does `RecordPlayed` → `NotifyTrackChanged()` → fire-and-forget `SaveAsync(_library.Tracks)`; the `EndReached` handler does the same again. Per song change that is:

1. **2× full serialize + write of 17.9 MB** of indented JSON, roughly 60% of which is `null` fields spelled out in full.
2. **2× `TracksUpdated`** → `PopulateTracks()`: a 16k-element list copy, sub-list rebuild, album-grid rebuild (full `GroupBy` over 16k tracks), then a debounced full filter+sort+**16k `TrackRowViewModel` allocations**.
3. **2× `ScheduleContentSync()`** — a play-count bump is indistinguishable from a library change, so **playing a song triggers a network sync with the paired peer**.

There is also a lost-update race despite the semaphore: `SaveAsync` takes its snapshot as a parameter and serializes *before* acquiring the write lock, so an earlier-queued call carrying stale data can physically land last.

The lost-update race was already closed in Tier 0 (`SaveAsync` now serializes inside the lock).

**Fixed** on three fronts. `FlowerJsonContext` sets `WriteIndented = false` and `DefaultIgnoreCondition = WhenWritingNull`, safe because that context covers only formats Flower controls both ends of — a reader of a missing property gets the same default an explicit null would have given. `LibraryStore.ScheduleSave` coalesces the write-per-event behind a 3s debounce, with `Flush()` for the app-exit path and `Save()` superseding anything still pending, so quitting inside the window can't lose the increment that opened it. And `Library` now raises a distinct `TrackStatsChanged` carrying the mutated track: `PlaylistControlViewModel`'s `Play`/`EndReached` no longer call `NotifyTrackChanged` at all, so a play no longer rebuilds 16k rows or schedules a peer sync — `MainViewModel` re-raises the two affected columns on the one affected row instead (`TrackRowViewModel.NotifyStatsChanged`).

**Still open:** the real fix named above — splitting mutable per-track state out of the metadata blob so a play-count bump writes ~100 bytes instead of the whole library. That is Tier 4.1's SQLite work, and everything here is on the JSON layer beneath it.

### 1.2 Album art is decoded at full source resolution — DONE

`AlbumArtLoader.LoadLocalBitmap` does `new Bitmap(ms)` with no downscale. Modern embedded art is routinely 1400×1400+ → ~7.8 MB decoded RGBA per bitmap, for tiles rendered at ~120 px. An album grid showing 40 tiles can hold 300 MB of decoded bitmaps; the `WeakReference` cache softens it, but iOS jetsam will not wait for a GC. 

**Fixed** by `AlbumArtLoader.DecodeScaled`, capping decodes at 768px — mobile `NowPlayingView`'s 280pt square at ~2.7x DPI, the widest this app actually paints, and one cache serves every call site so the size has to satisfy the largest. It never scales *up*: applying `DecodeToWidth` unconditionally would inflate a 300px cover into a 768-wide bitmap and make the problem worse for libraries with modest art, and SkiaSharp (so `SKCodec`) isn't a reference of this project, so there is no cheap way to read the intrinsic size first — oversized art is decoded twice, once to learn its size and once at the size wanted. The full-size decode is transient; what used to be *retained* per album is what mattered. A 32-entry strong-referenced LRU now keeps the visible set alive across collections, and `Retain` prunes dead `WeakReference` entries, which previously accumulated one per album ever displayed for the life of the process.

### 1.3 `Flower.Server` materializes the entire `Tracks` table on every browse request — DONE

`GetAlbumList2` is `(await db.Tracks.ToListAsync()).GroupBy(t => t.AlbumId)`. `GetArtists` does the same with a projection. `Search3` pulls all SQL-side matches then re-filters in memory with *different* case semantics than the SQL `LIKE`, so the two passes genuinely disagree. Every one is a full table scan + full materialization + in-memory group/sort/paginate, per request, with no caching — against the 16k-track library `SYNC-PLAN.md` names as the target scale. The indices on `Path`/`ArtistId`/`AlbumId` exist but these query shapes never exercise them.

**Fixed.** `GetAlbumList2` and `Search3` share an `AlbumSummaries` projection that groups and aggregates in SQL and paginates there too; `GetArtists` collapses to one row per distinct (artist, album) pair SQL-side (~1.4k rows rather than ~16k) and counts in memory; `Search3` is now three individually-limited SQL queries with the disagreeing in-memory re-filter removed, so one filter decides matching and the `Take` happens in the database.

Two things only a real run would have caught, and there is still no `Flower.Server` test project to catch them (Tier 5.1): SQLite refuses `Max()` over a `DateTimeOffset`, so `type=newest` — which orders by each album's most recent `DateAdded` — aggregates client-side over a two-column projection instead, pending the value converter that belongs with Tier 4.1's schema work; and EF Core can only translate a grouped aggregate projection written as a **member initializer**, never a constructor call, so `AlbumSummary` is an init-property class rather than the positional record it started as (which compiled fine and threw at request time).

Verified against the real 16,116-track library, all five `getAlbumList2` sort types plus `search3` and `getArtists`, zero server-side exceptions. Warm timings, before → after: `getAlbumList2` 187ms → 29ms, `search3` 172ms → 31ms, `getArtists` 37ms → 33ms.

### 1.4 Sync transfers the whole manifest every time, and re-hashes all album art to build it — DONE

`GET /api/flower/v1/library` returns the complete catalog (6-8 MB at 16k tracks by `SYNC-PLAN.md`'s own estimate) with no ETag, version, or `If-Modified-Since`, and is re-pulled on the 5s-debounced local-change path — so editing one playlist track re-downloads the peer's entire library manifest. Building that manifest calls `ComputeAlbumArtHash` once per album, each opening the file with TagLib and SHA-256'ing the art bytes: ~1,400 file opens and hashes per request, uncached.

Meanwhile a *server-side* change is never noticed while both apps stay running — sync fires only on first mDNS contact or a debounced **local** change, and the 5s `/info` poll checks reachability/trust/alias only. This is `todo.txt`'s "push library sync events instead of polling", and it is a correctness gap, not an optimization.

**Resolution.** `Library.ChangeToken` — a session id plus a mutation counter, bumped by every mutation the manifest can see (the list itself, a `Path`, a play count, a `LastPlayedAt`) — is now the currency for all three problems:

- **Conditional pull.** `GET /api/flower/v1/library` serves the token as its `ETag` and answers `304` to a matching `If-None-Match`. `LibrarySyncService` remembers the token per peer and sends it back, so an unchanged catalog costs one 304 instead of 6-8 MB. It records the token only after the merge *and* the save both succeed — remembering it earlier would mean a device that failed to persist gets 304'd for content it never stored. A 304 returns `LibrarySyncResult.Unchanged`, deliberately distinct from an empty manifest: merging empty would prune every placeholder the peer ever taught this device about, so conflating the two is data loss, not a missed optimization.
- **Manifest build cost.** The serialized manifest is cached alongside the token it was built from, so several peers missing the cache at once still build it once. `ComputeAlbumArtHash` is separately memoized on path + last-write-time + length, which covers the OpenSubsonic browse endpoints (per-request by nature) and self-invalidates when a file is re-tagged.
- **The correctness gap.** `/info` now advertises the same token, and `DiscoveredDevice.LibraryToken` carries it into `MainViewModel.TriggerSyncIfPeerCatalogChanged`. The ~5s poll every Client already runs therefore notices a *server-side* change promptly — no new endpoint, no long-lived connection to keep alive on mobile. A redundant trigger is cheap by construction, since the pull it starts is itself conditional.

Why a session id rather than a bare counter or a content hash: a bare counter collides across a restart, letting a peer holding "7" see a different catalog that has also reached "7" and conclude nothing changed — the one failure mode that actually loses data. A content hash would avoid even the one redundant pull a restart causes, but computing it means building the whole manifest, which is the work this exists to skip, and `/info` is polled every ~5s per peer.

**Verification:** `SyncHttpServerRoundTripTests` (ETag matches `ChangeToken`, 304 with an empty body, a changed catalog invalidating a stale token, `/info` advertising the same token and moving it when the library changes with no request from the peer), `LibrarySyncConditionalPullTests` (no condition on the first pull then the served token on the second, a 304 leaving every placeholder alone, a failed pull not poisoning the next one), and `LibraryTests`' `ChangeToken` cases (stable while idle, moves for every manifest-visible mutation including in-place `NotifyTrackChanged`, never shared between two libraries with identical contents).

### 1.5 Repeated O(n) passes with no incrementality — MOSTLY DONE

- **Done** — `Track.SyncKey` is cached, invalidated by the setters of the four fields it derives from (`Title`/`Artists`/`Album`/`Duration`, now backed properties), so a tag edit still takes effect.
- **Done** — `Library` keeps a lazily-built path index. Built lazily and invalidated rather than maintained incrementally, because `Path` is not immutable: `LibraryDownloadService` sets it on a placeholder in place, so `NotifyTrackChanged` invalidates too.
- **Partly done** — `TrackListBuilder.SortKey` now counts then fills exactly once via `string.Create`, and returns the input untouched when there is nothing to strip (allocating nothing at all). `Build` still reallocates all 16k row view-models per rebuild rather than diffing — **deferred**, see below.
- **Done** — `TrustedPeerStore` caches both lists in memory, invalidated by its own writes. The only way to observe a stale value is editing `trusted-peers.json` under a running app, which was never supported.
- **Done** — `MusicListPanel` precomputes a group-leader index array in `SetItems`, making the per-scroll cost O(viewport).
- **Done** — `RebuildRowsAsync` derives `buildGrids` from `IsShowingAlbumGrid`/`IsShowingRecentlyAddedGrid`, so the two tile grids are built only for the views that paint them. Switching to either runs `OnSidebarSelectionChanged`, which rebuilds through here again, so they are always ready before the view appears.
- **Mitigated, not fixed** — 1.2's strong LRU means the re-triggered loads now hit a live cache instead of re-decoding, but the `Task.Run` per row still happens. Properly fixing it means row diffing — **deferred**, same as `Build` above.
- **Done** — `NotifyPairButtonPropertiesChanged` diffs against the last-notified tuple and returns early when nothing changed, which is every firing of the 5s peer poll.
- **Not started** — the shared animation clock. At least eight timers run app-wide, four at 60 Hz (busy spinner, per-row download spinner, swipe easing, scroll-into-view), so a batch download still runs one dispatcher timer *per row*.

**Deferred deliberately:** row diffing in `TrackListBuilder.Build` (and the `AlbumArt` discard that rides on it). It is the largest remaining Tier 1 item and the only one that needs real structural change to a hot, well-covered path rather than a local fix; the cheaper wins above land first precisely so its benefit can be measured against them.

---

## Tier 2 — structural: multiple sources of truth — DONE (2.3 all but the Views-layer service location, deferred to 4.2)

### 2.1 Four track models and five identity schemes — DONE

| Layer | Model | Identity |
|---|---|---|
| Client domain | `Flower.Core.Models.Track` | `Id` (as of Tier 0), previously `Path` |
| Cross-device sync | same `Track` | `SyncKey` = normalized Title\|Artist\|Album\|rounded seconds |
| In-memory UI navigation | same `Track` | was **accidental** full-record value equality — fixed in Tier 0 |
| Wire (P2P + Subsonic) | `Child`/`AlbumID3` DTOs, `PlaylistSyncTrackDto` | `Track.Id` per song; `al-{hash}` per album |
| Server | `Flower.Server.Data.TrackEntity` | row id per song; the same `al-{hash}` per album |

The two album-ID schemes differ by a punctuation character and nothing enforces the distinction (`LibraryOpenSubsonicMapper.AlbumId` vs `SubsonicIdentity.AlbumId`). `SubsonicMapper.ToChild` re-implements duration rounding inline as `Math.Round(t.DurationSeconds)` instead of calling `Track.RoundedSeconds` — exactly the bug class `Track.RoundedSeconds`' own doc comment records having already been hit and fixed once. `TrackEntity` has a `Starred` column the client `Track` has no concept of.

**Direction:** `Track.Id` is now the single identity; demote `SyncKey` to a *matching heuristic* used at import and first pairing, not an identity. Move the canonical `(artist, album) → id` function and the DTO mapping into `Flower.Core` so client and server share one implementation.

**Done:** the id function and the duration rounding are now single implementations. `Flower.Core/Services/SubsonicIdentity.cs` is the only `(albumArtist, album) → id` in the codebase, used by the standalone server (`LibraryImportService`) and the embedded host (`LibraryOpenSubsonicMapper`) alike; the client's plain-text `al:{album}|{artist}` form is gone in favour of the hashed one, which is opaque and can't collide when an album name contains the separator. `SubsonicMapper.ToChild` calls `Track.RoundedSeconds` instead of re-implementing it.

Unifying them **surfaced a live bug**: `AlbumArtLoader`'s remote-fetch path and `SyncHttpServer.HandleGetCoverArtAsync` both derived the album id from the per-track `Artists`, while the manifest that published those ids grouped by `EffectiveAlbumArtist`. Remote cover art therefore 404'd for every album with an `AlbumArtists` tag or a compilation flag — silently, since a missing cover just renders as no art. Both now go through `LibraryOpenSubsonicMapper.AlbumIdFor(track)`, and a song's `ArtistId` is the album artist too, so it points at an artist the album listing actually mentions. Covered by `LibraryOpenSubsonicMapperTests` (compilation ids agree and match what `BuildAlbumList` publishes; ids are opaque and normalized) and `Flower.Server.Tests`' `IdentityParityTests` (rounding parity, including that both sides inherit `Math.Round`'s banker's rounding at an exact .5 because they call the same method).

**`Child.Id` is now the origin track's `Track.Id`**, not its `SyncKey`. `SyncKey` is derived from tags and a rounded duration, so serving it as the song id meant a tag edit on the serving device silently invalidated every id a peer was still holding — the peer's next stream or download request 404'd, indistinguishable from the peer being offline. The receiving side stores what the peer actually said, verbatim, as `Track.OriginTrackId` (carried across rescans by `UpdateTracks` and refreshed by `MergeSyncedTracks`, same as the rest of the origin metadata), and hands it straight back on `/rest/stream` and `/rest/download` instead of recomputing a `SyncKey` and hoping the far side lands on the same string. That is also what the OpenSubsonic spec asks of a client: ids are opaque, and the only correct thing to do with one is return it.

That removed a **second latent bug** on the way: a standalone `Flower.Server`'s ids are database row ids (`SubsonicMapper.ToChild`), and it looks a stream request up with `db.Tracks.FindAsync(id)` — it never computes a `SyncKey` for anything, so a client asking with one could never have matched. Only the peer-to-peer path was self-consistent enough to work at all; `PeerLibraryViewModel`'s browse-and-play path already did the right thing, which is why this never surfaced as a visible failure.

**The four models are resolved as intentional, not collapsed.** Each earns its separate existence, and the drift that made this an item — five identity schemes, two of them differing by a punctuation character — is what actually got fixed:

- `Flower.Models.Track` is the mutable domain model, shared instance across `Library`, playlists and ViewModels.
- `Child`/`AlbumID3` are protocol DTOs whose shape is defined by OpenSubsonic, not by Flower; bending them toward `Track` would break the published surface.
- `PlaylistSyncTrackDto` is deliberately *minimal* — four fields, exactly enough to recompute `SyncKey` on the far side. Replacing it with `Child` would put fifteen fields on the wire to use four of them.
- `TrackEntity` is an EF Core entity in a different process with a different storage model.

Identity is now `Track.Id` (the one identity), `SyncKey` (demoted in fact as well as in comment: a *matching* heuristic for rescans, sync merges and the iTunes importers, no longer addressing anything), and one shared opaque `SubsonicIdentity` for albums and artists.

**Moved rather than closed:** `TrackEntity.Starred` still has no client-side counterpart. That is not drift to deduplicate — it is a missing feature that needs UI, a persisted field and a sidebar view, and it is already tracked as part of §4.1's liked-songs/smart-playlists gap. Nothing in §2.1 depends on it.

### 2.2 Auth and album-art lookup implemented two-to-three times — DONE

**Auth.** `SyncHttpServer.VerifySelfSigned`/`VerifyTrustedPeer` and `Flower.Server/Services/DeviceSignatureAuth` were near-identical hand copies, down to the `GetIdentityValue` header/query fallback helper written twice — security-critical code where a fix to one silently leaves the other wrong. Both now call `Flower.Core`'s `PeerSignatureAuth`, over a `SignedRequest` that carries the five things any HTTP stack can describe (method, path, query, body, header accessor). Each server keeps exactly one `ToSignedRequest` adapter — HttpListener on one side, `HttpRequest` on the other — and nothing else. The header-else-query *policy*, which decides where an attacker is allowed to put an identity, now exists once, on `SignedRequest.Identity`.

**Album art.** The embedded-tag-then-`cover.*`/`folder.*` lookup existed three times, and had already drifted: `AlbumArtLoader` accepted eight image extensions, `SubsonicEndpoints`' private copy accepted three. **An album with a `cover.webp` therefore showed art in the app and 404'd from a self-hosted `Flower.Server` serving the same library.** All three now call `Flower.Core`'s `LocalAlbumArtReader.ForFile`, which returns the bytes *and* their MIME type. Carrying the type out of the lookup also deleted `SyncHttpServer.SniffImageContentType`, which read PNG magic bytes and labelled everything else `image/jpeg` — so a served WebP, GIF or TIFF was mislabelled even when it was found. It lives in `Flower.Core` rather than `Flower` because `Flower` is Avalonia-coupled and out of the server's reach (`SYNC-PLAN.md`'s "Reuse boundary"), which was the original excuse for the copy; nothing in the lookup needs Avalonia, only the `Bitmap` decoding layered on top, which stays in `AlbumArtLoader`.

**Verification:** 498 passing in `Flower.Tests` (was 475), 70 in `Flower.Server.Tests`. New: `PeerSignatureAuthTests` (11 cases — self-signed happy path, a whole-query identity verifying the same as a header one, header winning over a conflicting query param, fingerprint-not-the-hash-of-the-offered-key, malformed keys, a trusted peer verified against the key on file rather than the one it offers, no key on file, nonce replay, clock skew, tampered body), `LocalAlbumArtReaderTests` (11 cases over real files, including every accepted extension and its MIME type), and a round-trip `getCoverArt` test asserting a `cover.webp` is served as `image/webp`. Confirmed to have teeth by mutation: making `Identity` prefer the query over the header fails the header-precedence test; dropping `.webp` from the accepted set fails two.

### 2.3 DI is service location, and the composition root is a 330-line method — MOSTLY DONE

**The composition root.** `App.axaml.cs::Bootstrap` `new`'d all ~30 services by hand in dependency order and registered them as *instances*, so constructor injection was bypassed everywhere and adding a dependency to any service also meant editing `Bootstrap`. It is now `RegisterServices` — registration only, nothing constructed — where every service is registered by type and the container does the constructing; a factory lambda is used only where the container genuinely cannot produce the value itself (a platform hook: `PlatformMusicImporter.Current`/`PlatformAudioManager.Current`; a value read off disk: `AppSettings`, the cached `Library`, `DeviceSigningKey`/`DeviceIdentity`). Adding a dependency to an existing service is now an edit to that service's constructor and nothing else. `Bootstrap` itself is left with what is genuinely startup *sequencing* rather than wiring: theme before the first window, playlists loaded into `Library` before the save subscription is attached, the EQ curve re-applied before the first frame of audio, then the views, `SyncHttpServer.Start`, and the background rescan.

The double `BuildServiceProvider()` is gone: the `ILoggerFactory` is created once with `LoggerFactory.Create` and registered as an instance, so injected `ILogger<T>`s and `AppLogging`'s static-field loggers come from the same factory and no throwaway container is leaked. `Flower/Extensions/ServiceCollectionExtensions.cs` (a commented-out body, zero callers) is deleted.

**`AlbumArtLoader`.** No longer a `static` class reaching into `Ioc.Default.GetService<PeerTrackResolver>()`/`GetService<DeviceIdentity>()` from inside a static method — both are constructor parameters now, and it is registered in the container like anything else. The ViewModels that call it reach it through a single `AlbumArtLoader.Current` seam set once from the container in `Bootstrap`, because they are `init`-only objects built by *static* builders (`TrackListBuilder`, `AlbumGridBuilder`, `RecentlyAddedAlbumsBuilder`) with no constructor to inject through; threading the instance the rest of the way is 4.2's job. The point of the change is that the dependencies are now *supplied* rather than *located*, which is what made the peer path untestable: `PeerTrackResolver.Resolve` is `virtual` over a `protected` constructor, so a test can resolve a track to a local `FakePeerHttpServer` without standing up the mDNS discovery stack behind `PairedServerReachability`. Two tests that could not previously exist do so now — a real fetch over a real socket asserting the request identifies us and asks for the right album id and that the response is written to the content-addressed disk cache, and a 404 asserting the failure is *not* cached (caching it would make the placeholder icon permanent).

**Still open**, both deliberately deferred to 4.2/4.3: `Ioc.Default` remains a service locator in the Views/Controls layer (44 call sites, down 2 — every window and control still resolves its own ViewModel), and **event subscriptions are still never unsubscribed anywhere** — `MainViewModel`'s constructor alone subscribes to eleven event sources and implements no `IDisposable`. That is harmless only because it is a process-lifetime singleton, which nothing enforces, and it is what blocks per-test reconstruction. Both are properties of the ViewModel/View layer rather than of the container, and fixing them is the decomposition itself, not more wiring.

**The container itself is now under test.** `RegisterServices` being a static method over an `IServiceCollection` — rather than registration interleaved with startup sequencing inside one 330-line method — is what makes that possible at all, and `CompositionRootTests` (33 cases) builds the real thing: `ValidateOnBuild` walks every type-based registration and rejects a constructor parameter nothing satisfies, each factory-lambda registration is resolved for real (they are lambdas precisely because they read a file or a platform hook, which `ValidateOnBuild` cannot see into), and every ViewModel is resolved on the headless UI thread. Three invariants that would otherwise only fail in production are asserted directly: that `MainViewModel` gets its ten *nullable, defaulted* sync-stack dependencies rather than silently accepting the nulls meant for WASM — which would resolve, start, and leave sync dead with no error anywhere; that `AppSettings`/`DeviceIdentity`/`Library` are genuinely one instance each, since they are shared mutable state; and that `DeviceIdentity.Fingerprint` is derived from `DeviceSigningKey` rather than registered independently, which would hand this device an identity its own signatures do not verify against. The audio registrations are swapped for a `FakeAudioManager` (resolving the real one opens a miniaudio device); everything else, sync stack included, is the genuine registration — constructing those is inert, `Start()` is what opens sockets. Confirmed to have teeth by mutation: dropping any single registration fails the suite, and demoting `Library` to `AddTransient` fails the single-instance test alone.

**Verification:** 594 passing in `Flower.Tests` (was 559), plus a real desktop launch: library load, playlist restore, miniaudio device init, sync server bind, mDNS browse and the full startup rescan all come up clean — the one part still not unit-testable, since it is the sequencing rather than the graph.

### 2.4 `Library.Playlists` is unlocked while `Library.Tracks` is locked — DONE

**Locking.** `AddPlaylist`/`RemovePlaylist`/`ReplacePlaylists` now take `_lock` — the same one `Tracks` uses — and, more importantly, follow the same copy-on-write discipline: `Playlists` is an `IReadOnlyList<Playlist>` over a list that is never mutated in place, only swapped. Locking the mutators alone would not have been enough. The list is enumerated without a lock all over the place (`MainViewModel` rebuilding the sidebar, `PlaylistSyncMapper.ToManifest`, `MainView`'s context menus), so in-place mutation under a lock would only have converted a lost update into an `InvalidOperationException` in a reader. Both halves are covered by tests that fail against their respective mutants (`Enumerating_Playlists_is_unaffected_by_a_concurrent_mutation`, `Concurrent_AddPlaylist_calls_do_not_lose_playlists`).

**Persistence.** Saving was six separate `_playlistStore.SaveAsync(Library.Playlists)` calls spread across `MainViewModel` (4), `MainView.axaml.cs` (1) and `PlaylistSyncService`/`SyncHttpServer` (1 each) — one per known mutation path, with nothing stopping the next one from forgetting. It is now one subscription in `App.axaml.cs` to a new `Library.PlaylistsChanged`, which fires for every mutation including in-place ones: `Playlist` raises its own `Changed` event wherever it bumps `UpdatedAt`, and `Library` relays it, moving the subscription from the outgoing to the incoming set on every swap. `PlaylistsChanged` is deliberately separate from the existing `PlaylistsUpdated`, which means "rebuild the sidebar" and must *not* fire for local edits (it would tear down the row the user is mid-rename in).

`PlaylistStore` no longer needs to be a `MainViewModel`/`MainView`/`PlaylistSyncService`/`SyncHttpServer` dependency at all, and those four constructor parameters are gone. `SaveAsync` now takes its record snapshot inside its write lock rather than before it, since saves can now genuinely overlap and the older snapshot could otherwise land last.

**A bug this uncovered.** `Playlist.Tracks` was a public `List<Track>` and `MainViewModel.ReorderPlaylistTrack` reached straight into it with `Remove()`+`Insert()`, bypassing `UpdatedAt` entirely — so a drag-reorder was invisible to `PlaylistSyncPlanner`, which decides "did this side change?" purely from `UpdatedAt` against a per-peer baseline, and a reorder therefore never propagated to a paired device. `Tracks` is now `IReadOnlyList<Track>` and the reorder goes through a new `Playlist.MoveTrack`. It is copy-on-write for the same reason `Library.Playlists` is: the save now runs on a threadpool thread and enumerates a playlist's tracks while the UI thread may be adding to that same playlist. (`RemoveTrack` also stopped bumping `UpdatedAt` when the track was not in the playlist, which used to manufacture a sync-visible "change" out of a no-op.)

### 2.5 No schema version anywhere — DONE

Backward compatibility was ad hoc sentinel detection invented independently three times: `PlaylistRecord.Id = default`, `TrustedPeer.PublicKey = ""`, and `DeviceIdentityStore.Load`'s alias-backfill/fingerprint-correction. `Flower.Server` was worse — `EnsureCreatedAsync()` with no EF migrations at all, so **any** schema change wiped a self-hoster's database.

**Resolution.** The JSON side needed no versioning scheme at all, because there is nothing to be compatible *with* (see `CLAUDE.md`, "No Users Yet") — every sentinel was deleted rather than formalized:

- `PlaylistRecord.Id`/`UpdatedAt` are plain required fields; the `Guid.Empty`/`default` re-minting in `Load` is gone. `PlaylistTrackRecord` is now just `(Guid Id)` — the `Path` fallback and the pre-`Track.Id` `TrackPaths` list are both deleted, along with `Load`'s whole by-path index. An entry whose id doesn't resolve is dropped, full stop.
- `TrustedPeer.PublicKey` is a required constructor parameter, not `= ""`. `GetPublicKey` is a plain lookup: an approval without a key is not a representable state anymore.
- `DeviceIdentityStore.Load` no longer backfills a missing alias. The fingerprint correction *stays* — it is not a migration, it's the runtime response to a regenerated signing key (`DeviceKeyStore`), and its comment now says so.
- `Track.Id`'s initializer stays for the same reason (every `Track` needs an id from construction); only its "the initializer is also the migration" comment went.

`Flower.Server` got the real thing instead: `Microsoft.EntityFrameworkCore.Design` was already referenced, so `Data/Migrations/` now holds an `InitialCreate` migration plus the model snapshot, and startup calls `MigrateAsync()` instead of `EnsureCreatedAsync()`. Subsequent entity changes go through `dotnet ef migrations add <Name> -p Flower.Server -s Flower.Server -o Data/Migrations`. **An existing dev `flower.db` created by `EnsureCreated` has no `__EFMigrationsHistory` table and must be deleted once** — `MigrateAsync` would otherwise try to create tables that already exist.

---

### 2.6 Rescan carry-forward has no guard against a forgotten field — HALF DONE

Carried over from the original review draft, where it was §2.5 and was lost in a renumber (this document's §2.5 is that draft's §2.6).

The refactor half landed: `Library.UpdateTracks` used to repeat the same five assignments in both of its match branches, and they are now one `CarryForwardMutableState(previous, track)` called from both, so adding a persisted-but-not-rescannable field is one edit rather than two.

The guard half did not. `LibraryTests` pins the *current* fields one test at a time (`DateAdded`, `PlayCount`/`ImportedPlayCount`, `LastPlayedAt`, the sync-placeholder set), so nothing fails when someone adds `Starred`, `Rating` or a provider `Source` tag and forgets to list it. That is exactly the failure mode the method was introduced to prevent, and it is silent: the field just resets to its default on the next launch, on every launch. The original ask was a test that fails when a new such field is added and not carried forward — which means enumerating `Track`'s persisted properties by reflection and asserting each is either carried forward or explicitly named as rescannable, rather than another per-field test.

Small, and worth doing opportunistically rather than as its own scheduled item; it is not in the Suggested order below for that reason.

## Tier 3 — security — DONE

Ordered by real exposure, not theoretical severity. All six landed; see *Tier 3 execution* below for what each fix actually is.

1. **`Flower.Server`'s `/rest/*` has no rate limiting.** `AdminEndpoints` and `PairingEndpoints` each get a `RateLimiter`; `SubsonicEndpoints` has none. Classic Subsonic auth is `t = md5(password+salt)` with no expiry or nonce, so a captured `u`/`t`/`s` query string replays forever. Combined with the shipped default `AdminPassword = "changeme"` and `LanGuard`'s unconditional trust of the Tailscale CGNAT range `100.64.0.0/10`, that is an unauthenticated brute-force surface on any tailnet-exposed deployment.
2. **Application logs, including exception text and file paths, are pushed over plaintext HTTP** to the paired peer. TLS is permanently deferred for the P2P path by design; the log-push feature arrived after that decision and changed what is at stake.
3. **The private signing key is stored as plaintext PKCS8 JSON**, with no revocation path a victim can initiate. Documented as accepted, but it is the root of the whole identity scheme.
4. **`AdminAuthService` tokens are in-memory with no per-token revocation** — a stolen bearer token is good for 24 h and the only lever is a process restart.
5. `RateLimiter` is a fixed window, so a boundary-timed burst gets roughly 2× the intended ceiling on login and pairing-code redeem.
6. Body-size caps are inconsistent: `SyncHttpServer` caps at 20 MB; `Flower.Server` caps only pair-redeem at 4 KB, with no global limit.

### Tier 3 execution

1. **`/rest/*` rate limiting + no more shipped default password.** `SubsonicEndpoints` now carries two per-source-IP budgets: `FailedAuthLimiter` (10/60s) charged *only* on an auth failure and *peeked* (`RateLimiter.WouldAllow`) before every request, so a source that burns it is locked out of `/rest` entirely rather than getting another free guess; and `RequestLimiter` (600/60s) on every request, sized for an album grid's `getCoverArt` burst rather than for `SyncHttpServer`'s 120/60s browse ceiling. On top of that, `AdminPassword` no longer ships as `"changeme"` — `appsettings.json` ships it empty and `Program.cs` throws at startup on empty/whitespace/placeholder, checked after `builder.Build()` so environment variables and Docker secrets count. The replay property of `t=md5(password+salt)` itself is unchanged: that is protocol-level, and fixing it means breaking every third-party Subsonic client. `LanGuard`'s CGNAT trust is now a flag (`allowCarrierGradeNat`, exposed as `Flower:TrustTailscaleRange`, default on) so a non-tailnet deployment can drop `100.64.0.0/10` instead of trusting a whole carrier's subscriber range for nothing.
2. **Log push is opt-in.** `AppSettings.ShareLogsWithPairedServer`, default **false**, gates `LibrarySyncService.PushLogSnapshotAsync` on top of the existing role check, with a checkbox in both Settings surfaces that says plainly that the logs go out unencrypted and contain file paths and error details. The transport stays plaintext by design (TLS is permanently deferred for P2P — `SYNC-PLAN.md`); what changed is that the highest-value payload on that path is no longer sent by default.
3. **The private signing key is written 0600.** `AtomicJsonFile.Write/WriteAsync` take an `ownerOnly` flag, applied to the temp file *before* any bytes are serialized (so the key is never briefly world-readable) and re-applied to the target and its `.bak` after `File.Replace`, which preserves the target's old mode rather than the temp file's. No-op on Windows and best-effort on filesystems without POSIX modes. Still no OS keychain and still no remotely-initiated revocation — the accepted-limitation comment on `DeviceKeyStore` now spells out the peer-side revocation path instead of leaving it implied.
4. **Admin tokens are revocable.** `AdminAuthService.Revoke`/`RevokeAll`, backing `POST /api/admin/logout` (the presenting token, stashed in `HttpContext.Items` by the bearer filter) and `POST /api/admin/logout-all` (every session, including the caller's).
5. **`RateLimiter` is a sliding window.** The standard weighted approximation — previous window's count carried alongside the current one and charged in proportion to the overlap — so a boundary-timed burst no longer gets ~2x the ceiling. One extra `int` per key, no per-request timestamp list. It also evicts keys idle for four windows: keys are attacker-chosen source IPs, so the old never-evicting dictionary was itself an unbounded memory sink.
6. **A global body cap.** `Kestrel.Limits.MaxRequestBodySize = 20 MB`, matching `SyncHttpServer`, replacing Kestrel's 30 MB default on every route except pair-redeem's hand-rolled 4 KB check (which still applies on top).

**Not addressed, deliberately:** Subsonic token replay (protocol-level, see above), TLS on the P2P path (`SYNC-PLAN.md` decision), and OS-keychain key storage.

**Verification:** `dotnet test Flower.Tests/Flower.Tests.csproj --filter Category!=RequiresLibVLC` (414 passing) and `dotnet test Flower.Server.Tests/Flower.Server.Tests.csproj` (65 passing). New coverage: sliding-window/boundary-burst/peek/eviction cases in `RateLimiterTests`, `LanGuardTests`' CGNAT-off cases, `AdminAuthServiceTests`' revocation cases, `SubsonicRateLimitTests` (lockout, per-source isolation, normal traffic unaffected), `AdminPasswordStartupTests` (the server refuses to boot on a placeholder password), and a `DeviceKeyStore` 0600 assertion in `StoreRoundTripTests`.

---

## Tier 4 — rewrite candidates — 4.4 DONE, rest NOT STARTED

### 4.1 Persistence: JSON blobs → SQLite (client side)

The one genuine rewrite recommendation, deliberately sequenced late. Every Tier 0/1 data problem traces to "the whole library is one JSON document": non-atomic writes, whole-file rewrites per play, no partial reads, no indices, no migrations, no room for per-user or per-provider state. `Flower.Server` already uses EF Core + SQLite; the client should too, sharing the schema via `Flower.Core`. That collapses four track models toward one, makes a play-count bump a single-row `UPDATE`, and gives migrations for free.

**Start it when a roadmap item demands queryable state** — smart playlists, liked songs, per-user play counts, or "downloaded only". Until then Tier 0's atomic writes and Tier 1.1's stats/metadata split are worth doing on the JSON layer anyway: they are small, they stop active data loss now, and they are the same decomposition SQLite would need.

Migration path: keep `library.json` as an import-once source, write SQLite alongside for one release, then drop the JSON.

### 4.2 `MainViewModel` (2,573 lines) — decomposition

Six unrelated jobs in one class; roughly **900 lines are a P2P sync coordinator** living here only because `SidebarItems` does.

| New class | Moves in |
|---|---|
| `PeerSyncCoordinator` | `RunTrackedSync`, `ScheduleContentSync`, `TriggerSyncIfReady`, `RunPendingDeviceSyncs`, pair/unpair/confirm-trust, `ForceSyncNow`, `DeviceAlias`, plus the whole device-sidebar-row state machine |
| `LibraryBrowserViewModel` | `Rows`, `_currentFilteredTracks`, `FilterText`, the three independent sort states, `ScheduleFilter`/`RebuildRowsAsync`, grid tiles, `SubListItems`, `StatusBarText` |
| `PlaylistManagementViewModel` | playlist CRUD + `RefreshPlaylistSidebarItems` |
| `SidebarViewModel` | `SidebarItems`/`SelectedSidebarItem`/`BuildSidebarItems`, composed from the three above |
| `AppSettingsViewModel` | the six-plus repetitions of the `_appSettings ??= new(); …; _ = _appSettingsStore?.SaveAsync(…)` triplet |
| `ITunesImportCoordinator` | the two iTunes sync methods + their cooldown fields, used by both startup and Settings |

The 20-parameter constructor (10 required, 10 defaulted-to-null purely for WASM) is the symptom: testing anything means standing up or nulling the whole graph. Relatedly, `_appSettings` is nullable and lazily `??=`'d across the entire class *only* so the Avalonia previewer's parameterless constructor works — isolate that in a design-time subclass instead.

**Mobile is mostly good reuse, undermined by `private`.** `MobileMainViewModel` correctly wraps `MainViewModel` rather than re-implementing filter/sort, but duplicates what it can't reach: `PlayResolvingPlaceholder` is reimplemented inline in `PlayTrackCommand`; `SyncPlayQueueToCurrentView` is reimplemented because desktop's reads a private field — and its own comment records that gap **shipped as a real bug** ("mobile's queue stayed pinned to Importer's raw filesystem-scan order… confirmed on a real device"). Five drill-in methods hand-roll the same push-history/set-scope/rebuild/raise sequence.

### 4.3 Duplicated multi-select and drag gestures in `MainView.axaml.cs`

Roughly 400 lines implement shift-range / ctrl-toggle / drag-threshold / drop-highlight **twice** — once for `SubList`, once for the album grids — same logic, different target. Extract one reusable multi-select-and-drag-source helper.

Two pieces of genuine business logic also sit in code-behind and should move where they can be tested: `OpenTrackInfoForSelectedAlbums`/`ResolveSelectedAlbumTracks` (non-trivial precedence rules for what "Get Info" applies to) and `CommitRename` (mixes UI teardown with `PlaylistStore.SaveAsync`/`DeviceNicknameStore.SetAsync`/`ScheduleContentSync`).

### 4.4 `SyncHttpServer` streaming has no range support — DONE

`HandleStreamAsync` set `ContentLength64` and copied the whole file — no `Range` handling, so a dropped mobile download restarted at byte 0 and seeking within a peer-streamed track couldn't use partial content. `Flower.Server` already got this right via `Results.File(..., enableRangeProcessing: true)`, so only the peer-to-peer server was wrong.

**Server:** `HandleStreamAsync` now advertises `Accept-Ranges: bytes` on every response — including the unranged one, which is the only place a client can learn resuming is possible — and serves a single byte range as 206 + `Content-Range`, bounding the copy rather than seeking and running to EOF. `ParseSingleByteRange` handles the bounded, open-ended and suffix forms, clamps an end past the end (RFC 9110 14.1.1), and *ignores* anything it can't interpret (multipart, unknown unit, malformed — 14.2) rather than failing the request. A range starting past the end is 416 with `bytes */{length}`, which is what lets a client with an over-long partial recover instead of retrying forever.

**Client:** `DownloadTrackAsync` now downloads to `<destination>.part` and only renames on success, so a truncated file can never be mistaken for a playable one, and a failed transfer deliberately *keeps* the partial for the next attempt to resume from. A 200 answer to a ranged request (a peer on an older build, or any server that ignores `Range`) overwrites rather than appends; a 416 discards the partial and refetches. The orphan leak is closed at the source: `LibraryDownloadService` names the destination `{track.Id:N}.{ext}` instead of a fresh `Guid` per attempt, which is also what makes resuming possible at all.

**Verification:** 475 passing in `Flower.Tests` (was 454). Four round-trip tests over the real socket (bounded range, open-ended resume, 416 with the real length, `Accept-Ranges` on the full body), `RangeHeaderParsingTests` for the forms an `HttpClient` won't put on the wire, and three `OpenSubsonicClientTests` against a real `HttpListener` that aborts mid-transfer: resume asks for exactly the remainder, an ignored range overwrites, an unsatisfiable partial is discarded and refetched.

---

## Tier 5 — test coverage — DONE, except ForceSyncNow's reachable path and peer approval in 5.6

CI ran `dotnet test Flower.Tests`, which builds only `Flower.Tests → Flower → Flower.Core`: **`Flower.Server` and `Flower.CLI` were never compiled by CI**, let alone tested. (The `tests.yml` comment claiming `Flower.csproj` multi-targets `net10.0;net9.0` was stale — it targets `net10.0` only. Corrected.)

**What that hid, found while fixing it:** `Flower.Server/Data/`'s four entity sources — `FlowerDbContext.cs`, `TrackEntity.cs`, `PlaylistEntity.cs`, `PlaylistTrackEntity.cs` — **were never committed at all**. The `.gitignore` rule `Flower.Server/data/`, meant for the default runtime `DataDirectory`, also matched the *source* folder `Flower.Server/Data/` on a case-insensitive filesystem (macOS, Windows), so `git add` silently skipped them. `Flower.Server` has therefore been unbuildable from a clean checkout since it was scaffolded, and nothing noticed because no CI job ever built it. The rule is now per-artifact (`[Dd]ata/*.db`, `*.db-shm`, `*.db-wal`, `*.json`, `logs/`) rather than a directory exclusion — a directory exclusion would also have made a `!*.cs` negation impossible, since git does not descend into an excluded directory.

Highest-value additions, roughly in priority order:

1. ~~**A `Flower.Server` test project at all.**~~ **DONE** — `Flower.Server.Tests`, 55 tests. `SubsonicAuth` (salted-token acceptance, salt reuse, wrong password/username, each missing parameter, and that plaintext `p=` auth stays unsupported), `AdminAuthService` (fixed-time credential comparison including the prefix case, token issue/validate, tokens not surviving a restart), `PairingCodeService` (single-use, case/whitespace tolerance, ambiguous-character exclusion, independence between outstanding codes), and `LanGuard` (private/loopback/CGNAT allowed, public refused with 403 *before* auth runs).

   Plus endpoint tests over the real route table via `WebApplicationFactory` + temp SQLite, covering every Tier 1.3 query shape. Requests go through `TestServer.SendAsync` rather than an `HttpClient`, because `LanGuard` rejects the null `RemoteIpAddress` an `HttpClient`-issued test request carries — that is real behaviour worth testing rather than configuring away, so the harness presents a loopback client instead of bypassing the middleware.

   Confirmed to have teeth by mutation: reintroducing the `Max(DateTimeOffset)` that shipped in 1.3 fails two of these tests.

   New CI jobs: `server-test` (runs the above) and `build-rest` (builds `Flower.Desktop` and `Flower.CLI`), so every project in the solution is now compiled by CI.
2. ~~**A real socket round trip**: `SyncHttpServer`.~~ **DONE** — `SyncHttpServerRoundTripTests`, 22 tests against a real `SyncHttpServer` on a real port driven by a real `HttpClient` over loopback, with `TestSupport/TestSigningKey` standing in for a peer's keypair. Covers the open `/info` endpoint (including `trustsCaller` omitted-vs-false), unknown-route 404, the trust gate (unsigned request from a trusted fingerprint, correctly-signed request from an untrusted one, nonce replay, and a signature bound to a query it was not made for), all of `AuthMode.SelfSigned` pairing (approve, deny, no-UI-listening fail-closed, fingerprint-not-matching-its-public-key, idempotent re-pair, and the 5/60s per-source-IP pair budget defeating fresh-keypair-per-attempt), unpair notification, the body-carrying endpoints (log snapshot keyed by the *verified* header identity rather than the body's claim, tampered body invalidating the signature, playlist manifest replacing local playlists), the bulk-sync role check, and the OpenSubsonic surface (stream serving real file bytes, 404 for an unknown id, `getAlbumList2`).

   Confirmed to have teeth by mutation: making `VerifyTrustedPeer` return its fingerprint without checking the signature fails four of them.

   Two branches are deliberately not covered here. The non-LAN `LanGuard` rejection cannot be produced from a single-machine test (`LanGuardTests` covers the predicate instead), and the 20 MB body cap would mean uploading 20 MB into a server that closes the connection partway, racing the client's own send (`RequestBodyReaderTests` covers the cap logic instead). On Windows the wildcard `http://+:{port}/` bind needs a `netsh http add urlacl` reservation, so `BoundPort` stays null and every test early-returns rather than failing — the same known gap `SyncHttpServer.Start` documents.
3. ~~**`Importer`** — zero coverage.~~ **DONE** — `ImporterTests`, 11 tests: recursive walk, extension filtering (including case-insensitivity and rejecting a supported-looking `.ogg`), dedup across overlapping configured paths, blank/duplicate/nonexistent configured paths, skip-unreadable-file, the full tag→`Track` mapping (multi-value flattening, `FirstGenre`, year 0 → null), audio properties, and `IsCompilation`.

   Fixtures are real files read through real TagLib#, generated at test time by `SyntheticWav` — no binary fixtures in the repo. That constrains them to WAV, which TagLib# reads as a `TagLib.Riff.File` supporting a real ID3v2 tag, so the mapping and the Id3v2 branch of `ReadIsCompilation` are exercised for real; the Apple (m4a) and Xiph (flac) branches of that method are not reachable this way and stay uncovered. No test calls `Import()` with an unresolvable path set, because that falls back to `ResolveMusicPath` and would walk the developer's real `~/Music`.

   Confirmed to have teeth by mutation: dropping the `seenFiles` dedup, the `ToLower()` on the extension match, or the `Year > 0` guard each fails a test.
4. ~~**`AlbumArtLoader`** — zero coverage, despite carrying the most "confirmed on a real device" bug narratives in the codebase.~~ **DONE** — `AlbumArtLoaderTests`, 15 tests: embedded art decoded, no-art → null, undecodable art → placeholder rather than an unobserved fault, decode-down to `MaxArtPixels` and never scaling *up*, the cache-key collision itself (two albums in one flat downloads folder keeping their own covers; one album across two directories sharing a single decoded `Bitmap` by reference), the blank-`Album` fallback to a directory key, and the placeholder-track paths (missing hash, missing origin device, disk-cache hit, corrupt cache file falling through, in-memory hit keyed by hash across origin devices).

   Two pieces of test infrastructure came with this. `SyntheticPng` builds a real decodable PNG of an exact pixel size in memory — the image counterpart of `SyntheticWav`, and needed because these assertions are all about the *intrinsic* size of the art. And `TestAppBuilder` now configures the headless platform with real Skia drawing (`UseSkia()` + `UseHeadlessDrawing = false`, plus an `Avalonia.Skia` reference): headless's own drawing stub reports every image as 1×1 whatever bytes it is handed and never rejects garbage, which would have made the scaling and corrupt-input cases assert nothing at all. The whole suite runs on the Skia-backed headless platform now; nothing else changed behaviour under it.

   The peer HTTP fetch was the one branch this could not reach, because `AlbumArtLoader` was a static class service-locating `PeerTrackResolver`/`DeviceIdentity` out of the process-wide `Ioc.Default` — §2.3 stated as a concrete cost rather than a principle. §2.3 has since made both constructor parameters, and the fetch is covered: two more tests drive it against a real `FakePeerHttpServer` socket (the request identifies us and asks for the right album id, the response lands in the content-addressed disk cache; a 404 is *not* cached).

   Confirmed to have teeth by mutation: collapsing `LocalCacheKey` back to directory-only, removing the `PixelSize.Width <= MaxArtPixels` guard, or dropping `LoadLocalBitmap`'s try/catch each fails a test.
5. ~~**`MusicListView`/`MusicListPanel`**~~ **DONE** — both halves. `MusicListPanelTests`, 15 tests over the real panel: the realized window (viewport + 3-row overdraw, partially-scrolled rows, clamping at both ends), album-group-leader retention (a leader scrolled off the top still realized so its art spans down, no double-realization when the leader is already on screen, only the *visible* groups' leaders pulled in), the grow-only row pool (extras hidden rather than destroyed, and never arranged), `SetItems` re-binding every slot when the new list reuses the old indices, and the measure/arrange contract (full list height so the scrollbar is sized for every row, total column width so a horizontal scrollbar can appear, viewport width when the columns don't fill it, absolute per-row Y offsets).

   Assertions read the panel's real `Children`/`DataContext`/`Bounds` after a real measure-arrange pass, not an extracted copy of the arithmetic. `MusicListPanel` now takes its `ColumnManager` as an optional constructor parameter (`MusicListView` already had one resolved and passes it in) instead of service-locating it — one fewer `Ioc.Default` call site, and what makes the panel testable at all.

   Confirmed to have teeth by mutation: removing the overdraw, the group-leader `set.Add(leader)`, the `SetItems` index reset, or reporting the pool's height instead of the list's each fails between one and three tests.

   ~~Still open: `MusicListView` itself — shift-range/ctrl-toggle selection and header drag-reorder.~~ **DONE** — `MusicListViewGestureTests`, 21 tests. Selection: plain click, replacing a selection, shift-click ranges in both directions, a second shift-click re-measuring from the *same* anchor rather than the previous landing row, primary-modifier add/remove, deselecting the last remaining row leaving nothing selected, a plain press inside a multi-selection preserving all of it (so a drag or context menu acts on the whole thing), a click below the last row, and selection re-applied by `Track.Path` across `SetItems` — including that a row filtered out is dropped for good and not resurrected when the unfiltered view comes back. Header: click sorts, sub-threshold movement still sorts, a drag past the threshold reorders and does *not* sort, a drag back to its own slot is a no-op that also does not sort, a drop past the right edge puts the column last, and a right-click does neither.

   These drive the real gestures through a shown headless window's input pipeline (`HeadlessWindowExtensions.MouseDown`/`MouseMove`/`MouseUp`), not by calling the private handlers — the only way the click-vs-drag threshold and the pointer capture that carries a drag past the cell it started on are exercised at all. Geometry comes from the control's own layout (`RowDefinitions="28,*"`), and the modifier is derived from `PlatformShortcuts.Primary` so the Meta/Control split is not hardcoded.

   Confirmed to have teeth by mutation: collapsing `SelectRange` to the clicked row alone (5 failures), reading `_isColumnDragging` after `Capture(null)` instead of snapshotting it (3 — the exact bug the snapshot comment describes), making `ToggleRow` never deselect (2), and dropping `SetItems`' `_selectedPaths.IntersectWith(stillPresent)` (1) each fail.

   **One mutation survives, and it is a finding rather than a test gap:** removing the `_isRaisingSelectedTrack` echo guard in `SetSelectedTrack` breaks nothing, even with the selection driven through a real TwoWay binding to a source that re-raises unconditionally (`MultiSelectionSurvivesTheRealTwoWayBindingRoundTrip` reproduces `PlaylistControlViewModel`'s setter). Every path that raises the property assigns `_selectedRow` *before* raising, so the write-back always finds `row == _selectedRow` and returns without collapsing anything. The guard is dead defensive code against an ordering that no longer exists — worth deleting along with its comment, but that is a behaviour change and is left for a deliberate pass rather than folded into a test commit.
6. ~~**`MainViewModel`'s sync/pairing/device-row state machine** (~900 lines)~~ **Device rows and pairing DONE** — `MainViewModelDeviceSidebarTests`, 23 tests: the Devices/Server sections (which row lands where, the icon that goes with it, a peer that starts advertising Server mode relocating in place, the vacated header disappearing with its last member), identity matching (the same fingerprint under a new instance name updating one row; two devices sharing an unrenamed computer name staying separate; an unresolved arrival neither claiming an already-resolved row nor being shown under its raw mDNS name; duplicate rows collapsing once the `/info` handshake mutates a `DiscoveredDevice` into a fingerprint already tracked), display names (the IP subtitle appearing only on a genuine collision and going away again), and the pairing lifecycle (pinning on pair, unpinning on unpair, the pinned row surviving the server going offline and flipping to unreachable while an ordinary peer's row is removed outright, the launch placeholder for a paired server never discovered this session being claimed by the real peer rather than duplicated — and not claimed by an unrelated one, the ambiguous mDNS goodbye deliberately removing nothing, and becoming a Server yourself clearing a held pairing).

   `AddOrUpdateDeviceSidebarItem`/`RemoveDeviceSidebarItem` are `internal` rather than `private` for this (`InternalsVisibleTo Flower.Tests` was already in place). Driving them through the real `NetworkDiscoveryService` would mean standing up an mDNS backend *and* an HTTP `/info` endpoint per case purely to choose a `Fingerprint`, since the handshake is the only thing that ever sets one — and `NetworkDiscoveryServiceTests` already covers that handshake. What was untested is what `MainViewModel` does with the resulting `DiscoveredDevice`.

   Confirmed to have teeth by mutation: matching on `InstanceName` regardless of fingerprint, showing unresolved devices under their raw name, never claiming the launch placeholder, dropping the duplicate collapse, collapsing both sections into one, never relocating on a role change, letting empty headers linger, removing the paired row when it goes offline, showing the subtitle without a collision, and dropping the unpin from either `UnpairServer` or the `IsServer` flip each fail between one and three tests. **One survives and cannot be killed from here:** the selection restore in `RelocateDeviceSidebarItemIfNeeded`. It exists because the sidebar ListBox's two-way binding clears `SelectedSidebarItem` when the row is removed mid-move — with no real ListBox attached, the removal never clears it, so the restore is a no-op in any ViewModel-level test. It needs a headless view test, not a better assertion.

   **The sync-trigger side is now covered too** — `MainViewModelSyncTriggerTests`, 20 tests, and no fake `LibrarySyncService`/`PlaylistSyncService` was needed after all. Peers point at a closed loopback port, so the real sync services run and fail fast with a connection refusal; what is under test is the tracking *around* a sync, and a failing sync exercises every edge a successful one does. Covers role gating (a Server never initiates; a Client syncs only with its one paired Server; an unresolved peer is skipped), the once-per-session first-contact dedup, the `IsSyncing` edges (exactly one notification up and one down, however many concurrent syncs fan out, and the paired row's spinner clearing), `ForceSyncNow`'s not-paired and not-currently-found paths, `ScheduleContentSync`'s restart-don't-queue debounce (a burst of five collapsing to one sync run, and a Server scheduling nothing), and — a path found while doing this and previously untested altogether — `TriggerSyncIfPeerCatalogChanged`, the library-token trigger from Tier 1.4: first observation records without syncing, an unchanged token does nothing, each genuine change resyncs, and no token at all is ignored.

   Confirmed to have teeth by mutation: dropping the role gate from either trigger, the first-contact dedup, the debounce restart, `ForceSyncNow`'s paired check, the increment/decrement edge conditions, the row's carry-forward of in-flight state, the first-observation check, and the token recording each fail between one and eight tests. Two guards are redundant rather than untested and survive by construction: `TriggerSyncIfReady`'s empty-fingerprint check (the role gate already rejects an empty fingerprint) and `TriggerSyncIfPeerCatalogChanged`'s empty-token check (a first observation returns anyway).

   `ForceSyncNow`'s *reachable* path — the actual sync, the reached-but-unchanged vs. could-not-reach result strings, and its deliberate bypass of the dedup — stays uncovered. It reads `PairedServerReachability.PairedServerDevice`, which is only ever populated from `NetworkDiscoveryService.KnownDevices`, i.e. by a real mDNS announcement plus a real `/info` handshake; adding a device to the sidebar does not put it there. The peer-approval flow (`PeerApprovalRequested`) is also still open.

   **A test-infrastructure problem surfaced doing this and is not solved** — written up at length in `Flower.Tests/TestSupport/AssemblySetup.cs`. The suite fails intermittently, about 1 run in 8, inside Avalonia's own headless session setup (`HeadlessUnitTestSession.EnsureIsolatedApplication` → "The calling thread cannot access this object because a different thread owns it"). It surfaces as an unrelated `[AvaloniaFact]` failing — usually one of `CompositionRootTests` — and is never an assertion failure in the code under test. Measured: disabling collection parallelization does not fix it; excluding the two suites that must run `Dispatcher.UIThread.MainLoop` does not fix it either. What took it from ~1 in 3 down to ~1 in 8 was not letting a `MainViewModel` be constructed while `ContentSyncCooldown` is shortened — its constructor starts a periodic `_logPushTimer` at that interval and never stops it, so such an instance leaks a 150ms `DispatcherTimer` onto the shared dispatcher for the rest of the run. That leak (`MainViewModel` has no `Dispose`) is the most promising next thread to pull on, and is worth fixing on its own merits.
7. ~~**`Library.ReplacePlaylists`' `PlaylistsUnchanged` short-circuit**~~ **DONE** — 8 more tests in `LibraryTests`: that an *equal-but-not-identical* set short-circuits (the case that actually matters, since the sync merge rebuilds `Playlist` objects from a peer's manifest rather than handing back the held ones), and that each of the four ways a set can differ is told apart from identical on its own — an addition, a removal, only `UpdatedAt` moving, only the `Id` differing, and a pure reordering (order counts, because the sidebar renders in list order). Plus that a short-circuited call keeps the *held* instance and therefore its `Changed` subscription, and that the replacement list is copied rather than stored. Confirmed to have teeth by mutation: removing the short-circuit, comparing `Id` alone, comparing `UpdatedAt` alone, skipping the per-item loop, and storing the caller's list each fail between one and four tests. ~~**`ColumnManager.Reorder`**~~ **DONE** — `ColumnManagerTests`, 9 tests: moving a column later/earlier/onto its own position/past the end, that the index is expressed in *visible* columns and skips hidden ones, that a hidden column keeps its position relative to its neighbours across a reorder that never mentions it, that `Order` is renumbered contiguously, and that a `Width` change does not raise `ColumnsChanged` (the header rebuild that used to kill a resize drag mid-gesture) while `IsVisible` does.
8. ~~**No dedicated tests**~~ **DONE** — for `CurrentlyPlayingControlViewModel`, `TrackRowViewModel`, `VolumeControlViewModel`, `EqualizerViewModel`, `LogViewModel`, `SidebarItem`, or `ScreenStackPanel`'s swipe state machine. — **`SmallViewModelTests` covers five of the seven**, 54 tests: `TrackRowViewModel` (art size capped at `ArtMaxSize`, the blank-at-zero display strings, `NotifyStatsChanged`'s fan-out, the placeholder/available/downloadable flag lattice, and the download spinner's timer being stopped by both `Dispose` and clearing `IsDownloading`), `VolumeControlViewModel` (pass-through in both directions, no cached copy), `SidebarItem` (headers unselectable, both reachability glyphs gated on `IsPairedServer`, and both setters fanning out to them), `EqualizerViewModel` (one band per `Equalizer.BandCount`, first-run defaults, the wrong-length `BandGainsDb` re-size, restore-from-settings, live-apply on every mutation, and disabling clearing the filter rather than pushing an all-zero one), and `CurrentlyPlayingControlViewModel` (the constant-height `" "` subtitle, duration formatting either side of an hour, `TotalTime` preferring the track's tagged duration over the audio manager's, and the seek debounce — no seek mid-drag, only the settled position sent, never seeking while stopped, and a position update coming *from* the audio manager not bouncing back out as one).

   Two mutations needed the tests rewritten rather than the assertions tightened, and both are worth knowing about for future ViewModel tests here. The download spinner cannot be observed with `Dispatcher.UIThread.RunJobs()`: that drains queued operations but never advances the timer queue, so a 16ms `DispatcherTimer` reads as "not turning" whether or not it is still alive — `Dispatcher.UIThread.MainLoop(cts.Token)` with a short cancellation runs the real loop and does fire it. And the peer-stream-URL guard in `LoadAlbumArt` is invisible in the result (both routes end at `AlbumArt = null`, one of them via a caught TagLib exception), so it is asserted through a recording `ILogger` instead — the avoided read *is* the behaviour.

   **`LogViewModelTests`** covers the sixth, 19 tests over the "This Device live log vs. a paired client's pushed snapshot" selector: the sidebar (client rows only when running as a Server, a refresh keeping a still-present selection and falling back to This Device when a revoked peer's row vanished), the explicit "no snapshot received yet" placeholder, filtering (message and `SourceContext`, case-insensitively; minimum level; an unparseable level string dropped rather than shown), the restored font size/level/word-wrap, `LinesReset`'s growth flag (false for a pure filter/level change so the View does not yank the pane around, true across a selection change), the live local path (`LinesAppended` rather than a full re-render, a burst coalesced into far fewer batches than lines with no line emitted twice, filtered-out entries appending nothing, local entries never leaking into a client's pane), and the client refresh (a new snapshot replacing rather than appending, a push from a *different* client leaving the pane alone).

   Since `InMemoryLogStore` is a process-wide singleton with a private constructor, each test tags its entries with a unique marker and filters to it, which isolates `DisplayLines` from whatever the rest of the suite logs in parallel.

   Confirmed to have teeth by mutation: hardcoding the growth flag, dropping the selection-change reset of `_lastRenderedEntryCount`, making the filter case-sensitive or blind to `SourceContext`, keeping an unparseable level, dropping the no-snapshot placeholder, ignoring which fingerprint a snapshot came from, always resetting the sidebar selection, and failing to clear the pending-entry buffer each fail a test. Two guards are *not* individually killable and are redundant by construction rather than untested: `_flushScheduled` only suppresses duplicate dispatcher posts (the events emitted are identical either way), and the selection check in `OnLocalEntryAdded` is re-done in `FlushPendingLocalEntries` — removing both together does fail a test.

   **`ScreenStackPanelSwipeTests`** closes the last of it, 15 tests over the swipe state machine, driven through a shown headless window's real input pipeline: direction detection (a mostly-vertical drag rejected on the dx-vs-dy ratio even when it is long enough horizontally to have committed; nothing captured or moved below `EarlyCommitThreshold`), the interactive reveal (the current screen tracking the finger live, clamped to the panel width and never inverting when a gesture wobbles back past its own start, commit past `SwipeThreshold`, cancel-and-spring-back under it, and the same in the forward/redo direction), the discrete tab-paging fallback when there is nothing to reveal (including that it stops at the first and last tab rather than wrapping), that a committed swipe does *not* navigate until the 280ms easing finishes, and `AnimateGoBack` reusing the same commit path while doing nothing at all with no history.

   This needed the full-`MainViewModel` wiring that had been sitting privately inside `MainViewModelSidebarNavigationTests`, so it is now `TestSupport/MainViewModelHarness` — also the obvious starting point for Tier 5.6. One test-only accommodation: the panel is given a `Brushes.Transparent` background, because each screen's own opaque `AppBackgroundBrush` is an `App.axaml` resource that does not resolve under this suite's bare `Application`, leaving nothing hit-testable for a pointer event to land on.

   Confirmed to have teeth by mutation: removing the vertical-drag rejection, the early-commit threshold, or the release threshold; unclamping the live drag; swapping the back/forward commits; inverting the discrete swipe direction; navigating immediately instead of after the easing; leaving a cancelled swipe stranded mid-drag; and dropping `AnimateGoBack`'s `CanGoBack` gate each fail a test.
9. ~~**UI tests** (already on `todo.txt`): `Avalonia.Headless` can drive `MusicListView` virtualization and `ScreenStackPanel` navigation without a display.~~ **DONE** — both halves, by the work above rather than as a separate exercise: `MusicListPanelTests` (§5.5) drives the virtualization against a real measure/arrange pass, and `ScreenStackPanelSwipeTests` (§5.8) drives navigation through a shown headless window's input pipeline. `MusicListViewGestureTests` does the same for the track list's own gestures.

   Two things learned doing it, worth knowing before writing more of these. `Dispatcher.UIThread.RunJobs()` drains queued operations but does **not** advance the timer queue, so anything driven by a `DispatcherTimer` (the download spinner, `ScreenStackPanel`'s easing) reads as inert whether or not its timer is alive — use `Dispatcher.UIThread.MainLoop(cts.Token)` with a short cancellation instead. And hit testing needs something opaque: controls that rely on an `App.axaml` resource for their background (every mobile screen, via `AppBackgroundBrush`) are invisible to the pointer under this suite's bare `Application`, so the container under test needs an explicit background for input to reach it at all.

---

## Other notes worth folding into future work

- **`async void` on non-event-handler paths**: `MainViewModel.ForceSyncNow` is bound directly to a command, plus `ScheduleFilter` and seven methods on `MobileMainViewModel` including `SwipeBack`/`SwipeForward`/`ReorderCurrentPlaylistTrack`. A throw in any of these tears down the process — `TaskScheduler.UnobservedTaskException` does not observe `async void`.
- **`IAudioManager` is silently partial on WASM**: `WebAudioManager` no-ops `SetUpcoming` and `ApplyEqualizer` with no compile-time or runtime signal that a platform drops those features. Worth a capability flag before `Flower.Web` grows.
- **Seek drift**: `GaplessCoordinator.Seek` pre-negates the read split by the *requested* target; if LibVLC lands elsewhere (non-keyframe-aligned lossy seek) nothing re-synchronizes, so the scrubber can drift from the audio.
- **No connection reuse in the sync path** — `ConnectionClose = true` on every request, a documented workaround for `HttpListener`/iOS-backgrounding quirks. Even a three-request sync session pays three full handshakes. A permanent cost of the current transport, not a bug to fix in place.

---

## Roadmap impact

| Planned work | Blocked/complicated by | Unblocked by |
|---|---|---|
| Streaming providers (`IMusicProvider`, `Track.Source`) — `STREAMING-SERVICES-PLAN.md` | `Path` as identity; no `Source` field; credentials would land in `settings.json` | §0.2 (done), §4.1, a new `ISecretStore` |
| Push sync instead of polling — `todo.txt` | Full-manifest-only protocol, no version/ETag, sync triggered only by local events | §1.4 (done) — token as ETag, and the existing `/info` poll carries it |
| Family/friends read-only accounts — `todo.txt` | `Flower.Server` has no `User` table; `PlayCount` is a single global column | §4.1 shared schema, per-user play counts |
| Liked songs / smart playlists / "downloaded only" | No queryable store; `Starred` exists server-side only | §4.1 |
| Track last-played per song — `todo.txt` | `LastPlayedAt` exists but every write rewrites 17.9 MB | §1.1 |
| Export playlist with actual songs, playlist folders | Playlists persisted by `Path` and dropped placeholder tracks | §0.3 (done) |
| AirPlay/Bluetooth device picker — `AIRPLAY-BLUETOOTH-PLAN.md` | `IAudioSink` has no device-enumeration concept | Additive; design the seam when §4.2 touches the audio manager |
| CI benchmarks — `PERFORMANCE-TRACKING-PLAN.md` | — | Do it *after* Tier 1 so the baselines mean something |

## Suggested order

1. **Tier 0** — done.
2. ~~**Tier 1.1**~~ — done, on the JSON layer. The real split lands with 4.1.
3. ~~**Tier 5.1**~~ — done.
4. ~~**Tier 1.4**~~ — done: ETag/`If-None-Match` on the manifest, a memoized album-art hash, and server-side changes surfaced through the existing `/info` poll.
5. ~~**Tier 3**~~ — done.
6. ~~**Tier 2.5**~~ — done: sentinels deleted (no users to be compatible with), EF migrations added to `Flower.Server`.
7. ~~**Tier 5.2**~~ — done: 22 real-socket round-trip tests over `SyncHttpServer`'s whole route table.
8. ~~**Tier 2.1**~~ — done: one shared `SubsonicIdentity`, one duration rounding, and `Child.Id` demoted from `SyncKey` to `Track.Id`.
9. ~~**Tier 4.4** — range support on `SyncHttpServer` streaming.~~ **Done.** Ranged serving, resumable downloads, no more orphaned partials.
10. ~~**Tier 2.2** — the two hand-copied signature verifiers and the three copies of album-art fallback.~~ **Done.** Both collapsed into `Flower.Core`; the album-art copies had already drifted into a real cross-implementation bug.
11. ~~**Tier 2.4** — `Library.Playlists` unlocked while `Library.Tracks` is locked.~~ **Done.** Copy-on-write under the same lock, persistence made structural via `Library.PlaylistsChanged`, and a silent drag-reorder-never-syncs bug fixed on the way. §2.3's DI cleanup followed and closed out Tier 2.
12. ~~**Tier 2.3** — service-location DI and a 330-line hand-wired composition root.~~ **Done**, except the Views/Controls layer's own `Ioc.Default` use and the never-unsubscribed event handlers, which are 4.2's decomposition rather than wiring. `Bootstrap` is registration-by-type now, and `AlbumArtLoader`'s hidden dependencies became constructor parameters, which is what finally made its peer-fetch path testable.
13. ~~**Tier 5.3/5.4/5.5/5.7** — `Importer`, `AlbumArtLoader`, `MusicListPanel`, `ColumnManager.Reorder`.~~ **Done.** 50 tests, all mutation-checked; `MusicListView`'s own selection/drag gestures and `Library.ReplacePlaylists`' short-circuit are what remains of Tier 5's mid-priority items.
14. **Tier 4.1 — the SQLite migration.** Recommended next: it is the largest remaining correctness/performance lever (see §4.1), and 4.2/4.3 want to be done after it, not before.
15. **Tier 4.2/4.3** — ViewModel and code-behind decomposition, last, when the seams are visible.
