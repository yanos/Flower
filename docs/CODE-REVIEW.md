# Code Review

What is still open from two whole-codebase reviews, merged into one backlog in
November 2026:

- **September 2026**, against `master` at `9ab685e`: performance, security,
  audio playback and quality, memory, test coverage, organisation and dead
  code.
- **October 2026**, against `master` at `1c61ad7`: bugs, test coverage,
  performance, outdated comments, places with more than one source of truth,
  how well controls are shared across the heads, and security.

**Only what is still open is here.** Each finding keeps its review and its
original number (*September E3*, *October B4*), which is what a source comment
cites. Every fixed or moot finding is one line in `CITED-DECISIONS.md`, which is
also what an older citation of `CODE-REVIEW-2026-09.md` or
`CODE-REVIEW-2026-10.md` resolves against. Several findings here are what
remained of one that was otherwise fixed. The full original text of both:

```bash
git show 2e55685:docs/CODE-REVIEW-2026-09.md
git show 2e55685:docs/CODE-REVIEW-2026-10.md
```

Everything below was read from the code, not reproduced. Severity is about what
happens to a listener or the library, not about how hard the fix is.

---

## Suggested order

1. **October B4**: check the file's own folder. This is the one finding left
   that can take songs off every device.
2. **October C1.3**: have the iTunes importers report whether they changed
   anything. A few lines, and two full rewrites saved on every desktop launch.
3. **October C1.1**: a separate token for plays. This is the biggest network
   win for phones, and the largest change on this list.
4. **September E8**, **September G1**: when
   convenient.
5. **October A1**, **October C2**, **September E3**: wait until something
   wants them.

---

## Security and the trust boundary

### October A1. Pairing codes in links could be much longer

`Flower.Server/Services/PairingCodeService.cs`

Guessing is fixed: the 20th wrong code from any source in ten minutes cancels
every outstanding code (`PairingCodeGuessingTests`). What is left is the other
half of the proposal. The 5-character length only matters for a code read
aloud. The invite link, the QR code and the browser URL all carry the code
without anyone typing it, so they could carry a much longer one, and a guess
against those would be hopeless rather than merely bounded.

---

## Bugs

### October B4. The vanished-file guard checks the library root, not the folder the file was in

`Flower/Services/LibraryMirrorService.cs:221-229` (comment), `:276` (code)

The comment says a missing file is reported to the server as deleted only when
"the folder it lived in is still there and still has something in it". The
code checks `LibraryFolders.IsPresent(root)`, which is the *library root*. Take
a library root `~/Music` that has a mount point or symlink in it
(`~/Music/External` pointing at a USB drive). Unplug the drive, and every song
under it reads as deleted, because the root exists and is not empty. An admin
device then removes all of those songs from the server, and every other device
follows.

The server keeps the files and Restore brings them back, so nothing is lost.
But this is the failure the guard was written to prevent.

**Fix.** Check the file's own parent folder, or walk up to the root and require
every folder on the way to exist and be non-empty. That is what the comment
promises, so the comment stays and the code changes to match it.

---

## Performance

### October C1. Every play still makes every device download the whole catalog

`Flower.Core/Models/Library.cs` (`RecordPlayFor`, `MergeReportedTrackState`,
`UpdateTracks`), `Flower/Services/LibrarySyncService.cs`,
`Flower/ViewModels/ITunesImportCoordinator.cs:72-84`

The client half is fixed: a merge that moved nothing writes nothing and raises
nothing, and one that moved only plays or stars writes only those rows
(`LibrarySyncNoOpPullTests`). So is a rescan that found nothing new, which no
longer moves the token (C1.2, in `CITED-DECISIONS.md`). Two things are left,
numbered as they were.

- **C1.1. The server's change token moves on every play.** Every play a
  listener reports moves it, so every other device's next `GET /library` is a
  full fetch, 6-8 MB on the library measured. The merge is now cheap, but the
  download is not. Plays are by far the most frequent change, and the one a
  device could apply without re-reading the catalog, so they could have their
  own token.
- **C1.3. The desktop's two iTunes imports rewrite everything on every launch.**
  `ITunesImportCoordinator.ApplyAsync` calls `NotifyLibraryChanged` whether
  or not a single count moved, which is two whole-table rewrites and two full
  UI rebuilds per launch. `ITunesPlayCountImporter.Apply` and
  `ITunesDateAddedImporter.Apply` return `void`. They should return whether
  they changed anything, as the server's `ITunesIntegration.ApplyImports`
  already does.

`ONLINE-METADATA-PLAN.md` cites this shape: a popularity refresh that returns
the same ranking must not count as a change.

### October C2. Cover art has no `ETag` and no thumbnail size

`Flower.Server/Endpoints/MediaEndpoints.cs`

The server no longer re-reads art from the audio file on every request
(`CoverArtCache`). It still sends no `ETag` or `Cache-Control`, and art goes out
at full size (1400 px and up) to phones that draw it at 100 px. The native
clients keep their own disk cache, and `HttpClient` has no HTTP cache to
revalidate with, so an `ETag` would help the browser tab only. A thumbnail size
parameter on the batch route would help everyone's first cold scroll.

---

## Organisation

### September E3. The shell view models are still large — reduced

`Flower/ViewModels/MainViewModel.cs` (3251 lines),
`Flower/ViewModels/Mobile/MobileMainViewModel.cs` (2738 lines), as of
November 2026

`ARCHITECTURE-REVIEW.md` Tier 4.2 decomposed `MainViewModel` and marked itself
done, but the shell-sized view model problem was only half in its scope. The
mobile shell never was in it.

