# Cited decisions

Three documents were retired in September 2026 once they had no open items
left: `ARCHITECTURE-REVIEW.md`, `OPEN-INTERNET-REVIEW.md` and
`SMART-PLAYLIST-PLAN.md`. Between them they were cited by roughly 140 source
comments — almost always as *"see Tier 2.3"* or *"see #2b"*, standing in for the
reasoning behind the code the comment sits on.

**This file is what those citations resolve against.** One line per numbered
item: what the problem was, and what was done about it. It is an index, not a
summary — the arguments, the measurements and the rejected alternatives are not
here.

**The full text is in git**, and it is worth reading when an entry below is not
enough:

```bash
git show 070c2ca:docs/ARCHITECTURE-REVIEW.md
git show 070c2ca:docs/OPEN-INTERNET-REVIEW.md
git show ad337e7:docs/SMART-PLAYLIST-PLAN.md
```

(The first two outlived their retirement by a month: they were left tracked
when the third was deleted, and went in October 2026. `070c2ca` is the last
commit that has them.)

Everything listed here is **done**. Nothing in this file is a backlog;
`CODE-REVIEW.md` is where open work lives.

---

## `ARCHITECTURE-REVIEW.md` — the August 2026 structural review

Six tiers, worked in order. Tier numbers are what the source comments cite.

### Tier 0 — data-loss and correctness bugs

| | |
|---|---|
| **0.1** | Non-atomic writes over the only copy of irreplaceable data — writes now go through a temp-file-and-rename. |
| **0.2** | `Track` was a `record` with 40 mutable properties, so value equality was being used as identity — identity is `Path` now, and the record's generated equality is not relied on. |
| **0.3** | Playlists held orphaned `Track` instances after every rescan — playlists store references resolved against the library. |
| **0.4** | `PlaylistSyncPlanner` silently resolved delete-vs-edit in favour of delete. |
| **0.5** | `ColumnManager`'s debounce did not debounce — fixed with a real `CancellationTokenSource`. |
| **0.6** | Descending sort reversed secondary keys — fixed by applying `OrderByDescending` to the primary key only. |
| **0.7** | Native LibVLC handles leaked on every track change (LibVLC is gone now; the disposal discipline is not). |
| **0.8** | A leaked 60fps `DispatcherTimer` per downloading row — fixed by disposing outgoing rows. |
| **0.9** | `ScheduleContentSync` ran off the UI thread over a collection the UI mutates — the whole handler is marshalled now. |
| **0.10** | Decode failure was indistinguishable from end-of-track. |

### Tier 1 — performance

| | |
|---|---|
| **1.1** | A full 17.9 MB library serialization on every track change. |
| **1.2** | Album art was decoded at full source resolution — decoded down to `MaxArtPixels`, never scaled up. |
| **1.3** | `Flower.Server` materialized the entire `Tracks` table on every browse request. |
| **1.4** | Sync transferred the whole manifest every time, re-hashing all album art to build it. |
| **1.5** | Repeated O(n) passes with no incrementality. |

### Tier 2 — structural: multiple sources of truth

| | |
|---|---|
| **2.1** | Four track models and five identity schemes — collapsed to one model and one identity. |
| **2.2** | Auth and album-art lookup were each implemented two-to-three times. |
| **2.3** | DI was service location and the composition root was a 330-line method. The most-cited item in the review: a comment citing 2.3 usually marks a constructor parameter that used to be an `Ioc.Default` lookup. Its two deferred halves — the settings-tab views' service location, and unsubscription across the rest of the ViewModel layer — are also done. |
| **2.4** | `Library.Playlists` was unlocked while `Library.Tracks` was locked. |
| **2.5** | No schema version anywhere. |
| **2.6** | Rescan carry-forward had no guard against a forgotten field. |

### Tier 3 — security

No numbered subsections. Superseded in practice by `OPEN-INTERNET-REVIEW.md`
below, which is the deeper read of the same surface.

### Tier 4 — rewrite candidates

| | |
|---|---|
| **4.1** | Persistence: JSON blobs → SQLite. Raw `Microsoft.Data.Sqlite`, **not** EF Core — EF cannot run on iOS, and EF Core is gone from the repository entirely. |
| **4.2** | `MainViewModel` decomposition. Two defects were found while decomposing it, both fixed. |
| **4.3** | Duplicated multi-select and drag gestures in `MainView.axaml.cs`. |
| **4.4** | `SyncHttpServer` streaming had no range support — done, then removed along with its subject when a client stopped serving anything. |

