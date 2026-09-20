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
git show ad337e7:docs/ARCHITECTURE-REVIEW.md
git show ad337e7:docs/OPEN-INTERNET-REVIEW.md
git show ad337e7:docs/SMART-PLAYLIST-PLAN.md
```

Everything listed here is **done**. Nothing in this file is a backlog;
`CODE-REVIEW-2026-09.md` is where open work lives.

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