They are not duplicates. When this was checked, only 8 public member names
were common to both (`Dispose`, `IsShowingAlbumGrid`, `IsShowingTrackList`,
and the five transport commands), against 157 desktop and 116 mobile. These
are two different shells over shared services, which is the right shape. They
are simply both large.

In October 2026 the parts that had grown into features of their own, and
needed nothing of the shell but a notification, moved out:

- From `MobileMainViewModel`: `MobileSearchViewModel` and
  `MobileServerPairingViewModel`.
- From `MainViewModel`: `BrowserPairingViewModel`, `ArtistPageViewModel` and
  `PairingCodeIssuerViewModel`.

The views bind through the new property (`Search.Query`, `Pairing.…`,
`BrowserPairing.…`, `ArtistPage.…`, `PairingCodes.…`). Since then both have
grown again, from 2606 and 2654 lines. Most of what is left is what a shell
is: navigation, sheet state, and forwarding to the shared services. The next
candidate is whatever has grown into a feature of its own since.

### September E8. Three loose ends in the album grid

`Flower/Controls/AlbumGridView.axaml.cs`, `Flower/Controls/AlbumGridRowControl.axaml.cs`,
`Flower/ViewModels/Mobile/AlbumTileViewModel.cs`

The expansion height is measured now. Three things are left, independent,
low-risk, none a blocker:

- `RebuildRows` re-chunks on every `SizeChanged` wider than a pixel, so a
  resize snaps between column counts instead of reflowing. Needs a debounce.
- No keyboard navigation between the grid's tiles. An expanded album's track
  list has it (`AlbumTrackList`), but the grid does not.
- `AlbumTileViewModel` and friends still live under `Flower.ViewModels.Mobile`,
  although desktop depends on them directly.

---

## Testing

### September G1. The headless teardown flake

Inherited from the August architecture review.

A full `Flower.Tests` run has failed 0-2 tests, a *different* one each time.
The failure is not an assertion: it is `[Test Case Cleanup Failure] ... The
calling thread cannot access this object because a different thread owns it`,
out of `Avalonia.Threading.Dispatcher.VerifyAccess` during teardown. The
victims observed have included `MusicListPanelTests`,
`CurrentlyPlayingControlViewModelTests`, `PlaylistPlaybackIntegrationTests`,
`MainViewModelDeviceSidebarTests`, `AlbumGridViewRebuildTests`,
`MusicListViewGestureTests` and `AlbumArtLoaderTests`, and each passes when
its class runs alone. The bug is in how the headless session's UI thread is
shared across test classes, which is why chasing the named victim has never
led anywhere.

Not re-checked since September. It is worth fixing at the harness level before
it trains anyone to ignore a red run. That is the real cost of a flake, and why
this is more than cosmetic.

(The other flake in this finding, `FakePeerHttpServer` racing its own port, is
fixed.)

---

## Checked and sound

Things that look like defects on a first read and are not. Recorded so the next
review does not spend its time here. From the September review unless noted.

- **`OutputStage`'s fade state being written from two threads.** `BeginFadeIn`
  and `ArmFadeIn` write `_fadeInLength`, `_fadeInRemaining`, `_fadeOutLength` and
  `_fadeOutRemaining` from the UI thread, and the render callback owns all four.
  It is safe: both callers are `MiniaudioSink.Resume`/`Pause`/`Stop`, all under
  `_gate`, and all with the device stopped — `ma_device_stop` waits for the
  callback to return before it does. A divide-by-zero on
  `_fadeOutRemaining / (float)_fadeOutLength` is not reachable.

- **`GaplessRingBuffer`'s generation protocol.** The index-before-generation
  publication order, the "counterpart has not rebased yet, treat as empty" rule,
  and the post-copy generation re-check are all correct, and the `Interlocked`
  rather than `Volatile` choice for the 64-bit indices is right for the armeabi-v7a
  build the comment cites.

- **`Library.Tracks` being enumerated outside the lock.** Safe, because every
  mutation replaces the list wholesale rather than mutating it in place.

- **Smart playlist recomputation looping forever.** `PlaylistsChanged` triggers
  `Schedule()`, and `Refresh()` can change playlists — but `LimitSelector.Random`
  draws from a `Random` seeded per playlist id (`SmartPlaylistEvaluator.SeededFor`),
  so a pass over an unchanged library produces an identical result and the
  fixed point holds. This was thought about.

- **The `Play`/`SetUpcoming` identity guards.** `Track.Id` is a per-instance
  `Guid` that `Clone()` preserves and `Library.CarryForwardMutableState`
  carries across a rescan (`RescanCarryForwardGuardTests` protects it), so two
  live spellings of one track compare equal and two different tracks never do.
  Repeat-one is unaffected: the guard only fires when `_current != null`, and a
  track that has drained has already cleared it.

- **`RateLimiter`'s sliding window.** The weighted previous/current
  approximation is the standard one and is implemented correctly, including the
  compare-and-retry against `ConcurrentDictionary`. `KeyFor` collapsing IPv6 to
  /64 while leaving link-local at full precision is the right call for both
  reasons its comment gives.

- **`async void`.** In September, all were genuine event handlers in Views,
  and the non-handler cases had been converted, with comments saying why.
  Since then: two timer ticks (`SettingsViewModel.OnLogPollTick`,
  `AiFeaturesSettingsViewModel.OnTick`), which are handlers, and
  `AppUpdatesViewModel.Start`, which is not. It catches only cancellation, so
  anything else `CheckAsync` lets out would take the process down. Not
  reviewed further.

- **Swallowed exceptions.** In September there was one empty catch in the
  client and server (`LibraryBrowserViewModel`,
  `catch (OperationCanceledException) { }`), and it is correct. Everything
  else logged. `AppUpdatesViewModel.Start`'s empty cancellation catch is the
  same, and correct for the same reason.
