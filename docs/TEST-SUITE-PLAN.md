# Test suite review and reorganisation

**Status: done (October 2026).** `Tests/Flower.Tests` went from 2,269 test methods
in 217 flat files to 2,099 in 23 folders, and from 3m54s for the fast run
(`--filter 'Category!=RequiresFfmpeg'`) to 58s for the whole suite, decoder tests
included. The time came from not laying out windows to measure them, and from a
subscription leak the slowest tests were paying for (see `MusicListViewGestureTests`
below).

`Tests/Flower.Server.Tests` kept all 256 tests and was grouped into folders. The
rules that came out of it are in CLAUDE.md under "What gets a test, and where it
goes". This file records what was removed, why, and the cases that were a
judgement call, so a removed test is not mistaken for a gap.

## The bar

- **No assertions on how the UI looks.** That means colours, pixels, font
  weights and sizes, margins, glyph sizes, and where one element sits relative
  to another. Moving things around must never mean editing tests.
- **Navigation and UI behaviour are tested on view models.** A headless window
  is justified only for what lives in the view and nowhere else: pointer
  capture, focus, or a binding's echo. A custom control's own logic goes in a
  class the control delegates to, where a plain test reaches it.
- **No trivial tests.** That means pass-throughs, list wrappers, and "forwards
  the call".
- **No duplicates.** When the same behaviour is asserted at two layers, the
  lower one stays.

## What was removed

**Whole files that measured the UI:**

| File | What it pinned |
|---|---|
| `AlbumTileCaptionTests` | caption font sizes and line heights |
| `AlbumDetailLayoutTests` | pixel samples, art sizes, alignment, wide vs narrow geometry |
| `MobileTabBarLayoutTests` | tab overlap and fit, icon footprint, label sizes, oval width |
| `NowPlayingLayoutTests` | cover position, gaps, slider thumb bounds |
| `BackdropBlurTests` | pixel samples of a blur |
| `TrackRowPlayingTests` | bold font weight |
| `SquareAlbumArtViewTests` | a square measures square |
| `HomeShelfSlideTests` | animation offsets |
| `AlbumGridColumnSizingTests` | a view-level copy of `AlbumGridRowTests` |

`MobileTabBarLayoutTests`' two focus tests survive, as `Views/MobileSearchFocusTests`.

**Single tests of the same kind:**

- the three grid-placement tests in `PlaybackControlsPlacementTests`, which is
  now a view-model test of the same setting;
- `PlaylistDetailLayoutTests`' header geometry (now `PlaylistScreenTests`, its
  view-model tests window-less);
- the accent-wash colour in `NowPlayingMenuTests`;
- row and sheet geometry in `PlaylistRowActionsTests` (two tests),
  `TrackRowArtistTests`, `ConnectionStatusIconTests`, `TrackDownloadButtonTests`,
  `ScrollIndexBarTests`, `MobileScreenFilterTests`, `MobileTabMemoryTests`,
  `MobileSmartPlaylistEditorTests` (two tests), `MobileQueueAndSheetTests`,
  `NewPlaylistEntryTests` and `LibraryWriteWarningTests` (now a view-model
  test);
- the alpha-channel check in `PlatformContextMenuTests`;
- offset and opacity assertions in `SlidingSheetTests` and
  `ScreenStackPanelSwipeTests`. Which way a screen arrives is still tested, on
  the view model, in `MobileNavigationTransitionTests`.

**Trivial tests:**

- list wrappers in `PlaylistTests` and `LibraryTests`;
- pass-throughs in `GaplessAudioManagerTests` and the old `SmallViewModelTests`
  (whose `VolumeControlViewModelTests` was left empty and went);
- whole files: `RoutePickerControlTests`, `VolumeIconTests`,
  `LogSyncContractsTests`, `MiniaudioIosAudioSessionTests`;
- single tests in `TrackTests`, `TagSuggestionSourceTests`, `TextInputTests`,
  `ResourceMonitorTests`, `LocalFileDeletionTests`,
  `MainViewServerSettingsPageTests`, and the JSON-indentation checks.

