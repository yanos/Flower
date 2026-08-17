# Flower — Architecture Review & Remediation Roadmap

## Context

Flower is a ~33k-line, 9-project cross-platform music player (Avalonia 12 / .NET 10) that has grown very fast. In the last handful of commits it gained a shared `Flower.Core`, a self-hosted `Flower.Server` (EF Core + full OpenSubsonic REST surface), and an in-flight `Flower.Web` WASM head — on top of an existing LAN P2P sync stack, a hand-rolled gapless audio pipeline, a custom virtualizing track list, and a separate mobile UI.

The engineering quality of individual pieces is high, and the code is unusually well documented — most non-obvious decisions carry an inline narrative of the real bug that motivated them. What has *not* kept up is the foundation: the domain model, the identity scheme, and the persistence layer were designed for a single-machine local player and are now load-bearing for a distributed system with four different track representations.

This review answers **what works, what needs improvement, and what needs a rewrite**, with the roadmap in `docs/*.md` and `docs/todo.txt` (streaming providers, push sync, family/read-only accounts, smart playlists, liked songs, downloaded-only filter, UI tests) treated as the forward constraint.

**Scale reality check** — measured against the real dev library, not estimated:

| Fact | Value |
|---|---|
| `library.json` | **17.9 MB**, 16,116 tracks, `WriteIndented = true` |
| Rewritten in full | on **every track start** and **every track end** |
| `Flower.Server` test coverage | **zero** (`Flower.Tests` references only `Flower/Flower.csproj`) |
| Event unsubscriptions (`-=`) in `Flower/ViewModels` + `Flower/Services` | **0** |
| Total tests | 335 across 39 files |

## Status

**Tier 0: implemented. Tier 1: implemented except §1.4, plus two deliberately deferred §1.5 items. Tiers 2-5: documented, not started.**

This file is the original review and proposal, kept as written. Per *Scope of this change* below, the living tracker is **`docs/ARCHITECTURE-REVIEW.md`** — it carries the per-item status, what each fix actually turned out to be, and the findings that only surfaced during implementation. Update that file as things land, not this one; the execution sections here (*Tier 0 execution*, *Tier 1 execution*) record what was planned and, for Tier 1, what was done.

Delivered so far:

- **Tier 0** (all ten items) — atomic persistence, `Track` identity by `Guid`, playlist re-resolution, delete-vs-edit conflicts, and the six small fixes. Commit `b1695f5`.
- **Tier 1.1** — unindented/null-omitting JSON, coalesced library saves, and a distinct `Library.TrackStatsChanged` so a play no longer rebuilds the UI or triggers a peer sync.
- **Tier 1.2** — album art decoded at a display-sized cap, with a strong LRU and pruning of the previously unbounded weak cache.
- **Tier 1.3** — `Flower.Server` browse queries grouped, aggregated and paginated in SQL. Measured against the real 16,116-track library: `getAlbumList2` 187ms → 29ms, `search3` 172ms → 31ms.
- **Tier 1.5** — cached `SyncKey`, O(1) path lookups in `Library`, cached `TrustedPeerStore`, precomputed album-group leaders, desktop no longer building two discarded tile grids per keystroke, allocation-free `SortKey`, diffed pair-button notifications.

Still open, in the order worth doing them:

