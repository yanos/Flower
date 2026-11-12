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
original number (*September E3*), which is what a source comment
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

1. **September E3**: wait until something wants it.

---

## Organisation

### September E3. The shell view models are still large — reduced

`Flower/ViewModels/MainViewModel.cs` (3135 lines),
`Flower/ViewModels/Mobile/MobileMainViewModel.cs` (2647 lines), as of
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
`BrowserPairing.…`, `ArtistPage.…`, `PairingCodes.…`). Both then grew again,
from 2606 and 2654 lines to 3251 and 2738. In November 2026 two more moved
out the same way:

- From `MobileMainViewModel`: `MobileLibraryRemovalViewModel`, the Remove
  from Library sheet (`Removal.…`). The two menu entries that ask it stay,
  since what they ask about is the shell's action target.
- From `MainViewModel`: `ServerAdministrationViewModel`, the selected
  server's settings panels and Open in Browser (`ServerAdmin.…`).

Most of what is left is what a shell is: navigation, sheet state, and
forwarding to the shared services. Each constructor is about 550 lines of
that wiring. Looked at and left alone:

- Mobile's smart playlist sheet. `ScreenTitle`, the rows-on-screen choice and
  the Vibe sheet beside it all reach into it, so moving it would leave as
  much forwarding behind as it took away.
- Desktop's Back and Forward. The history itself is already
  `NavigationHistory`; what stays is the glue to the sidebar.

The next candidate is still whatever grows into a feature of its own.

---

## Checked and sound

Things that look like defects on a first read and are not. Recorded so the next
review does not spend its time here. From the September review unless noted.

- **`OutputStage`'s fade state being written from two threads.** Moot since
  November 2026: `OutputStage` was deleted with the managed render callback
  (AUDIO-QUALITY-PLAN.md, Phase 6 step 8). The bridge's transport envelope,
  which replaced it, reaches the callback only through atomics.

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
  `AppUpdatesViewModel.Start`, which is not. `CheckAsync` turns every failed
  check into a Status line, so all it could let out was a throwing
  property-changed handler; since November 2026 `Start` logs that per poll
  and carries on rather than ending the process.

- **Swallowed exceptions.** In September there was one empty catch in the
  client and server (`LibraryBrowserViewModel`,
  `catch (OperationCanceledException) { }`), and it is correct. Everything
  else logged. `AppUpdatesViewModel.Start`'s empty cancellation catch is the
  same, and correct for the same reason.
