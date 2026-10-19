# Code Review — October 2026

A whole-codebase pass against `master` at `1c61ad7`, plus the uncommitted
`MainView.axaml.cs` change (Track Info's Prev/Next browsing the Queue page's own
rows), which reads correctly and is not discussed further. It covers bugs,
test coverage, performance, outdated comments, places with more than one source
of truth, how well controls are shared across the heads, and security.

`CODE-REVIEW-2026-09.md` is still the standing backlog. This review does not
repeat its findings. Section G lists which of them are still open, checked
against the code as it is today, and the rest of this document is new.

**Baseline.** Both suites are green on this tree: `Flower.Tests` (fast filter)
2481/2481, and `Flower.Server.Tests` 316/316.

**How sure each finding is.** Everything below was read from the code. A
finding that was not reproduced says so. None of the races in section B was
observed happening; each was reasoned out from which thread runs what.
Severity is about what happens to a listener or the library, not about how hard
the fix is.

---

## A. Security and the trust boundary

### A1. Pairing codes can be brute-forced from an IPv6 prefix when public access is on — **fixed**

`Flower.Server/Endpoints/PairingEndpoints.cs:20` (`RedeemRateLimiter`),
`Flower.Server/Services/PairingCodeService.cs:25-33`

The redeem limiter allows 5 attempts a minute **per source**, and
`RateLimiter.KeyFor` makes a source an IPv6 /64. Nothing counts failures across
all sources. The comment behind the 5-character code works its odds out for a
single source ("50 tries inside a code's whole lifetime is a one-in-670,000
shot"). That is the right sum for one /64, and a /64 is the smallest unit of
IPv6 anyone hands out.

| Attacker holds | Attempts in one code's 10 minutes | Chance of hitting one outstanding code |
|---|---|---|
| one /64 | 50 | 1 in 670,000 |
| a /56 (a common home delegation) | 12,800 | 0.04% |
| a /48 (free from a tunnel broker, standard at many hosts) | 3,276,800 | **~10%** |

The keyspace is 32⁵ = 33,554,432. An attempt costs the attacker one ECDSA
signature, and any self-generated key passes `VerifySelfSigned`. This needs
`AllowPublicAccess` and an IPv6 path to the server, either a v6 port forward or
a tunnel with `TrustedProxies` set, which is exactly how the docs say to set it
up. A hit gets whatever the code grants. That includes **admin** for the
startup bootstrap code (printed on every boot of a server with no admin, or
with `--pairing-code`) and for any "add an admin device" code.

Every attempt also inserts a nonce into `NonceReplayGuard` before the signature
is checked. At ~5,000 attempts a second with 120 s of retention, that is
~650,000 live entries, and September's **B2** (a full prune on every insert)
then runs a 650k-entry scan per request. So the same flood is also a CPU
exhaustion attack on the server.

**Fix.** Add a server-wide budget on *failed* redemptions, independent of
source. After about 20 failures inside one code lifetime, invalidate every
outstanding code and log a warning that names the attempt count. An attacker
then can't do better than the odds of a single source, and the owner learns
that someone is guessing. Separately, the 5-character length only matters for a
code read aloud. The invite link, the QR code and the browser URL all carry the
code without anyone typing it, so they could carry a much longer one.

**Fixed** with the first half. `PairingCodeService` counts wrong codes from
every source over a ten-minute window. The 20th one (`MaxWrongCodes`) cancels
every outstanding code, and `PairingEndpoints` logs a warning saying so and
telling the operator to issue a new code (or restart with `--pairing-code`). No
code now faces more than 20 guesses, which is a 1-in-1.7-million chance
whatever the attacker's address space. The trade-off is that someone guessing
can stop codes from being redeemed for as long as they keep guessing, which is
loud and costs only a fresh code once they stop. The per-source limit stays,
and its comment no longer claims to be what stops guessing. The longer code in
links was not done. Tests: `PairingCodeGuessingTests`.

### A2. Every paired device receives every other device's listening history — **fixed**

`Flower.Core/Services/LibraryDtoMapper.cs:104-108`,
`Flower.Server/Endpoints/SyncEndpoints.cs:345-363`

`GET /library` is built once per change token and served the same to every
caller. Each track's `PlayCounts` is the whole of `Track.RemotePlayCounts`,
keyed by device fingerprint. So a guest's phone downloads a per-device play
tally for every song from every listener, plus the owner's own `LastPlayed` and
`Starred`. In a household, a fingerprint is a person.

`SECURITY.md` gives each listener their own playlists and their own album
progress, and it stops a guest from *writing* someone else's counts. Nothing
covers *reading* them. That doesn't fit a server that gives "a handful of
friends or family" accounts.

**Fix.** Send a non-admin caller only the server's total and its own entry,
and keep the per-device map for admin devices, which do need it to merge.
That makes the manifest depend on the caller, so `LibraryManifestCache` would
need two cached variants: admin, and guest-minus-self.

**Fixed**, slightly wider than proposed. A guest's catalog
(`SyncEndpoints.ForGuest`) keeps only the guest's own `PlayCounts` entry, and
drops `LastPlayed`, `ResumePositionSeconds` (the owner's podcast position) and
`ImportedPlayCount` as well. The server's own count goes too: on a guest's
screen, "Plays" now means that guest's plays. A native device already ignores
its own entry, and a guest's browser tab, which has no library of its own,
still sees its own plays. Stars are still served, as the owner's curation.
`LibraryManifestCache` caches one body per audience: one for the owner's
devices, one per guest. The ETag is still the change token, the same value
`/info` reports. Data a guest device pulled before this change stays
in its local database, because the merge never removes a count. Test:
`SyncEndpointTests.A_guest_device_is_served_its_own_plays_and_nobody_elses`.

### A3. `/info` tells an anonymous caller whether a fingerprint is paired — **fixed**

`Flower.Server/Endpoints/DiscoveryEndpoints.cs` (the trusted-or-not field of
`SyncInfoResponseDto`)

An unsigned caller that claims a fingerprint gets `false` back when that
fingerprint is unknown and `null` when it is known but the signature failed. So
anyone can ask "is this device paired here?" `AdminEndpoints.cs`' gate comment
takes care never to reveal *admin* status to an unsigned caller, and this
reveals pairing status one level down. It matters little on its own, since
fingerprints are not secret, but A2 hands every guest every other device's
fingerprint.

**Fix.** Return `null` for every result except a verified `true`.

**Fixed, but not quite as proposed.** `null` for everything but a verified
`true` would have broken revocation: `trustsCaller: false` is how a revoked
device finds out, and a revoked device has no key on file to verify against.
It does still hold its key, though, and every client request carries
`X-Flower-PublicKey`. So `false` now goes to a caller whose request
self-signs with the key its fingerprint is the hash of
(`DeviceSignatureAuth.VerifySelfSigned`, the pairing check), and anyone else
claiming a fingerprint gets `null`, paired or not. Tests:
`DiscoveryEndpointTests.Tells_a_device_it_has_revoked_that_it_is_no_longer_trusted`,
`Answers_a_claimed_fingerprint_the_same_whether_or_not_it_is_paired`. One
residue is recorded in `SECURITY.md`, "After the October review": the two
claims are charged to different rate-limit budgets, so a caller willing to run
into both limits can still tell them apart.

### A4. The web UI can be framed — **fixed**

`Flower.Server/Configuration/WebUiHosting.cs` (static files and the SPA fallback)

The server sends no `Content-Security-Policy: frame-ancestors`, no
`X-Frame-Options` and no `X-Content-Type-Options`. An admin's browser profile
holds a non-extractable signing key, and the page signs anything it is clicked
into doing: issuing an admin pairing code, revoking a device, permanently
deleting removed files. Clickjacking a canvas-rendered app is harder than
clickjacking a DOM page, but it is not hard. The `.pdb` files the bundle
publishes are served as well.

**Fix.** Add `frame-ancestors 'none'` and `nosniff` to every response from
`MapWebUi`, and leave the `.pdb` files out of a Release bundle.

**Fixed.** Static files and the single-page fallback both send
`Content-Security-Policy: frame-ancestors 'none'`, `X-Frame-Options: DENY`
and `X-Content-Type-Options: nosniff` (`WebUiHosting.RefuseFraming`,
`WebUiFramingTests`). The `.pdb` half did not reproduce: the WASM publish ships
webcil and no `.pdb`, in Debug and Release alike. The only debugging files in
the bundle are Avalonia's three JS source maps. The `.pdb` content-type mapping
was dead, and is gone, so one would not be served if it ever appeared.

### A5. Smaller items — **fixed**

- **Device aliases are unbounded.** `TrustedPeerStore.RenameAsync` accepts any
  `X-Flower-Alias` a paired device sends, up to Kestrel's header limit, control
  characters included. The alias goes into `trusted-peers.json`, log lines and
  the admin Devices list. Cap it at about 64 characters and strip control
  characters, both at pairing and on rename.
- **Stream tickets are bearer tokens in a query string** (15 minutes,
  reusable). That is right for `<audio>`, but they show up in full in any log
  that records request URLs. The shipped config keeps `Microsoft.*` at Warning,
  so they are not logged today, and an operator who raises it to Information
  does log them. Add one line about this to `SECURITY.md`.

**Fixed.** `DeviceAlias.Clean` drops control and bidirectional-override
characters and cuts to 64 characters at a whole character. `TrustedPeerStore`
applies it on approval and on rename, and both log lines (pairing, rename)
name the cleaned alias (`DeviceAliasTests`). The ticket line is in
`SECURITY.md`, "After the October review", and in `API.md` beside the route.

---

## B. Bugs and races

### B1. The end-of-track queue decision runs off the UI thread against unsynchronised state — **fixed**

`Flower/ViewModels/PlaylistControlViewModel.cs:234-345` (the `EndReached`
handler), `:715-762` (`GetNextEntry`), `:1251-1336`
(`ShuffleOrderFor`/`Deal`)

`EndReached` is raised from inside `FfmpegTrackDecoder.ReportDrained`, on the
finished track's decode thread. The handler keeps "the queue decision" inline
on purpose, and calls `GetUpcomingEntry` on that thread. Under shuffle that
reaches `ShuffleOrderFor` and `Deal`, which *mutate* `_shuffleOrder`,
`_shuffledFor`, `_shufflePlayed` (a `HashSet`) and `_shufflePlayedOrder`, and
call `_random.Next` on a shared `System.Random`. The UI thread mutates all of
the same state at the same moment through `Next()`, `NoteShufflePlayed`,
`ToggleShuffle`, `ClearQueue` and every queue edit. None of it is locked.

So pressing Next, or reordering the Queue page, as a shuffled song ends races
two threads over one `List<int>` and one `HashSet<Guid>`. The milder outcome is
a wrong next song. The worse ones are a "collection was modified" exception, or
a `Random` corrupted into returning 0 forever, which is a documented failure
mode of concurrent `System.Random` use.

An exception here does real damage, because nothing contains it.
`GaplessCoordinator.HandleDrainedOrFaulted` invokes `EndReached` at `:845`,
**before** it starts the promotion drain thread at `:870`. An exception from a
subscriber therefore skips the drain, so the promoted track's staged audio is
never moved into the shared ring. It then unwinds into `DecodeUntilDone`'s
outer catch, which logs "The decode thread … ended unexpectedly" and calls
`ReportFaulted()` on a decoder that has already been handled. The listener
hears playback stop at a track boundary.

The comments around this still say the handler runs "on the LibVLC decode
callback thread" (`:234`, `:266`, `:387`).

**Fix.** Make the decision on the UI thread. The handler already posts the
`Play` there, so post the `GetUpcomingEntry` call along with it. Gapless output
does not depend on it, because the coordinator promotes whatever was armed and
the decision only feeds the next `Play(…, immediate: false)`. Separately, wrap
both invokes at `GaplessCoordinator.cs:843-845` in a try/catch that logs the
exception, so a broken subscriber cannot cancel a handover.

**Test it.** `OffPlaybackThread` is already the place to inject a thread. A
test can raise `EndReached` from a worker thread while the test thread calls
`Next()` in a loop under shuffle, and assert that nothing throws.

**Fixed** as proposed. The `EndReached` and `TrackFailed` handlers read which
track ended and do nothing else on the decode thread. Everything else, the
queue decision included, goes to the UI thread through
`PlaylistControlViewModel.OnUiThread`, a seam like `OffPlaybackThread` that a
test without a running dispatcher sets to inline. Once there, the handler does
nothing if that track is no longer the one playing. `GaplessCoordinator` now
catches and logs a subscriber's exception, so the handover goes on regardless.

The suggested stress test was written, and it passed on the old code as well,
so it was dropped: it proved nothing. What replaced it is a case the old code
got wrong every time.
`PlaylistAutoAdvanceTests.A_song_picked_while_the_end_of_the_last_one_is_on_its_way_keeps_playing`:
a track's end arrives from the decode thread, the user plays C before the UI
thread gets to it, and C has to stay. The old handler had already posted the
advance to B, which then replaced C.

### B2. Two definitions of path equality, and Linux gets both — **fixed**

`Flower.Core/Models/Library.cs:141, 400, 969, 1917` (all
`StringComparer.OrdinalIgnoreCase`, always),
`Flower.Core/Services/LibraryFolders.cs:12-15`,
`Flower.Core/Services/LibraryDtoMapper.cs:192-195` (both case-sensitive on
Linux)

The two `PathComparison` helpers say they are "matching how … Library.UpdateTracks
already compare[s] paths". It doesn't: `Library` ignores case on every
platform, Linux included. On a Linux server, and the Docker image is the
recommended deployment, `Song.flac` and `song.flac` in one folder are two files
on disk and one entry in `Library`. As a result:

- the second file is dropped from the rescan's carry-forward dictionary
  (`GroupBy(...).First()`),
- `_excludedPaths` excludes both files when only one was removed, and
- `ResolveCurrent`'s path index can credit a play to the wrong file.

This is uncommon in a curated library and ordinary in one ripped by several
tools. It is also the kind of disagreement that keeps producing new bugs.

**Fix.** Keep one `PathComparer`, in `Flower.Core`, chosen per OS the way
`LibraryFolders` does it, and use it everywhere a path is a key.
`LibraryIngest` (Ordinal in places) is a third opinion and should use it too.

**Fixed, per folder rather than per OS.** It was worse than described above.
`Importer`'s own seen-files set ignored case too, so on Linux a normal scan
found only one of the two files, and the other never entered the library at
all. The merge described above happens only when the folder watcher adds the
second file on its own. Choosing the rule per OS would also have been wrong
the other way round: a Linux server reading an SMB share sees a folder that
ignores case, and a Mac can be formatted to respect it.

`PathRules` (`Flower.Core/Services`) asks each library folder once, by
reading only, so it works on a read-only mount. It takes a name already in the
folder, flips its case, and checks whether that opens anything. A path is then
compared by the rule of the deepest folder it is under, and a path under none
takes the platform's rule. `Library.PathRules`, which each host sets from its
folders before every scan (`UsePathRules`), keys all of the following:
`UpdateTracks`' carry-forward, `_excludedPaths`, the removed-track index,
`AddScannedTrack`, the path index, the folder watcher's listing,
`LibraryIngest`'s replace and move, `ApplyMoves`, `TellMovesAsync`, the
catalog's `RelativePath`, and Settings' song count. `LibraryFolders.RootOf`
became `PathRules.RootOf`.
`excluded_paths` lost its `COLLATE NOCASE` in migration step 12, the first step
after the baseline. Each exclusion keeps the spelling it was stored under,
because the table now compares exactly. Tests: `PathCaseTests`, which gives the
library each rule directly so both are covered on any OS, plus one real-file
scan, and `SqliteMigrationTests` for step 12.

Left as they were: the library folder lists in Settings and `ScanRoots` still
dedupe folders ignoring case. That only matters for two folders on one Linux
disk whose names differ only in case, and they are settings, not file keys.
`LibraryDownloadService`'s collision check also still ignores case. That is
the safe direction: a false collision costs only an id-based file name.

### B3. A failed track tells the user nothing, and five in a row stop playback without a word — **fixed**

`Flower/ViewModels/PlaylistControlViewModel.cs:331-353, 425-428`

`PlaybackFailed` has no subscriber anywhere, and its comment says so ("Nothing
consumes it yet"). A track that cannot be decoded is skipped silently. After
`MaxConsecutiveFailures`, the handler returns, and the app stops in a state
that looks like playback with nothing coming out of it. The trigger is the
common case: an unreachable server, an expired pairing, or an unmounted drive.
The status bar on desktop (`ShowStatusMessage`) and a toast on the phone are
both already there to say it.

**Fixed**, though the phone had no toast after all. `PlaybackFailed` now carries
`PlaybackFailedEventArgs` (the track, and whether this failure is the one that
stopped playback), raised on the UI thread. `MainViewModel.PlaybackNotice`
turns it into a sentence. Desktop shows it on the status bar; the phone shows
it on the mini player's second line, in place of the artist. A skip is shown
for five seconds. Giving up stays until something plays.
`PlaybackNoticeTests` covers both.

### B4. The vanished-file guard checks the library root, not the folder the file was in

`Flower/Services/LibraryMirrorService.cs:218-226` (comment), `:269` (code)

The comment says a missing file is reported to the server as deleted only when
"the folder it lived in is still there and still has something in it". The
code checks `IsPresent(root)`, which is the *library root*. Take a library root
`~/Music` that has a mount point or symlink in it (`~/Music/External` pointing
at a USB drive). Unplug the drive, and every song under it reads as deleted:
the root exists and is not empty. An admin device then removes all of those
songs from the server, and every other device follows.

The server keeps the files and Restore brings them back, so nothing is lost.
But this is the failure the guard was written to prevent.

**Fix.** Check the file's own parent folder, or walk up to the root and require
every folder on the way to exist and be non-empty. That is what the comment
promises.

### B5. `SeekableHttpStream` leaks the response on a refusal, on top of September's B5 — **fixed**

`Flower.Core/Services/SeekableHttpStream.cs:713`

`response.EnsureSuccessStatusCode()` throws without disposing `response`. The
connection stays checked out of the pool until the GC finalizes it. Then the
read loop retries the refusal three times (September's B5, still open), and
each retry leaks another response. Dispose before throwing, and fix this
together with B5.

---

## C. Performance

### C1. A sync that changes nothing still rewrites the whole library and rebuilds every list — **fixed (client side)**

`Flower.Core/Models/Library.cs:1487-1624` (`MergeSyncedTracks`), `:571`
(`UpdateTracks`), `:2360-2365` (`NotifyLibraryChanged`)

September's D6 noted this and left it. Measured against what actually triggers
it, it costs more than that note suggests:

1. **The server's change token moves on almost everything.** Every play a
   listener reports (`RecordPlayFor`, `MergeReportedTrackState`), every rescan
   including one that found nothing new (`UpdateTracks` always runs
   `InvalidateIndexes`), and every iTunes import on the server.
2. A moved token makes the next `GET /library` on every device a full fetch,
   6-8 MB according to the comment at `LibrarySyncService.cs:111`. The client
   keeps the token in memory only (`:109-114`), so the first sync after every
   launch is a full fetch as well.
3. A full fetch runs `MergeSyncedTracks`, which swaps `Tracks` unconditionally,
   calls `InvalidateIndexes`, `RebindPlaylistTracks` and
   **`ReplaceAll(Tracks)`** (an upsert of every row, 16k on the library
   measured, **inside `Library._lock`**), and raises `LibraryChanged`, which
   repopulates every list.

So one person listening at home makes every other paired phone rewrite its
whole database and rebuild its UI at that phone's next sync. That happens while
`Library._lock` is held, and that lock is the one the end-of-track play count
waits on (see the comment at `PlaylistControlViewModel.cs:240-246`).

**Fix, in order of payoff.**

1. Have `MergeSyncedTracks` notice that it changed nothing: no newcomers, no
   stale tracks, and no field moved on any matched track. In that case, skip
   the swap, the persist and the event. Every `Merge*` helper already knows
   whether it assigned anything.
2. When something did change, persist only the changed rows. `ITrackStore`
   already has `Upsert`.
3. Separately, think about giving play counts their own token. Plays are by
   far the most frequent change, and they are the one change a device can
   apply without re-reading the catalog.

The same "rewrite all on no change" applies to the desktop's two iTunes imports
on every launch (`ITunesImportCoordinator.ApplyAsync:84`). Each one calls
`NotifyLibraryChanged` whether or not a single count moved, which is two
whole-table rewrites and two full UI rebuilds per launch. The importers return
`void`. They should return whether they changed anything, and the server's
`ITunesIntegration.ApplyImports` would then mean "changed" rather than "ran".

**Fixed: steps 1 and 2.** `MergeSyncedTracks` reads each matched track's
mergeable state before and after the merge (`Library.MergedTrackState`) and
then does one of three things:

- **Nothing moved.** No swap, no write, no event, and the change token stays
  where it was.
- **Moves in place** (plays, last played, stars, options, a resume position, a
  placeholder's tags or art). Only those rows are written, in one transaction
  through the new `ITrackStore.UpsertMany`, and one `TrackChanged` names those
  tracks with the kinds that moved, as `Remote`. Rows refresh, and lists
  rebuild only when tags moved.
- **Songs arrived or left, or a song's origin or Date Added moved.** The old
  path: swap, `ReplaceAll`, `LibraryChanged`.

Tests: `LibrarySyncNoOpPullTests`. Still open: the network side (the token
moves on every play, so every device still downloads the whole catalog, now
cheaply merged), the server rewriting everything after a rescan that found
nothing new, and the client's iTunes imports.

### C2. The server reads cover art from the audio file on every request — **fixed**

`Flower.Server/Endpoints/MediaEndpoints.cs` (`GetCoverArt`,
`CoverArtCandidates`), `Flower.Core/Services/LocalAlbumArt.cs:133-175`

Every cover request, and each of up to 32 ids in a batch request, opens audio
files with TagLib. For an album id, that means trying every track of the album
in turn. When none has embedded art, it also enumerates that track's folder
looking for `cover.*`/`folder.*`, once per track, the same folder each time.
An album with no art costs a TagLib parse of each track and N directory
listings, all of it over the NAS share on the reference deployment, before the
404. Neither the art nor its absence is remembered, and responses carry no
`ETag` or `Cache-Control`. The art also goes out at full size (1400 px and up)
to phones that draw it at 100 px.

The client's content-addressed disk cache limits how often this happens per
device. It doesn't limit the first cold scroll of every device, or a browser
tab, which has no disk cache.

**Fix.** Keep a small server-side map from album to `(source file, offset,
mime)`, or simply to the bytes, invalidated by `ChangeToken` and by
`TrackArtwork`/`AlbumArtWriter` writes. Cache misses too. Look in each folder
once per album, not once per track. Send an `ETag`. A thumbnail size parameter
on the batch route can come later.

**Fixed** (`CoverArtCache`, used by both routes), differently in three ways.

- **Not invalidated by `ChangeToken`.** That token moves on every play, so a
  house with a few listeners would empty the cache constantly. Instead, a
  rescan or any artwork, tag or file change in the library empties it, and a
  play does not.
- **Checked against the disk.** Each answer, a picture or "none", is kept
  with every file and folder the search went through: the songs whose tags had
  no picture, the folders it listed, and the file the picture came from. Before
  the answer is used again, each of those is checked to be the same size and
  age, which costs a stat apiece instead of a parse and a listing. A cover
  replaced on disk behind Flower's back changes its own file. One added,
  deleted or renamed changes its folder's modified time. Either way the next
  request notices. A folder the search never reached, because a song before it
  has a picture in its tags and that wins, cannot change the answer and is not
  checked.

The pictures are held up to 64 MB, least recently asked for going first.
`LocalAlbumArtReader.FirstFor` lists each folder once per album and keeps the
old precedence exactly: song by song, embedded picture then folder image.

There is no `ETag` yet. The native clients keep their own disk cache, and
`HttpClient` has no HTTP cache to revalidate with. The browser tab could use
one, and that is left for later along with the thumbnail parameter.
`CoverArtCacheTests` covers a changed cover, a cover added to a folder that had
none, a deleted one, and a play that clears nothing.

### C3. Every art cache hit walks two lists — **fixed**

`Flower/Services/AlbumArtLoader.cs:164-176` (`Retain`), `:109-145`
(`StrongLru`)

September's D2 (the sweep of the whole weak cache on every hit) is still there.
Since then, thumbnails have gained a 256-entry `StrongLru` whose `Retain` does
a linear `RemoveLocked` scan of a `LinkedList`. So every thumbnail hit during a
fling costs a 256-node walk, plus a sweep of every album ever shown. Each is
small on its own, and both run on the UI thread, during scrolling, at
tile-appearance rate.

**Fix.** Back the LRU with a dictionary of key to node, which makes it O(1),
and throttle the weak sweep (D2's fix).

**Fixed** as proposed. The sweep runs at most every thirty seconds, and a hit
no longer allocates a new `WeakReference` for the entry it found. See
September's D2.

### C4. Track Info decodes the whole picture to read its size, in three places — **fixed**

`Flower/Views/TrackInfoWindow.axaml.cs:569`,
`Flower/Views/Mobile/TrackInfoView.axaml.cs:476, 496`

This is September's D1 again, in the two Track Info views: `new Bitmap(...)`
of the full image just to read `PixelSize`, and the mobile view decodes it a
second time for the screen. The header-reading helper D1 proposes would cover
all four call sites.

**Fixed** with September's D1: `ImageHeader`, behind
`AlbumArtLoader.MeasureArt` for both views, and the phone's full-screen
picture is decoded once at the size it is shown.

---

## D. One source of truth

### D1. Track Info is implemented twice, in code-behind, where nothing can test it — **fixed**

`Flower/Views/TrackInfoWindow.axaml.cs` (1000 lines),
`Flower/Views/Mobile/TrackInfoView.axaml.cs` (681 lines)

The write side is shared (`TrackTagEdits`, `AlbumArtEditor`, `CanEditTagsOf`,
`PairedServerSourceName`). The read side, which decides what the sheet shows,
is written twice, and the two copies have already drifted:

- **Mixed values.** Desktop computes `Distinct()` per field, and mobile calls
  `TrackTagFields.CommonValue`. They are two definitions of "these songs
  disagree".
- **Fields.** Desktop shows `EncoderProfile` ("Encoding") and edits the
  compilation flag. Mobile shows neither, so on a phone a compilation flag can
  only be lost through a tag rewrite, never set.
- **Formatting.** Duration, bitrate, sample rate, channels, bit depth and date
  are formatted by two copies of the same lambdas.
- **Art.** `MeasureArt` exists twice (see C4).

All of this is logic about tracks, not layout, and CLAUDE.md's own rule puts it
in a view model. **Fix:** a `TrackInfoViewModel` (or a pure `TrackInfoSummary`)
that owns the fields, the mixed-value rule, formatting, editability and the
pending edits. Both views bind to it, and the drift above becomes a test.

**Fixed** as proposed. `TrackInfoViewModel` (`MainViewModel.TrackInfo()`)
owns every box (`TrackInfoField`: its text, its "Multiple values"
placeholder, what it showed), the one mixed-value rule, every read-only value
written out once, who may edit, the compilation flag staged with the tags,
the star and the three playback options written at once, and `Save`. Both
views bind to it. The phone gains the Encoding row and the compilation
checkbox, so it can set the flag now and not only lose it. What stays in the
views is theirs: tabs, Prev/Next, the art bitmaps and pickers, drag and drop,
the suggestion chips. `TrackInfoViewModelTests` covers the rules, and
`TrackInfoWindowTests` checks that a typed box is what OK saves.

### D2. Context menus are defined twice, once per head — **fixed for the song and album menus**

`Flower/Views/MainView.axaml.cs:1097-1210, 1471-1660` (desktop, built in
code-behind), `Flower/ViewModels/Mobile/MobileMainViewModel.ScreenMenu.cs` with
`Views/Mobile/TrackActionsView.axaml` and `AlbumActionsView.axaml` (phone)

The rules for when an action applies are shared (`LibraryRemoval.Removable`,
`LocalFileDeletionDialog.UpdateMenuItem`, `Availability.IsDownloadable`). The
lists of actions are not, and they differ already: the phone's album actions
include Shuffle and desktop's album menu doesn't. Each new action has to be
added in two places, in different idioms. A platform-free description of a
menu already exists (`ContextMenuEntry`, built for the macOS `NSMenu` path). If
the view models produced those for track, album and playlist targets, desktop
would render them as `ContextMenu`/`NSMenu`, the phone would render them as its
sheets, and a test could cover them.

**Fixed** for the song and album menus, with a description of its own rather
than `ContextMenuEntry`, which is the macOS drawing of an Avalonia menu and
would have had to carry the phone's sheets as well. `ActionMenus.ForSongs` and
`ForAlbum` list the entries (`ActionMenuEntry`: a `MenuAction`, its title and
icon, destructive or not), in groups, from `MenuFacts` that
`MainViewModel.MenuFactsFor` works out with the rules that were already shared.
The desktop draws them as a `ContextMenu` (`DesktopActionMenu`), so `NSMenu`
still comes for free, and the phone's two sheets list them. Each head runs an
entry its own way: dialogs on one, confirmation sheets on the other.

The desktop had three song menus, not one: the track list's, the album grid's
expanded rows' (`AlbumTrackList`), whose Add to Playlist offered smart
playlists, and the album tile's. All three are `ActionMenus` now. The desktop's
album menu gains Shuffle, and Remove from Continue Playing on a Home tile. Both
heads now say the same thing: "Get Info", "Play", and counts in Download,
Delete and Remove. Not done: the sidebar's playlist menu and the phone's screen
menus, which were never pairs.

### D3. "Play these songs as the queue" is written three times — **fixed**

`MainViewModel.PlayTracks` (`:2396`), `MobileMainViewModel.PlayTracks`
(`:1489`, identical), `MobileMainViewModel.PlayAlbum` (`:1053`, the same again
over `Main.Rows`). `MainViewModel.PlayAlbum` and `PlayTrackInAlbum` also build
`new Playlist("Now Playing Queue", ...)` themselves instead of going through
`SetPlayQueue`. **Fix:** the mobile copies call `Main.PlayTracks`, and every
place that builds a queue goes through `SetPlayQueue`.

**Fixed** as proposed. `MainViewModel.PlayTracks` is the one copy: the phone's
header Play and Shuffle, its album menu's and its screen menu's Shuffle all
call it, and `PlayAlbum`/`PlayTrackInAlbum` queue through `SetPlayQueue`. The
phone's header buttons used to queue the rows of whatever list was in front of
the user (`CurrentTrackRows`) while picking the starting song from `Main.Rows`.
That never mattered, because the header only shows where those two are the same
list, but now both come from `Main.Rows`.

### D4. Each setting is wired in about seven places — **fixed for device settings**

Take `HomeContinuePlayingLimit`. It lives in `AppSettings`, in a
`MainViewModel` property (persisted immediately, and what the phone binds to),
in two draft records in `ISettingsBackend.cs` (`:85`, `:170`), in
`LocalSettingsBackend` load and save (`:54`, `:96`), and in a
`SettingsViewModel` property with its snapshot and save (`:212`, `:523`,
`:619`), before either view's XAML. The truth itself is single, since
`AppSettings` behind `MainViewModel` is the only store. The cost is that desktop
edits a draft and the phone persists at once, so every setting is written for
both models. That isn't a defect today. It is why adding a setting takes a
long time, and why one is eventually going to be wired into one model and not
the other.

**Fixed for the settings only a device has**: theme, player-bar placement and
tint, Home's three shelves, Send logs, and transfers away from home. Desktop no
longer drafts them. `ISettingsBackend.Device` is the `MainViewModel` when the
screen shows this device's own settings, and the panel binds each of them
straight to its property there, so it applies when touched, as on the phone.
One of these settings is now its `AppSettings` field, one `MainViewModel`
property, and a control in each view. The two pickers go through
`EnumIndexConverter`, which reads the enum's own order as the index.

Cancel now drops only what is still drafted: the name, the library folders and
the Music.app imports. Those are the settings a server has too, where a save is
one request. The two imports stay drafted on a device as well, because saving
them starts the import. Getting this wrong now costs one place instead of two:
a new server setting still goes through the snapshot, the draft and
`SettingsViewModel`, and a new device setting does not.

### D5. The retired review documents still exist — **fixed**

`CLAUDE.md` and `docs/CITED-DECISIONS.md` both say `ARCHITECTURE-REVIEW.md`
and `OPEN-INTERNET-REVIEW.md` were "finished, and deleted once they were". Both
files are still tracked, at 669 and 617 lines (`git ls-files docs/`), and only
`SMART-PLAYLIST-PLAN.md` was actually deleted. So the ~140 citations now
resolve against two documents that disagree in detail. Either delete the two
files, as the index says happened, or change the index to say they remain.

**Fixed** the first way. Both are deleted, and `CITED-DECISIONS.md`'s
`git show` lines name `070c2ca`, the last commit that has them.

---

## E. Outdated comments

**Fixed (October 2026)**, all but one. The `LibraryMirrorService` comment
describes what the guard should do, so B4 fixes the code to match it rather
than the other way round. Along the way, the references to
`PeerMediaClient.BuildUrl`/`BuildUrlAsync`, renamed `BuildSignedUrlAsync`,
were corrected too, and `GaplessFormat.LibVlcFourCc`, which nothing read, was
deleted.

Each of these describes something the code no longer does, in the present
tense. Historical notes ("this used to…") are fine and are not listed. The
worst offender is the first group, because a reader working on the threading
code would trust it.

**Thread and decoder claims that are now false**

- `Flower/ViewModels/PlaylistControlViewModel.cs:234, 266, 366, 387`: "runs on
  the LibVLC decode callback thread". It runs on the FFmpeg decode thread (B1).
- `Flower/Audio/GaplessFormat.cs:27, 37`: "What TrackDecoder asks LibVLC for".
- `Flower/Audio/ITrackDecoder.cs:9-25`: "The subset of TrackDecoder's surface",
  "LibVLC routinely lands".
- `Flower/Audio/Ffmpeg/FfmpegTrackDecoder.cs:18, 191, 219`: "alongside the
  LibVLC-based TrackDecoder", "The same stream LibVLC reads through today".
- `Flower/Audio/GaplessCoordinator.cs:769`: this is a **log message**, not a
  comment, so it reaches users' logs: "…or LibVLC may have ended early".
- `Flower.Core/Services/SeekableHttpStream.cs:169` ("LibVLC calls
  MediaInput.Open"), `:248` ("see TrackDecoder.PrepareAsync").
- `Flower.Core/Services/SignedRequestCanonicalizer.cs:23`: "the
  LibVLC/PeerMediaClient.BuildUrl case".
- `Flower.Core/Models/Library.cs:1900-1903`: "a rescan replaces every Track
  instance with a brand-new one carrying a brand-new Id". The opposite is true:
  `CarryForwardMutableState` (`:1360`) carries the `Id` over, and that is what
  `RescanCarryForwardGuardTests` checks.

**Protocol claims that are now false (OpenSubsonic is gone)**

- `Flower.Core/Services/PeerCredentialsHandler.cs:95-97`: "The Subsonic params
  (u/t/s/v/c/f/id) are deliberately kept: they are what the server falls back
  to when signature auth is not in play". No such fallback exists any more.
- `Flower.Core/Services/LibraryDtoMapper.cs:46-52`: "The /rest browse endpoints
  pass nothing".
- `Flower.Core/Services/SeekableHttpStream.cs:858`: "a Subsonic error envelope
  on its mandated HTTP 200".
- `Flower.Core/Models/LibrarySnapshot.cs:10, 21, 25`: "the three ids a Subsonic
  client can star", "wherever an OpenSubsonic request has to be answered".
- `Flower.Core/Models/Library.cs:187, 1424, 1992, 2000, 2271, 2645`:
  "Subsonic-style id", "Subsonic's scrobble".
- `Flower.Core/Importer/RemoteLibraryImporter.cs:35`: "a genuine
  SubsonicLibraryImporter is still worth having for third-party servers".
- `Flower/Services/PlaylistSyncService.cs:85`,
  `Flower/Services/LibrarySyncService.cs:86`: "Under Client/Server roles". The
  role concept is gone.
- Tests: `GaplessCoordinatorTests.cs:57-61` still builds URLs with
  `u=&t=&s=&v=1.16.1&c=Flower`, and `SeekableHttpStreamTests.cs:26` and
  `LoopbackMediaServer.cs:15` both describe themselves as standing in for "the
  Subsonic /stream endpoint".

**Smaller items**

- ~~`LibraryFolders.cs:12`, `LibraryDtoMapper.cs:192`: "matching
  Library.UpdateTracks". False, see B2.~~ Gone with B2's fix.
- `LibraryMirrorService.cs:222`: "the folder it lived in". The code checks the
  root, see B4.
- ~~`LocalAlbumArt.cs:148`: "Debug, not Warning", above a `LogTrace`.~~ Rewritten
  with C2's fix.
- `PlaylistControlViewModel.cs:426-427` points at `ARCHITECTURE-REVIEW.md` for
  a "couldn't play this" surface, and `GaplessAudioManager.cs:144-147` waits for
  one too. That is B3, and it is tracked here now.

**`CLAUDE.md` itself**

- "Startup: load cached `library.json` synchronously", and the Persistence
  paragraph's `library.json`, `playlists.json` and `config.json`: the library
  and playlists are in `flower.db` (SQLite, `FlowerDb`), and column state is in
  `settings.json` through `AppSettingsStore`. `ColumnManager → config.json`
  under "UI Structure" is stale in the same way.
- The retired review documents, see D5.

---

## F. Where testing is thin

The suite is broad and disciplined. These are the gaps the findings above went
through. Each would have caught the finding it is listed against.

1. **Concurrent end-of-track vs. UI** (B1). No test raises `EndReached` from a
   different thread than the one editing the queue. The existing tests run
   `OffPlaybackThread` inline and raise the event from the test thread, so the
   thread the app actually uses is never exercised.
2. **Case-only path differences on Linux** (B2). This can be written as a
   `Library` unit test with two tracks whose paths differ only in case, run on
   Linux CI. Today it would fail. **Written** (`PathCaseTests`), and it needs no
   Linux runner, because the folder's rule is given to the library directly.
3. **A server-wide failed-redemption budget** (A1).
   `PairRedeemEndpointTests` covers the per-source limit only. Once the fix is
   in, a test can spread failures across many fake /64 sources (`TestServer`
   lets a test set `RemoteIpAddress`) and assert that the outstanding code is
   burned.
4. **What a guest's manifest contains** (A2). `SyncEndpointTests` asserts how
   the manifest is shaped, but not what a non-admin caller is allowed to see in
   it.
5. **A sync that changes nothing** (C1). `LibrarySyncMergeTests` can assert
   that merging the same catalog twice raises no second `LibraryChanged` and
   writes no rows.
6. **Track Info's read side** (D1). There is nothing to test until D1 moves the
   logic out of code-behind.
7. **September's F list is all written now:** the equalizer at
   44.1/32/22.05 kHz (F.1, `EqualizerTests`), `Library.Snapshot` under
   concurrent invalidation (F.3, `LibrarySnapshotRaceTests`), cancelling a
   throttled `SeekableHttpStream` (F.4, `SeekableHttpStreamTests`), and the
   dedicated `NonceReplayGuard` test (F.6, `NonceReplayGuardTests`). F.2 is
   resolved: September's A2 is fixed, and `GaplessHandoverResponsivenessTests`
   is its regression test.
8. **Untested logic files** (no test names the type and none drives it
   indirectly): `PlaylistSessionOverlay.Apply`, the conflict overlay every
   playlist sync goes through (`PlaylistThreeWayMerge` beside it is well
   tested), `CrashReportScanner`, and `AlbumTracksViewModel`.

---

## G. Still open from September

Checked against the code, not taken from the September document's own marks.

| Item | State | Where it stands |
|---|---|---|
| A1 Equalizer unstable below 32 kHz | fixed | October 2026: bands at or above 0.45 × the rate are bypassed |
| A2 Retire/PromoteTarget deadlock | fixed | as recorded |
| B1 Sync bodies materialised three times | fixed | October 2026: read once into one array, which the handlers deserialize (`RequestGate.BodyOf`) |
| B2 Nonce prune on every insert | fixed | October 2026: the sweep runs every 15 s, as `RateLimiter`'s does |
| B3 Stale `Library.Snapshot` | fixed | October 2026: each index is tagged with the generation it was built for |
| B4 Uncancellable stream waits | fixed | October 2026: `SeekableHttpStream.Cancel`, called before the decoder's join |
| B5 Refusals retried as transient | fixed | October 2026, with the response leak in B5 above |
| B6 Late arm dropped | fixed | |
| C2 Log ingestion unbounded | fixed | `MaxEntriesPerReport`, `MaxBytesPerDevice` |
| C4 Unbounded nonce/fingerprint key lengths | fixed | October 2026: over 64 characters is refused as malformed before it becomes a key |
| D1 Double decode of oversized covers | fixed | October 2026: the size comes from the header (`ImageHeader`), here and in Track Info (C4) |
| D2 Weak-cache sweep on every hit | fixed | October 2026: at most every 30 s, with C3's O(1) LRU |
| D3 `SortedSet` per scroll | fixed | |
| D4 Watchdog timer for the life of the process | fixed | October 2026: runs only while playing, one tick at a time |
| D5 `ThreadCount` sees pool threads only | fixed | October 2026: `LiveDecoders` counts the decoders themselves |
| E2 `GaplessFormat` process-wide statics | fixed | October 2026: a `PcmFormat` the sink settles and every piece is handed |
| E3 Shell view models too large | **worse** | `MobileMainViewModel` 1609 → 3215 lines, `MainViewModel` 1885 → 2893 |
| E5 `GCHandle` leak on a second `Start` | fixed | October 2026 |
| E6 Armed handlers kept after promotion | fixed | October 2026 |
| E7 `Process.Start` on mobile | **open** | three sites now: `MainViewModel.cs:1411, 2764`, `SettingsPanel.axaml.cs:177` |
| G2 `IAudioManager` partial on WASM | fixed | October 2026: `IAudioManager.Features`, and the equalizer is not offered without it |
| E8, G1 | not re-checked in depth | |

### Dead code found along the way

- **Denied peers** — **removed (October 2026)**, `denied-peers.json` with
  them. `TrustedPeerStore.DenyAsync`, `ForgetDenialAsync`,
  `LoadDenied`, `denied-peers.json`, `DeniedPeerRow`,
  `SettingsViewModel.DeniedDevices` and its command, the
  `ISettingsBackend` members, and the "denied devices" template in
  `SettingsPanel.axaml:630`. Nothing denies anything since the live-approval
  prompt went away with peer-to-peer. `LocalSettingsBackend` throws for it,
  `RemoteServerSettingsBackend` returns `[]`, and only tests call `DenyAsync`.
  `ApproveAsync` still rewrites `denied-peers.json` on every pairing.

---

## Suggested order

1. **A1**: a server-wide failed-redemption budget. This is the one finding here
   that lets a stranger in.
2. **B1**: move the queue decision onto the UI thread, and wrap the
   coordinator's invokes in try/catch. This is the one that can stop playback.
3. **September A1**: the equalizer Nyquist guard and its test. Still open, and
   still cheap.
4. **C1, step 1**: skip a merge that changed nothing. This is the biggest
   battery and CPU win on phones, and only a few lines.
5. **B2**: one `PathComparer`.
6. **A2 + A3**: what a guest's manifest and `/info` reveal.
7. **B3**: tell the user when a track would not play.
8. **B4, B5** with September's B5, and **A4**.
9. **C2, C3**, with September's D1/D2.
10. **D1, D2**: the Track Info view model and shared menu descriptions. These
    are the two refactors that remove a whole class of cross-head drift.
11. **E**, **D5** and the dead denied-peer code. These are mechanical changes
    and can go in one commit.