### Tier 5 — test coverage

CI built only `Flower.Tests → Flower → Flower.Core`, so `Flower.Server` and
`Flower.CLI` were never compiled by CI at all. What that hid: `Flower.Server/Data/`'s
four entity sources had **never been committed**, because the `.gitignore` rule
`Flower.Server/data/` — meant for the runtime data directory — also matched the
*source* folder on a case-insensitive filesystem. The server had been unbuildable
from a clean checkout since it was scaffolded. The rule is per-artifact now
(`[Dd]ata/*.db`, `*.db-shm`, `*.db-wal`, `*.json`, `logs/`) rather than a directory
exclusion, which would also have made a `!*.cs` negation impossible since git does
not descend into an excluded directory.

| | |
|---|---|
| **5.1** | A `Flower.Server` test project at all → `Flower.Server.Tests`. New CI jobs `server-test` and `build-rest` (now `build-desktop`) mean every project in the solution is compiled by CI. |
| **5.2** | A real socket round trip for `SyncHttpServer` — done, then deleted with its subject. |
| **5.3** | `Importer` had zero coverage → `ImporterTests`, over real files read through real TagLib#, generated at test time by `SyntheticWav`. |
| **5.4** | `AlbumArtLoader` had zero coverage → `AlbumArtLoaderTests`. Brought `SyntheticPng` and real Skia drawing in the headless test platform with it — headless's own stub reports every image as 1×1, which would have made every scaling assertion vacuous. |
| **5.5** | `MusicListView`/`MusicListPanel` → `MusicListPanelTests`. |
| **5.6** | `MainViewModel`'s sync/pairing/device-row state machine → `MainViewModelDeviceSidebarTests`. |
| **5.7** | `Library.ReplacePlaylists`' `PlaylistsUnchanged` short-circuit. |
| **5.8** | No dedicated tests for `CurrentlyPlayingControlViewModel`, `TrackRowViewModel`, `VolumeControlViewModel`, `EqualizerViewModel`. |
| **5.9** | UI tests driving `MusicListView` virtualization through `Avalonia.Headless`. |

**A recurring method worth keeping**: several Tier 5 items record being
"confirmed to have teeth by mutation" — reintroducing the original bug and
checking that named tests fail. That is the standard this suite was built to,
and it is why the tests are worth trusting.

---

## `OPEN-INTERNET-REVIEW.md` — the trust-boundary read-through

The review that gated turning on any remote transport. Its framing finding: **`LanGuard`
was cited as the containing control in five separate places that had each been reasoned
about independently, and a tunnel delivering over loopback retires all five at once**
without anything logging that it happened.

| | |
|---|---|
| **#1** | `/info` handed its address list to anyone who asked — it now answers `addresses` and `trustsCaller` only to a peer whose signature verifies, and the client signs its poll to be one. |
| **#2** | Every per-IP budget collapsed to one bucket behind a tunnel. An undeclared proxy now warns about itself (watching for an `X-Forwarded-For` from a hop `TrustedProxies` does not name), and the failed-auth lockout gates failed authentications only, so a paired device sharing an address with a guesser keeps playing. |
| **#2b** | One budget across browse, art and audio, so a cover-art burst starved playback — **this is the one most cited from code**. The surface budgets bulk, art and media separately now; every refusal carries `Retry-After`; the client waits out a throttle instead of treating it as an I/O error and dropping the track; and `POST /api/flower/v1/cover-art/batch` removes the burst at its source. See also `CoverArtBatch` and `AlbumArtLoader`'s 40ms debounce. |
| **#2c** | A stream URL was signed once at resolve time and then fetched several times (probe, body, reopen), so the probe spent the single-use nonce and the body GET was refused as a replay — a track with a correct length and ~130 bytes of JSON for audio. Signed per request now, a protocol error is never decoded as audio (`SeekableHttpStream.ProtocolErrorFor`), and `LoopbackMediaServer.RequiresFreshNonce` is the device check that can fail on it. |
| **#3** | Per-IP keying was close to free to evade over IPv6 — one shared helper now collapses IPv6 to its /64, so budgets bound a caller rather than an address. |
| **#4** | `/api/admin` had no rate limit at all. |
| **#5** | The signed canonical form was ambiguous across `&` and `=`, so a value could imitate a separator — parameters are percent-encoded now. |
| **#6** | Classic Subsonic auth was unencrypted-transport-hostile by construction. No longer a gate: the server serves TLS from its own device key with nothing to configure, a paired client validates it against the key it already stores, and `PinnedTlsHandshakeTests` pins it over a real handshake. What is left is a deployment note for third-party clients. |
| **#7** | Bearer tokens in URLs, once URLs leave the house. Closed by giving the browser its own identity: a non-extractable WebCrypto P-256 keypair in IndexedDB, redeeming a pairing code and signing every request. `AdminSessionService`, `AdminSessionCredentials`, `PeerOrSessionAuth` and the `X-Flower-Admin-Session` header are all deleted — there is no bearer credential left in the project. Cost: `IPeerCredentials` became asynchronous, and the browser UI now requires HTTPS or `localhost`, because `crypto.subtle` exists only in a secure context. |
| **#8** | Smaller notes, recorded rather than acted on. |

