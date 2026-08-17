# Architecture Review — Findings and Remediation

Whole-codebase review (August 2026) of structure, class design, data structures, algorithms, performance, latent bugs, duplicated sources of truth, and test coverage — read against the roadmap in the other `docs/*.md` files and `todo.txt`.

**Status: Tier 0 implemented. Tier 1 implemented except 1.4 and two deferred 1.5 items. Tier 2.5 implemented. Tier 3 implemented. Tier 5.1 implemented. The rest of Tier 2, Tier 4, and the rest of Tier 5 documented, not started.** Unlike the other plan docs, this one is a standing backlog rather than a single initiative — each tier below records its own state, and items should be struck off here as they land rather than moved elsewhere.

## Scale reality check

Measured against the real 16k-track development library, not estimated:

| Fact | Value |
|---|---|
| `library.json` | **17.9 MB**, 16,116 tracks, `WriteIndented = true` — since Tier 1.1, unindented and null-omitting |
| Rewritten in full | was on **every track start** and **every track end**; since Tier 1.1, coalesced behind a 3s debounce |
| `Flower.Server` test coverage | was zero; 55 tests as of Tier 5.1 |
| Event unsubscriptions (`-=`) in `Flower/ViewModels` + `Flower/Services` | 0 |
| Tests at review time | 478 — 413 in `Flower.Tests`, 65 in `Flower.Server.Tests` (393 before Tier 1, 461 before Tier 3; Tier 2.5 net -1, deleting two legacy-shape tests and adding one) |

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

## Tier 1 — performance — MOSTLY DONE (1.4 not started; two 1.5 items deferred)

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

### 1.4 Sync transfers the whole manifest every time, and re-hashes all album art to build it