**Duplicates:**

- two `ITunesPlayCountImporter` tests that `StoreRoundTripTests` repeated (its
  other four moved into `ITunesPlayCountImporterTests`, which they did not
  duplicate after all: the two importers match separately);
- seven `MainViewModelDeviceSidebarTests` that `DeviceSidebarSectionTests` already
  covers one layer down;
- one of the two unpairing tests repeated from `RemoteServerReachabilityTests`.
  The other stays, as proof `UnpairServer` reaches it;
- the mid-sync spinner test in `MainViewModelSyncTriggerTests`;
- `Changing_the_field_keeps_the_operator_that_was_picked` (mobile editor);
- `A_playlist_that_already_depends_on_this_one_is_not_offered`, which
  `SmartPlaylistGraphTests` covers;
- `NavigationBarSettingsViewTests` (`MobileTabsTests` covers the reorder);
- `ArtistRowViewTests`.

**Backward compatibility, removed with the code it tested:**

- `SqliteMigrations` held eleven steps against old schemas. They are one
  `Schema.Baseline`, stamped at version 11, so every database that ran all the
  steps carries on untouched and later steps start at 12.
- A database stamped below 11 throws `ObsoleteSchemaException`. `FlowerDb`
  moves it aside to `.corrupt` and starts fresh, the way it does an unreadable
  file.
- The reconciling pass that adds folded-in columns stays, with its tests. That
  is how new columns arrive, not compatibility.
- `JsonLibraryImport` (`library.json`/`playlists.json` → SQLite) is deleted.
- The legacy-reader tests for `TrustedPeerStore` and numeric smart-playlist
  rules went. Their "code" was a nullable field and the default enum
  converter, so nothing else changed.

`MdnsInstanceNameTests.The_presentation_form_that_used_to_be_used_is_still_not_the_name`
was on that list by mistake and stays. It is a canary for a library workaround,
not a compatibility test.

## The judgement calls, as decided

- **`MusicListViewGestureTests`.**
  - The selection rules moved out of `MusicListView` into
    `Controls/TrackRowSelection`, and the header's click-vs-drag into
    `Controls/ColumnHeaderDrag`. The control delegates to both, and both are
    tested window-less. Arrow-key movement gained a test it never had.
  - The window file keeps only what a window shows: the binding echo, a press
    below the last row, and one header click and one header drag end to end.
  - Those tests were slow because of a leak, not rendering. `MusicListView`,
    `MusicListPanel` and every pooled `TrackRowControl` subscribed to the
    app-wide `ColumnManager` and never unsubscribed. So every list ever built in
    the run, closed windows included, rebuilt its header and rows on each
    column change, and the test constructor's column-order reset fired one
    change per column. Measured: building the window took ~15ms and a mouse
    event, render included, ~0.1ms, while the reset took 250ms on its first
    call and 580ms by its fourth. Tests went 0.4s → 1.1s over a run.
  - The controls now subscribe only while attached to the visual tree, and catch
    up on reattach. `MakeHeaderCell` had also been adding a column handler per
    cell on every header rebuild, and `BuildHeader` a `ContextRequested` handler
    on every rebuild. Both are gone, the first being redundant with
    `ColumnsChanged`. `Views/MusicListViewLifetimeTests` holds it, and fails
    with the leak put back. The window tests now take 15–75ms each.
- **`TrackRowArtistTests`.** The "show the artist on each row?" rule moved from
  `TrackListScreenView` to `TrackRowViewModel.ArtistIsWorthShowing` and is
  tested as a pure function.
- **`MobileTabMemoryTests`.** Which screen a tab comes back on moved to a
  window-less `ViewModels/Mobile/MobileTabHistoryTests`. Scroll restore for an
  album and list virtualisation stay in the window file.
- **`ScrollIndexBarTests`.** These keep one end-to-end jump and the
  sideways-drag guard. `AlphabetIndexTests` covers the letter logic.
- **`SlidingSheetTests` and `ScreenStackPanelSwipeTests`.** These keep the
  state machines: a close that stays visible until it finishes, a reopen
  mid-close, swipe commit and cancel, history integrity, and tab paging.
  "Moves with the finger" and "slides in from the right" went.