---

## `SMART-PLAYLIST-PLAN.md` — rule-based self-updating playlists

All five phases built and tested. The design decisions source comments cite:

**The core decision — rules are the state, tracks are a cache.** The obvious
shape is `SmartPlaylist : Playlist` with a computed `Tracks`. Don't: `Playlist`
is load-bearing for three consumers that all assume a stored list — the UI and
playback which read `Tracks`, `PlaylistSyncPlanner` which decides "did this side
change?" purely from `UpdatedAt` against a per-peer baseline, and the stores. So
a smart playlist is an ordinary `Playlist` carrying one nullable rules property,
with its track list kept as a materialized cache.

| Cited as | |
|---|---|
| **"Rule model"** | Rules are evaluated in memory rather than translated to SQL, and relative dates stay relative — a rule says "added in the last 30 days", not a baked timestamp. |
| **"Persistence"** (phase 2) | Rules are stored as JSON in one column and travel as a unit (`SmartPlaylistRulesJson`); the schema went V5 → V6. |
| **Phase 3** | Recomputation: when it runs and what drives it — `SmartPlaylistRefresher`, and notably *not* `Library`. |
| **Phase 4 / "UI"** | The rule editor and playlist management. Edits that would mutate a smart playlist's track list directly are a no-op, because the rules are the state. |
| **Phase 5** | Rules crossing the wire. `PlaylistSyncPlanner` never returns `Conflict` for smart-on-both-sides. |

**Out of scope, deliberately:** seed-based "radio"/auto-DJ. It shares nothing
with this but the `Track` model, and entangling them would give both a worse
design. If it happens it gets its own document.

---

## `CODE-REVIEW-2026-09.md` and `CODE-REVIEW-2026-10.md` — their fixed findings

In November 2026 every finding the two reviews had closed was taken out of
them, and what was left of both was merged into one backlog, `CODE-REVIEW.md`.
Source comments cite the closed findings as *"docs/CODE-REVIEW-2026-09.md
B6"*, and this is what those citations resolve against. The findings still
open (September E3; October B4, C1) are in
`CODE-REVIEW.md` under the same review and number. The full text of both,
measurements and rejected alternatives included:

```bash
git show 2e55685:docs/CODE-REVIEW-2026-09.md
git show 2e55685:docs/CODE-REVIEW-2026-10.md
```

### September 2026