1. **Tier 5.1** — a `Flower.Server` test project and CI jobs building every project. Promoted above the remaining Tier 1 work: §1.3 shipped two defects that compiled cleanly and failed only at request time (SQLite cannot `Max` a `DateTimeOffset`; EF translates a grouped aggregate projection only as a member initializer, never a constructor call), and nothing in the suite would have caught either.
2. **Tier 1.4** — manifest ETag/versioning and push-based sync events. The one Tier 1 section untouched, and partly a correctness gap rather than an optimization: a server-side change is still never noticed while both apps stay running.
3. **Tier 1.5, deferred** — row diffing in `TrackListBuilder.Build`, and with it the `AlbumArt` discard on every rebuild (mitigated by §1.2's LRU, not fixed). The largest remaining Tier 1 item and the only one needing structural change to a hot, well-covered path.
4. **Tier 3** — server rate limiting, default-password handling, log-push exposure.
5. **Tier 4.1** — the SQLite migration, once a roadmap item actually demands queryable state. It also subsumes §1.1's real fix (splitting mutable per-track state out of the metadata blob) and §1.3's `DateAdded` value converter.
6. **Tiers 2, 4.2/4.3** — the source-of-truth consolidation and the ViewModel/code-behind decomposition, last, when the seams are visible.

---

## Scope of this change

1. **Write `docs/ARCHITECTURE-REVIEW.md`** — the full findings below, matching the repo's one-file-per-initiative convention, added to the index table in `CLAUDE.md`. It records its own status like the other plan docs, so Tier 1-5 stay tracked after Tier 0 ships.
2. **Implement Tier 0** — the ten correctness / data-loss items, each with its regression test written first. Detailed execution order in *Tier 0 execution* below.

Tier 1-5 are documented but **not** implemented here. The SQLite migration (§4.1) stays the stated end state, explicitly gated behind Tier 0-1 and the roadmap items that actually need it — the JSON layer gets hardened in the meantime rather than abandoned.

---

## What genuinely works — keep it

These are load-bearing and well designed; do not disturb them while fixing the rest.

- **Play-count merge is a correct G-Counter CRDT.** `Track.RemotePlayCounts` keyed by device fingerprint, merged per-key by `Math.Max` (`Flower.Core/Models/Library.cs:249`), with `LibrarySyncMapper.ToPlaceholderTrack` stripping the receiver's own fingerprint out of inbound reports. Idempotent, order-independent, multi-hop safe. No notes.
- **The Phase-4 request signing scheme.** ECDSA P-256 proof-of-possession over canonical method+path+query+body-hash+timestamp+nonce, ±60s skew, per-fingerprint replay guard; `SignatureVerifier.Verify` burns the nonce *before* verifying so a forged attempt can't be retried (`Flower.Core/Services/SignatureVerifier.cs:35`). Sound.
- **`Library`'s `_lock` around `Tracks` read-modify-write**, and the resolve-then-mutate-under-lock pattern in `IncrementPlayCount`/`RecordPlayed`. The reasoning in the comments is correct and the regression is covered by a test.
- **The gapless pipeline's two-LibVLC-core split** and the ring-buffer-read-derived position counter. Both were hard-won; the CLAUDE.md write-ups are accurate.
- **`Flower.Core` extraction itself** — `RateLimiter`, `SignatureVerifier`, `SignedRequestCanonicalizer`, `NonceReplayGuard`, `LanGuard`, `DeviceSigningKey` are genuinely shared. That part of the split worked.
- **Server concurrency choices**: SQLite WAL + `Default Timeout=30` + `IDbContextFactory<T>` per request. Right calls.
- **Test *style*** where it exists: `TestSupport/` fakes, synthetic WAV fixtures, `Avalonia.Headless` integration tests, and the `PlatformDataDirectory` pinning that keeps tests off the real library. The patterns are good — there just isn't enough of it in the right places.

---

## Tier 0 — Data-loss and correctness bugs (fix first) — DONE

### 0.1 `library.json` is written non-atomically, and it is the only copy of irreplaceable data

Every store writes straight over the live file — `File.WriteAllText`/`WriteAllTextAsync`, no temp+rename, no `.bak`:
`LibraryStore.cs:92,127` · `PlaylistStore.cs:84` · `AppSettingsStore.cs:198,207` · `PlaylistSyncStateStore.cs:47` · `DeviceIdentityStore.cs:112,119` · `DeviceNicknameStore.cs:71` · `DeviceKeyStore.cs:98` · `TrustedPeerStore.cs:137,144`.

A crash or forced quit mid-write truncates the file. `LibraryStore.Load` then catches, logs a `Warning`, and returns an **empty list** (`LibraryStore.cs:48-55`) — after which the startup rescan repopulates from disk with `DateAdded = now` and `PlayCount = 0`. Net effect: **every play count, first-seen date, last-played timestamp, and remote-device play count in the library is silently and permanently destroyed**, and "Recently Added" shows the entire library. None of that data exists anywhere else. `DeviceKeyStore` has the same shape with a worse consequence — it regenerates a fresh keypair on corrupt load, permanently breaking trust with every peer.

**Fix:** one shared `AtomicJsonFile` helper in `Flower.Core/Persistence` — write to `<name>.tmp`, `fsync`, `File.Replace(tmp, target, target + ".bak")`; on load, fall back to `.bak`, and on total failure rename the bad file to `.corrupt` and surface it to the user rather than silently starting empty. Route all nine stores through it.

### 0.2 `Track` is a `record` with 40 mutable properties — value equality is used as identity in navigation paths

`Flower.Core/Models/Track.cs:8` declares `public record Track` with every field `{ get; set; }`, including a `Dictionary<string,int>`. Records synthesize value-based `Equals`/`GetHashCode` over all of them, and that synthesized equality is what these call sites actually use:

- `Playlist.GetNextTrack`/`GetPreviousTrack` → `Tracks.IndexOf(currentTrack)` (`Palylist.cs:87,100`)
- `Playlist.RemoveTrack` → `Tracks.Remove(track)` (`Palylist.cs:69`)
- `PlaylistControlViewModel.GetNextTrack`'s shuffle re-roll → `while (candidate == currentTrack)` (`PlaylistControlViewModel.cs:195`)
- `MainView.axaml.cs:1212`, `AlbumGridRowControl.axaml.cs:274` → `tracks.IndexOf(track)` for Track Info navigation
- `Library.MergeSyncedTracks` → `new HashSet<Track>(...)` + `RemoveAll(stale.Contains)` (`Library.cs:231`)

Consequences, all real:
- **The same song twice in a playlist is broken.** `IndexOf` always resolves to the first occurrence, so next/previous and remove act on the wrong one.
- Two tracks with identical tags (untagged files, "Track 01", silence tracks) are indistinguishable to every one of those paths.
- Every `IndexOf` is O(n × 40 field comparisons) — on a 16k queue that is a measurable cost on each advance.
- Mutable fields + hash-based collections is a latent bug the next `Track`-keyed cache will inherit.
- `Track` is `record`-in-name-only: nothing uses `with`; every mutation is in place (`PlayCount++`, `t.Title = v` from `TrackInfoWindow`, `Path` set post-download).

**Fix:** give `Track` a stable surrogate `Id` (Guid, minted at first import, persisted, carried forward by `UpdateTracks` exactly like `DateAdded`), change `record` → `sealed class`, and implement `IEquatable<Track>` on `Id` alone. Replace every `IndexOf`/`Remove`/`HashSet<Track>` with id-based or `ReferenceEquals` lookups. This single change also unblocks §3 (streaming provider tracks that have no `Path`) and the "liked songs"/"smart playlist" roadmap items.

### 0.3 Playlists hold orphaned `Track` instances after every rescan

`PlaylistStore.Load(library.Tracks)` resolves playlist membership to `Track` object references once, at startup (`PlaylistStore.cs:50-61`). The startup rescan then replaces `Library.Tracks` with brand-new instances (`Library.UpdateTracks`). Nothing re-resolves playlists — `TracksUpdated` has exactly two subscribers (`MainViewModel.cs:1267`, `MobileMainViewModel.cs:650`), neither of which touches `Library.Playlists`.

So for the entire session after the first rescan, a playlist's `Track` objects are a different object graph from the library's. Play counts incremented on the library instance never appear in playlist views; `IndexOf` lookups start failing once any field diverges. Additionally `PlaylistStore.SaveAsync` persists only `t.Path != null` tracks (`PlaylistStore.cs:78`) — **a synced-but-not-downloaded track added to a playlist is silently dropped on save.**

**Fix:** re-resolve `Library.Playlists` inside `UpdateTracks` (under the lock) by the new stable `Track.Id`; persist playlist membership by `Id` with `Path`/`SyncKey` as migration fallbacks.

### 0.4 `PlaylistSyncPlanner` silently resolves delete-vs-edit conflicts in favour of delete

`Flower/Services/PlaylistSyncPlanner.cs:58-72`:

```csharp
if (r == null)
{
    var localOnlyKind = baselineFor(id) != null ? Delete : KeepLocal;
    decisions.Add(new PlaylistSyncDecision(id, localOnlyKind, l, null));
    continue;
}
```

If a baseline exists and the remote no longer has the playlist, the decision is `Delete` — **without checking `l.UpdatedAt > baseline`**. A user who edited a playlist locally while the peer deleted it loses the edits with no conflict prompt, even though the edit-vs-edit branch 20 lines below (`:81-90`) correctly does the baseline comparison. The `l == null` branch has the mirror bug.

**Fix:** in both branches, compare against the baseline and raise `Conflict` when the surviving side also changed. `PlaylistSyncPlannerTests` has no case for this — add one first.

### 0.5 `ColumnManager`'s debounce doesn't debounce, and races an unlocked `settings.json` write

`Flower/Controls/ColumnManager.cs`: `ScheduleSave()` is `_pendingSave = SaveAsync();` — a new unawaited `Task.Delay(500)` chain per call, never cancelling the previous, and `_pendingSave` is never read (dead field, unobserved faults). Column `Width` changes call it too, so a resize drag spawns dozens of concurrent saves. `AppSettingsStore.SaveAsync` has **no write lock** (`AppSettingsStore.cs:192-198`) — unlike `LibraryStore`, which added a `SemaphoreSlim` precisely because of this failure mode (silent corruption on Unix, `IOException` on Windows).

**Fix:** real `CancellationTokenSource`-based debounce; add the same write-lock (or the `AtomicJsonFile` from §0.1) to every store, not just `LibraryStore`.

### 0.6 Descending sort reverses secondary keys

`TrackListBuilder.Sort` ends with `asc ? ordered : ordered.Reverse()` (`TrackListBuilder.cs:63`). `Enumerable.Reverse` reverses *ties* too, so sorting by Album descending also reverses disc/track order **within** each album. Should be `OrderByDescending` on the primary key with the `ThenBy` chain kept ascending.

### 0.7 Native LibVLC handles leak on every track change

`ITrackDecoder : IDisposable` (`Flower/Manager/ITrackDecoder.cs:14`), and `TrackDecoder.Dispose()` (`:333-339`) is what actually disposes `_media`, `_mediaPlayer`, and `_watchdog`. But **`Dispose()` is never called on a decoder anywhere in the pipeline** — `GaplessCoordinator` only ever calls `Retire()` (`:185,214,459,490,503`), and `Retire()` (`TrackDecoder.cs:240-262`) stops the watchdog and offloads `mediaPlayer.Stop()` to a detached `Task.Run`, then returns without disposing anything.

Every track change constructs a fresh `TrackDecoder` (new `Media` + `MediaPlayer`) and retires the previous one, so native handles accumulate for the life of the process. A long listening session leaks hundreds. Either `Retire()` must dispose after the detached `Stop()` completes, or `Dispose()` is dead code that should be deleted — the ambiguity itself is the bug.

### 0.8 Leaked 60 fps `DispatcherTimer` per downloading row

`TrackRowViewModel.StartSpin()` (`:139-146`) creates a 16 ms `DispatcherTimer`; `StopSpin` runs only from the `IsDownloading` setter. `Rows` is replaced wholesale on every rescan, search, and track change (`MainViewModel.cs:2487`), so any row spinning at that moment is dropped with its timer still registered on the dispatcher — which keeps the VM alive and keeps ticking at 60 fps forever. Batch-downloading an album on mobile accumulates these.

### 0.9 `ScheduleContentSync` runs off the UI thread and enumerates an `ObservableCollection` the UI mutates

`MainViewModel.cs:1267-1277` marshals `PopulateTracks` via `Dispatcher.UIThread.Post` but calls `ScheduleContentSync()` on the raising thread. `Library.TracksUpdated` can be raised from a LibVLC callback thread (`Library.cs:14-20`; reached via `PlaylistControlViewModel.EndReached` → `NotifyTrackChanged`). `ScheduleContentSync` → `DebouncedContentSyncAsync` awaits and resumes on a thread-pool thread, then `RunPendingDeviceSyncs()` (`:186-223`) LINQs over `_sidebarItems` — a plain `ObservableCollection<SidebarItem>` that the UI thread mutates through the (correctly marshalled) `AddOrUpdateDeviceSidebarItem`/`RemoveDeviceItem`. Narrow window, real race.

### 0.10 Decode failure is indistinguishable from end-of-track

`GaplessCoordinator` funnels `TrackDecoder.Faulted` and `Drained` into the same `HandleDrainedOrFaulted` (`:377-439`). A corrupt or unsupported file mid-playlist is handled exactly like a natural end: promote the armed track, or stop. `PlaylistControlViewModel.EndReached` can't tell them apart, so playback silently skips or halts with only a `LogWarning` — and the play count is incremented as if the track had played. This also blocks the `AUDIOPHILE-PLAN.md` DSD/APE work, which explicitly wants a user-facing "unsupported format" message.

---

## Tier 0 execution

Ordered so each step lands on a green suite. Test first in every case — several of these are invisible to the current suite by construction.

**Step 1 — atomic persistence (§0.1).** New `Flower.Core/Persistence/AtomicJsonFile.cs`: `Write(path, json)` / `WriteAsync` doing temp → flush → `File.Replace(tmp, target, target + ".bak")`, and `TryRead(path)` falling back to `.bak`, then renaming an unreadable file to `.corrupt` and reporting it. Route all nine stores through it (`LibraryStore`, `PlaylistStore`, `AppSettingsStore`, `PlaylistSyncStateStore`, `DeviceIdentityStore`, `DeviceNicknameStore`, `DeviceKeyStore`, `TrustedPeerStore`, `ClientLogStore`), and give each the `SemaphoreSlim` write-lock `LibraryStore` already has. Tests extend `StoreRoundTripTests` (which already pins `PlatformDataDirectory` — reuse that harness): truncated file recovers from `.bak`; corrupt-with-no-bak surfaces rather than silently emptying; concurrent writes don't interleave.

**Step 2 — `Track` identity (§0.2).** Add `public Guid Id { get; set; }` to `Track`, minted at import, carried forward by `UpdateTracks` alongside `DateAdded` (see step 3's shared carry-forward method). Change `public record Track` → `public sealed class Track : IEquatable<Track>` with `Equals`/`GetHashCode` on `Id` alone. Then fix every consumer of the old value equality: `Playlist.GetNextTrack`/`GetPreviousTrack`/`RemoveTrack` (`Palylist.cs:69,87,100`), `PlaylistControlViewModel.GetNextTrack`'s shuffle re-roll (`:195`), `MainView.axaml.cs:1212`, `AlbumGridRowControl.axaml.cs:274`, `Library.MergeSyncedTracks`'s `HashSet<Track>` (`:231`). Migration: tracks loaded from an existing `library.json` with `Id == Guid.Empty` get one minted on load. Tests: two value-identical-but-distinct tracks in one playlist — next/previous/remove must each act on the right instance (this is the case that exists nowhere in the suite today).

**Step 3 — playlist re-resolution (§0.3).** Extract the duplicated carry-forward block in `Library.UpdateTracks` (`:87-91` and `:95-99`) into one `CarryForwardMutableState(from, to)` — the fix for §2.5 falls out for free and step 2 needs it for `Id`. Then re-resolve `Library.Playlists` against the new track instances inside `UpdateTracks`, under `_lock`. Change `PlaylistStore` to persist by `Id` (keeping `Path` as a migration fallback) and **stop dropping `Path == null` tracks on save** (`:78`). Tests: rescan → playlist and library share instances; a placeholder track in a playlist survives a save/load round trip.

**Step 4 — sync conflict (§0.4).** In `PlaylistSyncPlanner.Plan`, both single-sided branches (`:58-72`) compare the surviving side's `UpdatedAt` against the baseline and return `Conflict` when it also changed. Test written first in `PlaylistSyncPlannerTests`: baseline exists, remote deleted, local edited → `Conflict`, not `Delete`; and the mirror case.

**Step 5 — the small independent fixes.** Each is self-contained and can land in any order:
- §0.5 `ColumnManager.ScheduleSave` → real `CancellationTokenSource` debounce; delete the dead `_pendingSave`.
- §0.6 `TrackListBuilder.Sort` → `OrderByDescending` on the primary key with `ThenBy` chains kept ascending, instead of `.Reverse()`. Test: album-descending keeps track numbers ascending within each album.
- §0.7 `TrackDecoder.Retire` → dispose `_media`/`_mediaPlayer`/`_watchdog` in the detached `Task.Run` continuation after `Stop()` returns; if that proves unsafe, delete `Dispose()` and document why. Verify against `GaplessCoordinatorRealDecodeTests`.
- §0.8 `TrackRowViewModel` → stop the spin timer when the row leaves the collection (simplest: make it `IDisposable` and have `RebuildRowsAsync` dispose the outgoing rows, or drive the animation from one shared clock as §1 suggests).
- §0.9 `MainViewModel.cs:1267-1277` → move `ScheduleContentSync()` inside the `Dispatcher.UIThread.Post` callback.
- §0.10 `GaplessCoordinator` → give `Faulted` its own path, surface a `TrackFailed` event through `IAudioManager`, and have `PlaylistControlViewModel` skip without counting a play. This is also the seam `AUDIOPHILE-PLAN.md` §3 needs for "unsupported format" messaging.

**Verification for the whole tier:** `dotnet build Flower.sln`, then `dotnet test Flower.Tests/Flower.Tests.csproj --filter Category!=RequiresLibVLC`, then a full run with local VLC for the audio-pipeline changes in §0.7/§0.10. Plus the manual checks listed at the bottom.

---

## Tier 1 — Performance — DONE except §1.4 and two deferred §1.5 items

### 1.1 A full 17.9 MB library serialization on every track change (the headline problem)

`PlaylistControlViewModel.Play` (`:226-228`) does `RecordPlayed` → `NotifyTrackChanged()` → `_ = _libraryStore.SaveAsync(_library.Tracks)`. The `EndReached` handler (`:133-139`) does the same again. Per song change that is:

1. **2× full serialize + write of 17.9 MB** of indented JSON (~60% of which is `null` fields being spelled out).
2. **2× `TracksUpdated`** → `PopulateTracks()` (`MainViewModel.cs:2417`): a 16k-element list copy, `RebuildSubListItems`, `RebuildAlbumGrids` (full `GroupBy` over 16k tracks), then a debounced full filter+sort+**16k `TrackRowViewModel` allocations**.
3. **2× `ScheduleContentSync()`** (`MainViewModel.cs:1274`) — a play-count bump is indistinguishable from a library change, so **playing a song triggers a network sync with the paired peer.**

There is also a genuine lost-update race despite the semaphore: `SaveAsync` takes its snapshot as a *parameter* at call time but serializes before acquiring `_writeLock` (`LibraryStore.cs:79-81`), so an earlier-queued call carrying stale data can physically land last.

**Fix, in order of payoff:**
- Split "mutable per-track state" (play counts, `LastPlayedAt`, `DateAdded`, `RemotePlayCounts`, future `Starred`) out of the metadata blob into an append-friendly sidecar, or move the whole library to SQLite (see §4.1). A play-count bump should write ~100 bytes, not 17.9 MB.
- Immediately, and cheaply: `WriteIndented = false` + `DefaultIgnoreCondition = WhenWritingNull` on `FlowerJsonContext` (~17.9 MB → ~4 MB), and coalesce saves behind a dirty-flag + periodic flush instead of write-per-event.
- Give `Library` a distinct `TrackStatsChanged` event so a play-count bump doesn't trigger a full `PopulateTracks` or a peer sync.

### 1.2 Album art is decoded at full source resolution

`AlbumArtLoader.LoadLocalBitmap` (`:102-103`) does `new Bitmap(ms)` with no downscale. Modern embedded art is routinely 1400×1400+ → ~7.8 MB decoded RGBA per bitmap, for tiles rendered at ~120 px. An album grid showing 40 tiles can hold 300 MB of decoded bitmaps. The `WeakReference` cache softens it but jetsam on iOS will not wait for a GC.

**Fix:** `Bitmap.DecodeToWidth(stream, targetPx)` sized to the largest actual display size; keep a small strong-referenced LRU for the visible set rather than an unbounded `ConcurrentDictionary<string, WeakReference>` that is never pruned (`:47`).

### 1.3 `Flower.Server` materializes the entire `Tracks` table on every browse request

`SubsonicEndpoints.cs:158` — `var groups = (await db.Tracks.ToListAsync()).GroupBy(t => t.AlbumId);` in `GetAlbumList2`. `GetArtists` (`:87`) does the same with a projection. `Search3` (`:188`) pulls all SQL-side matches then re-filters in memory with different case semantics than the SQL `LIKE` (so the two passes genuinely disagree). Every one of these is a full table scan + full materialization + in-memory group/sort/paginate, per request, with no caching — against the 16k-track library the plan itself names as the target scale. The indices on `Path`/`ArtistId`/`AlbumId` exist but are never exercised by these query shapes.

### 1.4 Sync transfers the whole manifest every time, and re-hashes all album art to build it

`GET /api/flower/v1/library` returns the complete catalog (SYNC-PLAN's own estimate: 6-8 MB at 16k tracks) with no ETag/version/If-Modified-Since. It is re-pulled on the 5s-debounced local-change path — so editing one playlist track re-downloads the peer's entire library manifest. Building that manifest calls `LibraryOpenSubsonicMapper.ComputeAlbumArtHash` once per album (`:60-64`), each of which opens the file with TagLib and SHA-256s the art bytes: ~1,400 file opens and hashes per request, uncached.

Meanwhile a *server-side* change is never noticed at all while both apps stay running: sync only fires on first mDNS contact or a debounced **local** change. The 5s `/info` poll checks reachability/trust/alias only. This is the `docs/todo.txt` "push library sync events instead of polling" item, and it is a correctness gap, not an optimization.

### 1.5 Repeated O(n) passes with no incrementality

- `Track.SyncKey` is a computed property allocating 4 strings per read (`Track.cs:160`), read in tight loops in `UpdateTracks`, `MergeSyncedTracks`, and both iTunes importers — ~100k+ allocations per rescan just for keys. Cache it (invalidated on tag edit).
- `Library.IncrementPlayCount`/`RecordPlayed` do an O(n) `FirstOrDefault` over 16k tracks under the global lock, twice per song. A `Dictionary<string, Track>` by path (maintained by `UpdateTracks`) makes it O(1).
- `TrackListBuilder.SortKey` allocates a `char[]` + `string` per track per sort key (`:78-83`); `TrackListBuilder.Build` reallocates all 16k `TrackRowViewModel`s per rebuild rather than diffing.
- `TrustedPeerStore.Load()` does a synchronous `File.ReadAllText` + full deserialize **on every gated request** (`SyncHttpServer.VerifyTrustedPeer` → `GetPublicKey`) — blocking disk I/O on the streaming hot path, up to 120 browse requests/min. Cache in memory with invalidation on write.
- `MusicListPanel.ComputeRenderIndices` backscans to the album-group leader per visible row (`:128-129`) — O(viewport × group size). `TrackListBuilder` already knows `AlbumGroupSize`; precompute a leader-index array once in `SetItems`.
- `RebuildRowsAsync` always builds **three** O(library) collections — rows, album tiles, recently-added tiles (`MainViewModel.cs:2470-2481`) — even when viewing Songs/Playlists/Artists, where two of the three are discarded. Mobile passes `includeGridTiles: false` with a comment acknowledging the waste; desktop never got the same treatment.
- Wholesale `Rows` replacement also discards each row's lazily-decoded `AlbumArt`, so every settled keystroke re-triggers N `Task.Run` art loads even when they all hit the cache.
- `NotifyPairButtonPropertiesChanged` (`MainViewModel.cs:1044-1055`) unconditionally re-raises 9 properties from 6+ call sites instead of diffing.
- At least 8 timers run app-wide, four of them at 60 Hz (busy spinner, per-row download spinner, swipe easing, scroll-into-view). No shared animation clock exists; a batch download runs one 60 Hz dispatcher timer *per row*.

---

## Tier 1 execution

What was actually done, in the order it landed. Full per-item detail lives in `docs/ARCHITECTURE-REVIEW.md`.

**Step 1 — the play hot path (§1.1).** `FlowerJsonContext` set to `WriteIndented = false` + `DefaultIgnoreCondition = WhenWritingNull`; safe because that context covers only formats Flower controls both ends of. `LibraryStore.ScheduleSave` coalesces the write-per-event behind a 3s debounce, with `Flush()` for app exit and `Save()` discarding anything still pending, so quitting inside the debounce window cannot lose the increment that opened it. `Library` gained `TrackStatsChanged`, carrying the resolved track; `PlaylistControlViewModel.Play`/`EndReached` no longer call `NotifyTrackChanged`, and `MainViewModel` re-raises two columns on one row via `TrackRowViewModel.NotifyStatsChanged` instead of rebuilding 16k rows and scheduling a peer sync. The lost-update race named in §1.1 was already closed by Tier 0.

**Step 2 — album art (§1.2).** `AlbumArtLoader.DecodeScaled` caps decodes at 768px — mobile `NowPlayingView`'s 280pt square at ~2.7x DPI, the widest anything actually paints, and one cache serves every call site. It never scales *up*: `DecodeToWidth` applied unconditionally would inflate a 300px cover and make matters worse for modest art, and with no SkiaSharp reference there is no cheap way to read the intrinsic size first, so oversized art is decoded twice — once to learn its size, once at the size wanted. Only the retained bitmap shrinks, which is the one that mattered. Added a 32-entry strong LRU for the visible set, and pruning of dead `WeakReference` entries that previously accumulated one per album ever displayed.

**Step 3 — server query shapes (§1.3).** `GetAlbumList2` and `Search3` share an `AlbumSummaries` projection that groups, aggregates and paginates in SQL; `GetArtists` collapses to one row per distinct (artist, album) pair server-side; `Search3` became three individually-limited queries with the disagreeing in-memory re-filter deleted. Two defects here compiled cleanly and failed only at request time — SQLite refuses `Max()` over a `DateTimeOffset` (so `type=newest` aggregates client-side over a two-column projection, pending a value converter that belongs with §4.1), and EF Core translates a grouped aggregate projection only as a member initializer, never a constructor call (so `AlbumSummary` is an init-property class, not the positional record it started as). Verified by running the server against the real library and exercising all five sort types plus search and artists; there is still no test project that would have caught either, which is why §5.1 is now next.

**Step 4 — the O(n) hot paths (§1.5).** Cached `Track.SyncKey`, invalidated by the setters of the four fields it derives from (now backed properties) so a tag edit still takes effect. A lazily-built path index in `Library`, invalidated rather than maintained incrementally because `LibraryDownloadService` sets `Path` in place on a placeholder. In-memory caching in `TrustedPeerStore`, invalidated by its own writes. A precomputed group-leader array in `MusicListPanel.SetItems`. `RebuildRowsAsync` deriving `buildGrids` from the current view, so desktop stops building two discarded tile grids per keystroke. `SortKey` rewritten to count-then-fill via `string.Create`, returning the input untouched when nothing needs stripping. `NotifyPairButtonPropertiesChanged` diffing against the last-notified tuple.

**Verification.** `dotnet build` clean for Desktop and Server; `dotnet test --filter Category!=RequiresLibVLC` at 406 passing, up from 393 — new tests cover save coalescing and `Flush`, `Save` superseding a pending schedule, the JSON format change, `TrackStatsChanged` vs `TracksUpdated`, stats resolution across a rescan, path-index invalidation after a download sets `Path`, `SyncKey` cache invalidation per field, and `SortKey`'s punctuation handling. Server changes verified by running it against the real 16,116-track library rather than by test, for the reason given in step 3.

---

## Tier 2 — Structural: multiple sources of truth

### 2.1 Four track models and five identity schemes

| Layer | Model | Identity |
|---|---|---|
| Client domain | `Flower.Core.Models.Track` | `Path` (case-insensitive) |
| Cross-device sync | same `Track` | `SyncKey` = normalized Title\|Artist\|Album\|rounded seconds |
| In-memory UI navigation | same `Track` | **accidental** full-record value equality |
| Wire (P2P + Subsonic) | `Child`/`AlbumID3` DTOs, `PlaylistSyncTrackDto` | `al:{norm}\|{norm}` (client) |
| Server | `Flower.Server.Data.TrackEntity` | `al-{hash}` (server) |

The two album-ID schemes differ by a punctuation character and nothing enforces the distinction (`LibraryOpenSubsonicMapper.cs:76` vs `SubsonicIdentity.cs:16`). `SubsonicMapper.ToChild` re-implements duration rounding inline as `Math.Round(t.DurationSeconds)` (`SubsonicMapper.cs:25`) instead of calling `Track.RoundedSeconds` — which is exactly the bug class `Track.cs:168-183`'s comment documents having already been hit and fixed once. `TrackEntity` has a `Starred` column the client `Track` has no concept of.

**Fix:** stable `Track.Id` (§0.2) becomes the single identity; `SyncKey` is demoted to a *matching heuristic* used only at import/first-pairing, not an identity. Move the canonical `(artist, album) → id` function and the DTO mapping into `Flower.Core` so client and server share one implementation.

### 2.2 Auth and art lookup are each implemented two-to-three times

- `SyncHttpServer.VerifySelfSigned`/`VerifyTrustedPeer` (`:377-430`) vs `Flower.Server/Services/DeviceSignatureAuth.cs:18-46` — near-identical, hand-copied, including the `GetIdentityValue` header/query fallback helper implemented twice.
- Album-art file fallback logic exists in `AlbumArtLoader.TryGetLocalArtBytes`, `SyncHttpServer.HandleGetCoverArtAsync`'s content sniffing, and `Flower.Server/Endpoints/SubsonicEndpoints.cs:438-484`. Three places to fix when someone adds `.webp`.

### 2.3 DI is service location, and the composition root is a 330-line method

Every service in `App.axaml.cs::Bootstrap` (`:127-309`) is `new`'d by hand then registered as an *instance*, so constructor injection is bypassed and adding a dependency means editing `Bootstrap`. `services.BuildServiceProvider()` is called twice (`:67` and `:309`) — the first container exists solely to fetch an `ILoggerFactory` and is then leaked. Logging is service-located via `AppLogging.CreateTypedLogger<T>` throughout, contradicting the project's own stated preference for constructor-injected `ILogger<T>`.

`Ioc.Default` is used as a full service locator across **15 files / 44 call sites** — every control and window resolves its own dependencies (`MusicListPanel.cs:30`, `MainView.axaml.cs:34-36`, `TrackInfoWindow`, `ServerPickerView`, …). Worst case: **`AlbumArtLoader` is a `static` class that reaches into `Ioc.Default.GetService<PeerTrackResolver>()`/`GetService<DeviceIdentity>()` from inside a static method** (`:200-201`), hiding its real dependencies entirely and making it untestable without a live container — which is precisely why it has no tests despite carrying the most production-bug narratives in the codebase.

`Flower/Extensions/ServiceCollectionExtensions.cs` is dead scaffolding — `AddCommonServices` has a commented-out body and zero callers, while misleadingly suggesting registration is centralized there. Delete it.

**Event subscriptions are never unsubscribed anywhere** (`grep -c '-= '` over `Flower/ViewModels` + `Flower/Services` → 0). `MainViewModel`'s constructor alone subscribes to `TracksUpdated`, `PlaylistsUpdated`, `PropertyChanged`, `DeviceDiscovered`/`DeviceLost`, `reachability.Changed`, `ConflictDetected`, `PeerTrustRejected` ×2, `PeerUnpairNotified`, `PeerApprovalRequested`, and `InMemoryLogStore.EntryAdded` (`:1267-1404`), and implements no `IDisposable`. Harmless *only* because it is a process-lifetime singleton — nothing enforces that, and it blocks per-test reconstruction.

### 2.4 `Library.Playlists` is unlocked while `Library.Tracks` is locked

`AddPlaylist`/`RemovePlaylist`/`ReplacePlaylists` (`Library.cs:306-333`) take no lock, despite `ReplacePlaylists` being called from the sync path concurrently with UI-thread mutations. Same threat model that motivated `_lock` for `Tracks`, inconsistently applied. Persistence is also caller-responsibility — nothing structurally guarantees a mutation is followed by a save.

### 2.5 Rescan carry-forward is a hand-maintained field list, written twice

`Library.UpdateTracks` (`:87-91` and `:95-99`) repeats the same five assignments in two branches, plus `CarryForwardOrigin`. Every new persisted-and-not-rescannable field (`Starred`, `Rating`, `Source`, …) must be remembered by hand in three places. Make it one `CarryForwardMutableState(from, to)` method, and add a test that fails when a new such field is added and forgotten.

### 2.6 No schema version anywhere

Backward compatibility is ad hoc sentinel detection invented independently three times: `PlaylistRecord.Id = default` (`PlaylistStore.cs:38,56`), `TrustedPeer.PublicKey = ""` (`TrustedPeerStore.cs:12-15`), `DeviceIdentityStore.Load`'s alias-backfill/fingerprint-correction (`:54-78`). `Flower.Server` is worse: `EnsureCreatedAsync()` (`Program.cs:61`) with no EF migrations at all, so **any** schema change wipes a self-hoster's database.

---

## Tier 3 — Security

Ordered by real exposure, not theoretical severity.

1. **`Flower.Server`'s `/rest/*` has no rate limiting.** `AdminEndpoints` and `PairingEndpoints` each get a `RateLimiter`; `SubsonicEndpoints` has none. Classic Subsonic auth is `t = md5(password+salt)` (`SubsonicAuth.cs:27`) with no expiry or nonce, so a captured `u`/`t`/`s` query string replays forever. Combine with the shipped default `AdminPassword = "changeme"` (`FlowerServerOptions.cs:22`) and `LanGuard`'s unconditional trust of the Tailscale CGNAT range `100.64.0.0/10` (`LanGuard.cs:33`) and that is an unauthenticated brute-force surface on any tailnet-exposed deployment.
2. **Application logs, including exception text and file paths, are pushed over plaintext HTTP** to the paired peer (`LogSyncContracts.cs:13`). TLS is permanently deferred for the P2P path by design; the log-push feature was added after that decision and changes what's at stake.
3. **Private signing key stored as plaintext PKCS8 JSON** (`DeviceKeyStore.cs:20-28`), with no revocation path a victim can initiate. Documented as accepted, but it is the root of the whole identity scheme.
4. **`AdminAuthService` tokens are in-memory with no per-token revocation** — a stolen bearer token is good for 24 h and the only lever is a process restart.
5. `RateLimiter` is a fixed window, so a boundary-timed burst gets ~2× the intended ceiling on login and pairing-code redeem.
6. Body-size caps are inconsistent: `SyncHttpServer` caps at 20 MB, `Flower.Server` caps only pair-redeem at 4 KB with no global limit.

---

## Tier 4 — What should be rewritten, not patched

### 4.1 Persistence: JSON blobs → SQLite (client side) — *end state, deliberately sequenced later*

This is the one genuine rewrite recommendation, but it is **not** the next thing to build. Tier 0's atomic-write hardening and Tier 1.1's stats/metadata split are worth doing on the JSON layer regardless: they are small, they stop active data loss now, and they are the same decomposition SQLite would need anyway. Start the migration when a roadmap item actually demands queryable state — smart playlists, liked songs, per-user play counts, or "downloaded only". Every Tier-0/Tier-1 data problem above traces to "the whole library is one JSON document": non-atomic writes, whole-file rewrites per play, no partial reads, no indices, no migrations, no room for per-user or per-provider state. `Flower.Server` already uses EF Core + SQLite; the client should too, sharing the schema and entity model via `Flower.Core`. That collapses four track models toward one, makes a play-count bump a single-row `UPDATE`, gives migrations for free, and is the prerequisite for the roadmap items that need queryable state (smart playlists, liked songs, downloaded-only filter, per-user play counts, last-played history).

Migration path: keep `library.json` as an import-once source, write SQLite alongside for one release, then drop the JSON.

### 4.2 `MainViewModel` (2,573 lines) — decomposition

Six unrelated jobs in one class. Roughly **900 of the 2,573 lines are a P2P sync coordinator** that lives here only because `SidebarItems` does. Proposed seams:

| New class | Moves in |
|---|---|
| `PeerSyncCoordinator` | `RunTrackedSync`, `ScheduleContentSync`, `TriggerSyncIfReady`, `RunPendingDeviceSyncs`, `PairWithServer`/`UnpairServer`/`ConfirmServerTrust`, `ForceSyncNow`, `DeviceAlias` (`:67-630`) **plus** the whole device-sidebar-row state machine (`AddOrUpdateDeviceSidebarItem`/`RemoveDeviceItem`/`FindDeviceSidebarItem`/`RelocateDeviceSidebarItemIfNeeded`/`RemoveDuplicateDeviceSidebarItems`/`RefreshDeviceDisplayNames`/`SyncPairedServerSidebarRow`, `:1699-2027`) |
| `LibraryBrowserViewModel` | `Rows`, `_currentFilteredTracks`, `FilterText`, the three independent sort states (Songs/RecentlyAdded/History, `:823-846`), `ScheduleFilter`/`RebuildRowsAsync`, grid tiles, `SubListItems`, `StatusBarText` |
| `PlaylistManagementViewModel` | playlist CRUD + `RefreshPlaylistSidebarItems` (`:2181-2268`) |
| `SidebarViewModel` | `SidebarItems`/`SelectedSidebarItem`/`BuildSidebarItems`, composed from the three above |
| `AppSettingsViewModel` | the six-plus repetitions of the `_appSettings ??= new(); …; _ = _appSettingsStore?.SaveAsync(…)` triplet |
| `ITunesImportCoordinator` | `SyncITunesPlayCountAsync`/`SyncITunesDateAddedAsync` + their cooldown fields, used by both startup and Settings |

The 20-parameter constructor (10 required + 10 defaulted-to-null purely for WASM, `:1174-1207`) is the symptom: testing anything means standing up or nulling the whole graph. Relatedly, `_appSettings` is nullable and lazily `??=`'d across the entire class *only* so the Avalonia previewer's parameterless constructor works (`:1170-1172`) — isolate that in a design-time subclass instead.

**Mobile is mostly good reuse, undermined by `private`.** `MobileMainViewModel` correctly wraps `MainViewModel` rather than re-implementing filter/sort, but because the pieces it needs are private it duplicates them anyway:
- `PlayResolvingPlaceholder` (private, `MainViewModel.cs:1448-1458`) is reimplemented inline in `MobileMainViewModel.PlayTrackCommand` (`:721-755`).
- `SyncPlayQueueToCurrentView` is reimplemented (`MobileMainViewModel.cs:417-423`) because desktop's reads a private field — and the class's own comment records that this gap **shipped as a real bug** ("mobile's queue stayed pinned to Importer's raw filesystem-scan order… confirmed on a real device").
- Five drill-in methods (`:1164-1250`) hand-roll the same push-history/set-scope/rebuild/raise sequence.

### 4.3 Duplicated multi-select + drag gestures in `MainView.axaml.cs`

~400 lines (`:247-913`) implement shift-range / ctrl-toggle / drag-threshold / drop-highlight **twice** — once for `SubList` (`SelectSubListRange`/`ToggleSubListItem`) and once for the album grids (`SelectAlbumGridRange`/`ToggleAlbumGridItem`) — same logic, different target. Extract one reusable multi-select-and-drag-source helper.

Two pieces of genuine business logic also sit in code-behind and should move to the ViewModel where they can be tested: `OpenTrackInfoForSelectedAlbums`/`ResolveSelectedAlbumTracks` (`:1233-1288`, non-trivial precedence rules for what "Get Info" applies to) and `CommitRename` (`:1415-1445`, which mixes UI teardown with `PlaylistStore.SaveAsync`/`DeviceNicknameStore.SetAsync`/`ScheduleContentSync`).

### 4.4 `SyncHttpServer` streaming has no range support

`HandleStreamAsync` (`:667-694`) sets `ContentLength64` and `CopyToAsync`es the whole file. No `Range` handling — so a dropped mobile download restarts at byte 0, and seeking within a peer-streamed track can't use partial content. `Flower.Server` gets this right via `Results.File(..., enableRangeProcessing: true)`; the P2P host should too. Related: `OpenSubsonicClient.DownloadTrackAsync` (`:315-326`) `File.Create`s the destination and never deletes the partial file on failure, leaking orphans that no code path can reach.

---

## Tier 5 — Test coverage gaps that matter

CI currently runs 335 tests, but **`dotnet test Flower.Tests` builds only `Flower.Tests → Flower → Flower.Core`** — `Flower.Server` and `Flower.CLI` are never even compiled by CI, let alone tested. (The `tests.yml` comment claiming `Flower.csproj` multi-targets `net10.0;net9.0` is also stale — it targets `net10.0` only.)

Highest-value additions, roughly in priority order:

1. **`Flower.Server`: a test project at all.** `AdminAuthService`, `PairingCodeService`, `DeviceSignatureAuth`, `SubsonicAuth` are security-critical and entirely unverified. Use `WebApplicationFactory` + in-memory/temp SQLite. Add a CI job that at minimum *builds* `Flower.Server` and `Flower.CLI`.
2. **`PlaylistSyncPlanner` delete-vs-edit** (§0.4) — write the failing test first.
3. **`Track` identity/equality** — no test anywhere exercises two value-equal-but-distinct tracks through `IndexOf`/`Remove`/`GetNextTrack`. This is why §0.2 is invisible to CI.
4. **Store corruption and atomicity** — truncated file, `.bak` fallback, concurrent writes. `StoreRoundTripTests` is the established pattern; extend it. `PlaylistSyncStateStore` has no round-trip test at all despite being on the write path of every sync.
5. **A real socket round trip**: `SyncHttpServer` + `LibrarySyncService`/`PlaylistSyncService` against each other. Today `FakePeerHttpServer` substitutes for the real listener, so the route table, rate limits, and trust gates in `HandleRequestAsync` are validated only by hand.
6. **`Importer`** — zero coverage. The dedup-across-overlapping-paths logic, extension filtering, `IsCompilation` per-format branching, and skip-unreadable-file behaviour are all untested.
7. **`AlbumArtLoader`** — zero coverage, despite carrying the most "confirmed on a real device" bug narratives in the codebase (cache-key collision, corrupt-image fallback, remote fetch/disk cache).
8. **`MusicListView`/`MusicListPanel`** — the highest-risk untested surface in the UI given it is entirely hand-rolled: virtualization range math, album-group-leader spanning, shift-range/ctrl-toggle selection, header drag-reorder. All manually verified only.
9. **`MainViewModel`'s sync/pairing/device-row state machine** (~900 lines) — `MainViewModelSidebarNavigationTests` is a single debounce-timing regression test and touches none of it.
10. **Decode failure end-to-end** (§0.10) — no test injects a fault mid-track and asserts user-visible behaviour, and nothing tests what happens when `SaveAsync` throws inside the async-void `EndReached` handler.
11. **`Library.ReplacePlaylists`'s `PlaylistsUnchanged` short-circuit** and **`ColumnManager.Reorder`** — nontrivial algorithms, no tests.
12. **No dedicated tests** for `CurrentlyPlayingControlViewModel`, `TrackRowViewModel`, `VolumeControlViewModel`, `EqualizerViewModel`, `LogViewModel`, `SidebarItem`, `ScreenStackPanel`'s swipe state machine.
13. **UI tests** (already on the todo): `Avalonia.Headless` can drive `MusicListView` virtualization and `ScreenStackPanel` navigation without a display.

### Other bug notes worth folding in

- **`async void` on non-event-handler paths**: `MainViewModel.ForceSyncNow` (`:590`) is bound directly to a command; `ScheduleFilter` (`:2425`); seven methods on `MobileMainViewModel` including `SwipeBack`/`SwipeForward`/`ReorderCurrentPlaylistTrack`. A throw in any of these tears down the process — `TaskScheduler.UnobservedTaskException` (`App.axaml.cs:84`) does not observe `async void`.
- **`IAudioManager` is silently partial on WASM**: `WebAudioManager` no-ops `SetUpcoming` and `ApplyEqualizer` (`:132-134,158-160`) with no compile-time or runtime signal that a platform drops those features. Worth a capability flag before `Flower.Web` grows further.
- **Seek drift**: `GaplessCoordinator.Seek` pre-negates `_currentTrackReadSplit` by the *requested* target (`:255-282`); if LibVLC lands elsewhere (non-keyframe-aligned lossy seek) nothing re-synchronizes, so the scrubber can drift from the audio.
- **`OpenSubsonicClient` never reuses connections** — `ConnectionClose = true` on every request (`:144`, plus `PlaylistSyncService.cs:239`, `LibrarySyncService.cs:107,183`). A documented workaround for `HttpListener`/iOS-backgrounding quirks, but it means even a 3-request sync session pays three full handshakes. Note it as a permanent cost of the current transport, not a bug to fix in place.

---

## Future-proofing check against the roadmap

| Planned work | Blocked/complicated by | Unblocked by |
|---|---|---|
| Streaming providers (`IMusicProvider`, `Track.Source`) — `STREAMING-SERVICES-PLAN.md` | `Path` as identity; no `Source` field; credentials would land in `settings.json` | §0.2 stable `Id`, §4.1 SQLite, a new `ISecretStore` |
| Push sync instead of polling — `todo.txt` | Full-manifest-only protocol, no version/ETag, sync triggered only by local events | §1.4 manifest versioning + a server→client change notification |
| Family/friends read-only accounts — `todo.txt` | `Flower.Server` has no `User` table; `PlayCount` is a single global column | §4.1 shared schema, per-user play counts |
| Liked songs / smart playlists / "downloaded only" | No queryable store; `Starred` exists server-side only | §4.1 SQLite + §2.1 one track model |
| Track last-played per song — `todo.txt` | `LastPlayedAt` exists but every write rewrites 17.9 MB | §1.1 |
| Export playlist with actual songs, playlist folders | Playlists persist by `Path` and drop placeholder tracks | §0.3 |
| AirPlay/Bluetooth device picker — `AIRPLAY-BLUETOOTH-PLAN.md` | `IAudioSink` has no device-enumeration concept | Additive; design the seam when §4.2 touches the audio manager |
| CI benchmarks — `PERFORMANCE-TRACKING-PLAN.md` | — | Do this *after* Tier 1 so the baselines mean something |

---

## Recommended sequence

As originally proposed, annotated with what happened. The live version of this list is the *Status* section at the top.

1. ~~**Tier 0 — in scope for this change.**~~ Done. See *Tier 0 execution* above.
2. ~~**Tier 1.1** — stop rewriting the library per play.~~ Done on the JSON layer (`TrackStatsChanged`, coalesced saves, `WriteIndented` off). Splitting stats from metadata is the remaining half and belongs with 4.1.
3. **Tier 5.1** — `Flower.Server` test project + CI jobs that build every project. **Now the next thing to do**, promoted above the rest of Tier 1: 1.3 shipped two defects that only a running server caught.
4. ~~**Tier 1.2-1.5**~~ — art downscaling, server query shapes and the O(n) hot paths are done. **Manifest versioning (1.4) is not**, nor is row diffing in `TrackListBuilder.Build`.
5. **Tier 3** — server rate limiting, default-password handling, log-push exposure.
6. **Tier 4.1** — the SQLite migration, once its consumers (smart playlists, accounts) are actually next up.
7. **Tier 4.2/4.3** — ViewModel and code-behind decomposition, last, when the seams are visible.

---

## Verification (Tier 0, this change)

- `dotnet build Flower.sln` — deliberately the whole solution, since `Flower.Server`/`Flower.CLI` are not built by CI today and step 2's `Track` change touches `Flower.Core`.
- `dotnet test Flower.Tests/Flower.Tests.csproj --filter Category!=RequiresLibVLC` for the fast loop; the full run with a local VLC install for the §0.7/§0.10 audio changes.
- **§0.1 by hand:** back up the real `~/Library/Application Support/Flower/library.json`, truncate it mid-file, launch, and confirm the app recovers from `.bak` with play counts intact instead of starting empty and re-dating the whole library. Do this on a copy — the current behaviour would destroy 16k tracks' worth of history.
- **§0.2/§0.3 by hand:** add the same song to a playlist twice, remove the second one, confirm the right one goes; add a not-yet-downloaded (placeholder) track to a playlist, restart, confirm it survives.
- **§0.6 by hand:** sort by Album descending and confirm track numbers still ascend within each album.
- **§0.7 by hand:** play through 30+ tracks and watch process handle/memory count stay flat.
- **§0.10 by hand:** drop a truncated/corrupt audio file into the library mid-playlist and confirm it skips with a visible message and no play-count increment.

Later tiers, for reference: instrument `LibraryStore.SaveAsync` with a `Stopwatch` to confirm §1.1; watch process memory with 40+ album tiles visible for §1.2; time `getAlbumList2` against the 16k-track library for §1.3.