`GET /api/flower/v1/library` returns the complete catalog (6-8 MB at 16k tracks by `SYNC-PLAN.md`'s own estimate) with no ETag, version, or `If-Modified-Since`, and is re-pulled on the 5s-debounced local-change path — so editing one playlist track re-downloads the peer's entire library manifest. Building that manifest calls `ComputeAlbumArtHash` once per album, each opening the file with TagLib and SHA-256'ing the art bytes: ~1,400 file opens and hashes per request, uncached.

Meanwhile a *server-side* change is never noticed while both apps stay running — sync fires only on first mDNS contact or a debounced **local** change, and the 5s `/info` poll checks reachability/trust/alias only. This is `todo.txt`'s "push library sync events instead of polling", and it is a correctness gap, not an optimization.

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

## Tier 2 — structural: multiple sources of truth — 2.5 DONE, rest NOT STARTED

### 2.1 Four track models and five identity schemes

| Layer | Model | Identity |
|---|---|---|
| Client domain | `Flower.Core.Models.Track` | `Id` (as of Tier 0), previously `Path` |
| Cross-device sync | same `Track` | `SyncKey` = normalized Title\|Artist\|Album\|rounded seconds |
| In-memory UI navigation | same `Track` | was **accidental** full-record value equality — fixed in Tier 0 |
| Wire (P2P + Subsonic) | `Child`/`AlbumID3` DTOs, `PlaylistSyncTrackDto` | `al:{norm}\|{norm}` |
| Server | `Flower.Server.Data.TrackEntity` | `al-{hash}` |

The two album-ID schemes differ by a punctuation character and nothing enforces the distinction (`LibraryOpenSubsonicMapper.AlbumId` vs `SubsonicIdentity.AlbumId`). `SubsonicMapper.ToChild` re-implements duration rounding inline as `Math.Round(t.DurationSeconds)` instead of calling `Track.RoundedSeconds` — exactly the bug class `Track.RoundedSeconds`' own doc comment records having already been hit and fixed once. `TrackEntity` has a `Starred` column the client `Track` has no concept of.

**Direction:** `Track.Id` is now the single identity; demote `SyncKey` to a *matching heuristic* used at import and first pairing, not an identity. Move the canonical `(artist, album) → id` function and the DTO mapping into `Flower.Core` so client and server share one implementation.

### 2.2 Auth and album-art lookup implemented two-to-three times

`SyncHttpServer.VerifySelfSigned`/`VerifyTrustedPeer` vs `Flower.Server/Services/DeviceSignatureAuth` are near-identical hand copies, down to the `GetIdentityValue` header/query fallback helper written twice. Album-art file fallback exists in `AlbumArtLoader.TryGetLocalArtBytes`, `SyncHttpServer.HandleGetCoverArtAsync`'s sniffing, and `SubsonicEndpoints`' own private copy — three places to fix when someone adds a format.

### 2.3 DI is service location, and the composition root is a 330-line method

Every service in `App.axaml.cs::Bootstrap` is `new`'d by hand then registered as an *instance*, so constructor injection is bypassed and adding a dependency means editing `Bootstrap`. `BuildServiceProvider()` is called twice — the first container exists solely to fetch an `ILoggerFactory` and is then leaked. Logging is service-located via `AppLogging.CreateTypedLogger<T>` throughout.

`Ioc.Default` is used as a full service locator across 15 files / 44 call sites; every control and window resolves its own dependencies. Worst case: **`AlbumArtLoader` is a `static` class reaching into `Ioc.Default.GetService<PeerTrackResolver>()`/`GetService<DeviceIdentity>()` from inside a static method**, hiding its dependencies entirely and making it untestable without a live container — which is precisely why it has no tests despite carrying the most production-bug narratives in the codebase.

`Flower/Extensions/ServiceCollectionExtensions.cs` is dead scaffolding: `AddCommonServices` has a commented-out body and zero callers.

**Event subscriptions are never unsubscribed anywhere.** `MainViewModel`'s constructor alone subscribes to eleven event sources and implements no `IDisposable`. Harmless *only* because it is a process-lifetime singleton — nothing enforces that, and it blocks per-test reconstruction.

### 2.4 `Library.Playlists` is unlocked while `Library.Tracks` is locked

`AddPlaylist`/`RemovePlaylist`/`ReplacePlaylists` take no lock despite `ReplacePlaylists` being called from the sync path concurrently with UI-thread mutations — the same threat model that motivated `_lock` for `Tracks`, inconsistently applied. Persistence is also caller-responsibility; nothing structurally guarantees a mutation is followed by a save.

### 2.5 No schema version anywhere — DONE

Backward compatibility was ad hoc sentinel detection invented independently three times: `PlaylistRecord.Id = default`, `TrustedPeer.PublicKey = ""`, and `DeviceIdentityStore.Load`'s alias-backfill/fingerprint-correction. `Flower.Server` was worse — `EnsureCreatedAsync()` with no EF migrations at all, so **any** schema change wiped a self-hoster's database.

**Resolution.** The JSON side needed no versioning scheme at all, because there is nothing to be compatible *with* (see `CLAUDE.md`, "No Users Yet") — every sentinel was deleted rather than formalized:

- `PlaylistRecord.Id`/`UpdatedAt` are plain required fields; the `Guid.Empty`/`default` re-minting in `Load` is gone. `PlaylistTrackRecord` is now just `(Guid Id)` — the `Path` fallback and the pre-`Track.Id` `TrackPaths` list are both deleted, along with `Load`'s whole by-path index. An entry whose id doesn't resolve is dropped, full stop.
- `TrustedPeer.PublicKey` is a required constructor parameter, not `= ""`. `GetPublicKey` is a plain lookup: an approval without a key is not a representable state anymore.
- `DeviceIdentityStore.Load` no longer backfills a missing alias. The fingerprint correction *stays* — it is not a migration, it's the runtime response to a regenerated signing key (`DeviceKeyStore`), and its comment now says so.
- `Track.Id`'s initializer stays for the same reason (every `Track` needs an id from construction); only its "the initializer is also the migration" comment went.

`Flower.Server` got the real thing instead: `Microsoft.EntityFrameworkCore.Design` was already referenced, so `Data/Migrations/` now holds an `InitialCreate` migration plus the model snapshot, and startup calls `MigrateAsync()` instead of `EnsureCreatedAsync()`. Subsequent entity changes go through `dotnet ef migrations add <Name> -p Flower.Server -s Flower.Server -o Data/Migrations`. **An existing dev `flower.db` created by `EnsureCreated` has no `__EFMigrationsHistory` table and must be deleted once** — `MigrateAsync` would otherwise try to create tables that already exist.

---

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

## Tier 4 — rewrite candidates — NOT STARTED

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

### 4.4 `SyncHttpServer` streaming has no range support

`HandleStreamAsync` sets `ContentLength64` and copies the whole file — no `Range` handling, so a dropped mobile download restarts at byte 0 and seeking within a peer-streamed track can't use partial content. `Flower.Server` gets this right via `Results.File(..., enableRangeProcessing: true)`. Related: `OpenSubsonicClient.DownloadTrackAsync` creates the destination file and never deletes the partial on failure, leaking orphans no code path can reach.

---

## Tier 5 — test coverage — 5.1 DONE, rest NOT STARTED

CI ran `dotnet test Flower.Tests`, which builds only `Flower.Tests → Flower → Flower.Core`: **`Flower.Server` and `Flower.CLI` were never compiled by CI**, let alone tested. (The `tests.yml` comment claiming `Flower.csproj` multi-targets `net10.0;net9.0` was stale — it targets `net10.0` only. Corrected.)

**What that hid, found while fixing it:** `Flower.Server/Data/`'s four entity sources — `FlowerDbContext.cs`, `TrackEntity.cs`, `PlaylistEntity.cs`, `PlaylistTrackEntity.cs` — **were never committed at all**. The `.gitignore` rule `Flower.Server/data/`, meant for the default runtime `DataDirectory`, also matched the *source* folder `Flower.Server/Data/` on a case-insensitive filesystem (macOS, Windows), so `git add` silently skipped them. `Flower.Server` has therefore been unbuildable from a clean checkout since it was scaffolded, and nothing noticed because no CI job ever built it. The rule is now per-artifact (`[Dd]ata/*.db`, `*.db-shm`, `*.db-wal`, `*.json`, `logs/`) rather than a directory exclusion — a directory exclusion would also have made a `!*.cs` negation impossible, since git does not descend into an excluded directory.

Highest-value additions, roughly in priority order:

1. ~~**A `Flower.Server` test project at all.**~~ **DONE** — `Flower.Server.Tests`, 55 tests. `SubsonicAuth` (salted-token acceptance, salt reuse, wrong password/username, each missing parameter, and that plaintext `p=` auth stays unsupported), `AdminAuthService` (fixed-time credential comparison including the prefix case, token issue/validate, tokens not surviving a restart), `PairingCodeService` (single-use, case/whitespace tolerance, ambiguous-character exclusion, independence between outstanding codes), and `LanGuard` (private/loopback/CGNAT allowed, public refused with 403 *before* auth runs).

   Plus endpoint tests over the real route table via `WebApplicationFactory` + temp SQLite, covering every Tier 1.3 query shape. Requests go through `TestServer.SendAsync` rather than an `HttpClient`, because `LanGuard` rejects the null `RemoteIpAddress` an `HttpClient`-issued test request carries — that is real behaviour worth testing rather than configuring away, so the harness presents a loopback client instead of bypassing the middleware.

   Confirmed to have teeth by mutation: reintroducing the `Max(DateTimeOffset)` that shipped in 1.3 fails two of these tests.

   New CI jobs: `server-test` (runs the above) and `build-rest` (builds `Flower.Desktop` and `Flower.CLI`), so every project in the solution is now compiled by CI.
2. **A real socket round trip**: `SyncHttpServer` against `LibrarySyncService`/`PlaylistSyncService`. Today `FakePeerHttpServer` substitutes for the real listener, so the route table, rate limits, and trust gates in `HandleRequestAsync` are validated only by hand.
3. **`Importer`** — zero coverage. Dedup across overlapping paths, extension filtering, `IsCompilation` per-format branching, skip-unreadable-file behaviour.
4. **`AlbumArtLoader`** — zero coverage, despite carrying the most "confirmed on a real device" bug narratives in the codebase (cache-key collision, corrupt-image fallback, remote fetch/disk cache).
5. **`MusicListView`/`MusicListPanel`** — the highest-risk untested UI surface given it is entirely hand-rolled: virtualization range math, album-group-leader spanning, shift-range/ctrl-toggle selection, header drag-reorder.
6. **`MainViewModel`'s sync/pairing/device-row state machine** (~900 lines) — `MainViewModelSidebarNavigationTests` is a single debounce-timing regression test and touches none of it.
7. **`Library.ReplacePlaylists`' `PlaylistsUnchanged` short-circuit** and **`ColumnManager.Reorder`** — nontrivial algorithms, no tests.
8. **No dedicated tests** for `CurrentlyPlayingControlViewModel`, `TrackRowViewModel`, `VolumeControlViewModel`, `EqualizerViewModel`, `LogViewModel`, `SidebarItem`, or `ScreenStackPanel`'s swipe state machine.
9. **UI tests** (already on `todo.txt`): `Avalonia.Headless` can drive `MusicListView` virtualization and `ScreenStackPanel` navigation without a display.

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
| Push sync instead of polling — `todo.txt` | Full-manifest-only protocol, no version/ETag, sync triggered only by local events | §1.4 manifest versioning + a server→client change notification |
| Family/friends read-only accounts — `todo.txt` | `Flower.Server` has no `User` table; `PlayCount` is a single global column | §4.1 shared schema, per-user play counts |
| Liked songs / smart playlists / "downloaded only" | No queryable store; `Starred` exists server-side only | §4.1 + §2.1 |
| Track last-played per song — `todo.txt` | `LastPlayedAt` exists but every write rewrites 17.9 MB | §1.1 |
| Export playlist with actual songs, playlist folders | Playlists persisted by `Path` and dropped placeholder tracks | §0.3 (done) |
| AirPlay/Bluetooth device picker — `AIRPLAY-BLUETOOTH-PLAN.md` | `IAudioSink` has no device-enumeration concept | Additive; design the seam when §4.2 touches the audio manager |
| CI benchmarks — `PERFORMANCE-TRACKING-PLAN.md` | — | Do it *after* Tier 1 so the baselines mean something |

## Suggested order

1. **Tier 0** — done.
2. ~~**Tier 1.1**~~ — done, on the JSON layer. The real split lands with 4.1.
3. ~~**Tier 5.1**~~ — done.
4. **Tier 1.4** — manifest versioning/ETag and push-based sync events; the one Tier 1 section untouched. 1.2, 1.3 and most of 1.5 are done; row diffing is what is left.
5. ~~**Tier 3**~~ — done.
6. ~~**Tier 2.5**~~ — done: sentinels deleted (no users to be compatible with), EF migrations added to `Flower.Server`.
7. **Tier 4.1** — the SQLite migration, once its consumers are actually next up.
8. **Tier 4.2/4.3** — ViewModel and code-behind decomposition, last, when the seams are visible.