- **`RubberBandPullTests`.** These keep whether a pull commits, bounces or is
  left alone. The steadiness-of-stretch tests went.
- **`MobileQueueAndSheetTests`.** Only the Track Info keyboard focus test is left.
- **`ConnectionStatusIconTests`.** These keep `Resolve`'s precedence and the
  busy-hold timer. The visual-tree icon counts went.
- **Kept as they were:**
  - window-level regressions in Avalonia plumbing (`AlbumGridViewRebuildTests`,
    `RecyclingItemsTests`, `ScreenStackPanelEntranceTests`)
  - virtualisation guards
  - `WindowShortcutTests`, `HomeTileGestureTests`
  - the playlist header's pointer-capture tap
  - the virtualising panels' own arithmetic (`MusicListPanelTests`,
    `UniformRowPanelTests`)
  - `MakaretuMdnsBackendTests`, `DecoderVersionTests`, `DurationTextTests`
  - both layers of `EndReached` with repeat (integration and unit)

## Layout

One class per file. `StoreRoundTripTests` became five store files plus
`TestSupport/IsolatedHome`, its HOME redirect. `LibraryTests` became four:
rescan, sync merge, playlists, and change events. `SmallViewModelTests` and
`ExtractedViewModelTests` became one file per class. Namespaces follow folders.
The iOS runner's one exclusion is by full name,
`Flower.Tests.Browser.BrowserFontFilesTests`, so moving that class means moving
the exclusion too. `MusicListViewGestureTests` is no longer excluded: with the
leak fixed, the whole suite runs on the iOS 27 Simulator in 87s.

```
Tests/Flower.Tests/
  AppShell/          composition root, shutdown wait
  Audio/             ring, feeder, output stage, formats, EQ, starvation, native bindings
  Browser/           the WASM head's origin importers, writers, reporters, pairing, fonts
  Controls/          custom-control logic: selection, gestures, sheets, screen stack, panels
  Decoding/          FFmpeg decoder, HTTP streaming, device checks, PCM oracle
  Logging/           log stores, archive, log view model
  MusicLibrary/      Library (rescan, change events, removal), Track, sort/filter, tags
    Art/             loading, writing, collage, batch wire format
    Import/          scanner, encoder profile, Music.app importers and offer
  Network/           discovery, reachability, retry schedule, cleartext policy
    Security/        signing, certificates, pairing, refusals, rate limiting
  Persistence/       stores, schema, reinstall survival, data-directory isolation guard
  Playback/          coordinator, audio manager, queue, auto-advance, play counting
  Playlists/         playlist model, Library's playlists, merge, sync planner
    Smart/           evaluator, fields, graph, refresher, rules JSON, sync, editor
  Sync/              mapping, pulls, pushes, downloads, ingest, role policy
    Scenarios/       the multi-device end-to-end stories
  TestSupport/       fakes, fixtures, harnesses
  ViewModels/        MainViewModel and its collaborators, rows, sidebar, queue page
    Home/            shelves, settings, album progress
    Mobile/          navigation, tabs, filter, menus, sheets, editors
    Settings/        settings by tab
  Views/             what only a laid-out window shows: focus, capture, binding echo

Tests/Flower.Server.Tests/
  Configuration/     data directory, settings overrides, log levels
  Endpoints/         admin, media, sync, discovery, pairing, upload, removal, browser access
  MusicLibrary/      rescan coordinator, write access, identity parity
  Network/           mDNS name claim, public address and reachability
  Security/          request gate, LAN guard, forwarded headers, TLS pinning, auth
  TestSupport/       FlowerServerFixture, GuardedRequest, ReadOnlyFolders
```

Departures from the proposal this replaced:

- `Library/` and `App/` became `MusicLibrary/` and `AppShell/`, because a
  namespace named after a type shadows it in every test file.
- Shared view models went in `ViewModels/` rather than `ViewModels/Desktop/`.
- Surviving window tests got `Views/`.
- `TestSupport/` stayed flat.