| | |
|---|---|
| **A1** | The equalizer went unstable at output rates below 32 kHz. `Equalizer.BuildFrom` builds only the bands below 0.45 × the rate, and `EqualizerTests` drives every band from 48k down to 8k. |
| **A2** | `Retire()` during a track promotion deadlocked the UI thread, because `PromoteTarget` held its gate across a blocking ring write. The ring is reset before the retire, and the promotion's drain no longer holds the gate for its whole run (`GaplessHandoverResponsivenessTests`). |
| **B1** | Sync request bodies were materialised three times, before authentication. The gate reads the body once into one array, and the handlers deserialize those bytes (`RequestGate.BodyOf`). |
| **B2** | `NonceReplayGuard` pruned its whole dictionary on every insert. The sweep runs every 15 s. A ceiling on its size was rejected, because a full guard locks out every honest device too. |
| **B3** | `Library.Snapshot` could publish a stale index that never expired. Each index is tagged with the generation it was read under, and an older one is never accepted (`LibrarySnapshotRaceTests`). |
| **B4** | `SeekableHttpStream` could not be cancelled, and parked a decode thread for up to two minutes. `Cancel()` is safe from any thread, and the decoder calls it before its join. |
| **B5** | Refusals (401/403/404/410) were retried as though they were transient. They throw `HttpProtocolErrorException`, which is not retried. Server errors still are. |
| **B6** | A late arm was dropped, and with it the gapless handover. It turned out to be wasted work, not lost audio. `SetUpcoming` declines when nothing is playing but the ring still holds audio, because the `Play` that carries that case is already on its way. |
| **C1** | The Subsonic surface had authentication but no authorisation. That surface was deleted. The gap behind it was closed by listeners: each server playlist belongs to one (`SECURITY.md`, "Playlists belong to a listener"). |
| **C2** | Client log ingestion had no size bound. Fixed with `MaxEntriesPerReport` and `MaxBytesPerDevice`. |
| **C3** | `getAlbumList2` did not clamp `size`. Moot: the route went with the Subsonic adapter. The rule still holds: no bulk route takes a caller-supplied size without a clamp. |
| **C4** | Identity strings an attacker chooses became dictionary keys with no bound. A nonce or fingerprint over 64 characters is refused as malformed before it becomes a key. |
| **D1** | Every oversized album cover was decoded twice. The size is read from the file's header (`ImageHeader`: PNG, JPEG, WebP), here and in Track Info. |
| **D2** | `Retain` swept the whole weak cache on every hit and miss. Fixed by a sweep at most every 30 s, and an O(1) LRU. |
| **D3** | Every scroll event allocated a `SortedSet` and a `List` in `MusicListPanel`'s render-index pass. Neither is allocated on the scroll path any more. |
| **D4** | `MiniaudioSink`'s watchdog timer ran for the life of the process. It is armed when playback starts and re-armed only while playing. |
| **D5** | `ResourceMonitor.ThreadCount` could see only pool threads. `LiveDecoders` counts the decoders themselves. |
| **D6** | The discovery poll dialled unreachable remembered addresses forever. `PeerRetrySchedule` backs off to one dial a minute, never to never, and any sign the cause may be gone resets it. |
| **E1** | `LibVlcRawStreamSink` and `GaplessRingBufferStream` were unreachable. Deleted with LibVLC. The lesson that generalises: a fallback nothing can reach is not a fallback. |
| **E2** | `GaplessFormat` was mutable process-wide static state. The negotiated format is a `PcmFormat`, settled by the sink and handed to every piece. |
| **E4** | `GaplessAudioManager`'s constructor defaulted to the wrong decoder. Resolved when `TrackDecoderKind` went. The lesson that generalises: a parameter default that contradicts the documented one is a trap. |
| **E5** | `MiniaudioSink.Start` leaked a `GCHandle` when called twice. |
| **E6** | Promoted decoders kept the event handlers they were given while armed. `ClearArmedSlot` unsubscribes them. |
| **E7** | `Process.Start` was called directly, and two platforms cannot run it. Every site goes through the static `PlatformShell` (`Reveal`, `OpenFolder`, `OpenUrl`, `IsAvailable`), and the controls hide where `IsAvailable` is false. |
| **E8** | Loose ends in the album grid. The expansion height is measured. Keyboard navigation came in November 2026: a press gives the grid the focus, the arrows move along and between rows (`AlbumGridKeyboard`), an open album moves with the selection (`LibraryBrowserViewModel.MoveToAlbum`), and Enter does what a click does. `AlbumTileViewModel` and `AlbumTileKey` moved to `Flower.ViewModels`; the phone's own grid types stayed in `Mobile`. The resize debounce was not needed: since October `RebuildRows` re-chunks only when the column count changes, and a debounce would leave the rows a column off for the length of a drag. |
| **F** | The test gaps behind A1, A2, B3, B4, C1 and B2 have all been written. |
| **G1** | The headless teardown flake, "a different thread owns it" out of `Dispatcher.VerifyAccess`. Already fixed when September recorded it, by `e5d25db` (August): the headless session's thread claims the UI dispatcher before any test can (`HeadlessSessionWarmup`, `TestAppBuilder`). Checked in November 2026: three full `Flower.Tests.UI` runs and the last 33 failed CI runs show it nowhere. |
| **G1, socket half** | `FakePeerHttpServer` raced its own port. It binds before it publishes `Port`, and rotates where it starts in its range, so a released port is not reused at once. |
| **G2** | `IAudioManager` was silently partial on WASM. `IAudioManager.Features` says what a player has, and the equalizer is not offered without it. |

### October 2026

| | |
|---|---|
| **A1** | Pairing codes could be brute-forced from an IPv6 prefix. The 20th wrong code from any source in ten minutes cancels every outstanding code (`PairingCodeGuessingTests`). The other half, a longer code in links and QR codes, was dropped in November 2026: with guessing bounded, it bought little. |
| **A2** | Every paired device received every other device's listening history. A guest's manifest carries its own plays only. |
| **A3** | `/info` told an anonymous caller whether a fingerprint was paired. `trustsCaller: false` goes only to a caller that self-signs with that key (`SECURITY.md`, "After the October review"). |
| **A4** | The web UI could be framed. Every web UI response sends `frame-ancestors 'none'`, `X-Frame-Options: DENY` and `nosniff` (`WebUiFramingTests`). |
| **A5** | Device aliases had no bound and could hold control characters. `DeviceAlias.Clean` strips them and cuts at 64 characters. The stream-ticket logging note is in `SECURITY.md`. |
| **B1** | The end-of-track queue decision ran off the UI thread, against state nothing synchronised. It runs on the UI thread, and does nothing if that track is no longer the one playing. |
| **B2** | There were two definitions of path equality, and Linux got both. One `PathComparer`, from the folder's own rule (`PathCaseTests`). |
| **B3** | A failed track told the user nothing. `PlaybackFailed` carries `PlaybackFailedEventArgs`, which `MainViewModel.PlaybackNotice` shows on both heads. |
| **B5** | `SeekableHttpStream` leaked the response on a refusal. It is disposed before the throw. |
| **C1, client half** | A sync that changed nothing rewrote the whole library. `MergeSyncedTracks` skips a merge that moved nothing, and writes only the rows that moved (`LibrarySyncNoOpPullTests`). |
| **C1.2** | A server rescan that found nothing new moved the change token, so every device fetched the whole catalog after it. `UpdateTracks` compares what it found with what it had (`Track.HasSameStateAs`, which `TrackTests` checks covers every field), and an unchanged result moves neither the token nor a row, though the fresh instances still go in (`LibraryNoOpRescanTests`). |
| **C2** | The server read cover art from the audio file on every request. `CoverArtCache` keeps each answer, a miss included. It is checked against the files and folders it came from, and emptied by a library change but not by a play. Then the rest, in November 2026: both art routes take `size=thumbnail`, which the server shrinks to 192 px once and keeps beside the cover (`CoverArtThumbnail`, on SkiaSharp), and a song row asks for that rather than the full cover. `GET /cover-art` sends an `ETag` and `private, no-cache`, and answers a matching `If-None-Match` with a 304. Only a browser can use that, and only on the single-request fallback, since a tab mostly fetches through the batch POST. |
| **C3** | Every art cache hit walked two lists. Fixed by an O(1) LRU. |
| **C4** | Track Info decoded the whole picture to read its size. It reads `ImageHeader`. |
| **D1** | Track Info was implemented twice, in code-behind. It is now `TrackInfoViewModel`. |
| **D2** | Context menus were defined twice, once per head. The song and album menus are now `ActionMenus` built from `MenuFacts`. The menus left are on one head only, so there is nothing to drift. |
| **D3** | "Play these songs as the queue" was written three times. `MainViewModel.PlayTracks` is the one copy, and every queue goes through `SetPlayQueue`. |
| **D4** | Each setting was wired in about seven places. A setting only a device has now binds straight to its `MainViewModel` property. A server setting stays drafted, because a save there is one request. |
| **D5** | The retired review documents still existed. They are deleted, and indexed above. |
| **E** | Comments still described LibVLC, OpenSubsonic and client/server roles, and `CLAUDE.md` still named `library.json`. All corrected. |
| **F.1–F.7** | The test gaps behind B1, B2, A1, A2, C1 and D1 have been written. |
| **F.8** | `PlaylistSessionOverlay.Apply` and `CrashReportScanner` had no tests. `PlaylistSessionOverlayTests` and `CrashReportScannerTests` (Linux and macOS; the Windows event log cannot be filled from a test). |
| **Denied peers** | Dead since peer-to-peer went. Removed, `denied-peers.json` included. |
