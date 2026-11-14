# Mobile test cleanup

Across four passes, 144 test methods are removed or consolidated and 465 are
relocated from `Flower.Tests.UI` to `Flower.Tests`. The UI project has 609 fewer
test methods. Counts here refer to methods, with a theory counted once; runner
case counts include its individual data rows.

| Pass | Removed/consolidated | Moved to shared | UI reduction |
| --- | ---: | ---: | ---: |
| Initial cleanup, committed as `59fdca0` | 31 | 35 | 66 |
| Broader cleanup and playback separation, committed as `3700083` | 105 | 66 | 171 |
| Remaining model/helper separation | 8 | 331 | 339 |
| Browser, artist icon, Home row and sync-message follow-up | 0 | 33 | 33 |
| **Total** | **144** | **465** | **609** |

The initial cleanup moved these pure checks:

| Area moved to shared unit tests | Methods | Change |
| --- | ---: | --- |
| Album header metadata and artist identity | 6 | Extract `AlbumHeaderBuilder`; retain artist navigation in UI tests |
| Search history and suggestions | 6 | Extract history mutation; call existing suggestion ranking directly |
| Saved tabs and tab editing | 4 | Move parsing tests; extract toggle/reorder list operations |
| Navigation frame classification and scope keys | 4 | Move existing pure tests |
| Album and song filtering | 2 | Call `FilterableAlbumGrid` and `TrackListBuilder.TitleMatches` directly |
| Album grouping and row chunking | 5 | Move existing pure tests, combining row boundary checks |
| Animation clock subscriptions | 8 | Move deterministic clock tests; keep frame/timer integration in UI |
| **Total** | **35** | **15 converted from app-level tests; 20 already pure** |

The initial cleanup removed or consolidated these checks:

| Area removed or consolidated | Methods | Reason |
| --- | ---: | --- |
| Navigation frame tests | 5 | Three tested local stacks/copies rather than production history; two repeated simple predicates |
| Filter lifecycle | 5 | Covered by broader pull/filter/close and leave/Back view flows |
| Queue ordering and shuffle | 3 | Shared `QueuePageTests` exercise the underlying queue; retain mobile activation, updates and playback commands |
| Playlist creation | 2 | Check existence, focus and submission together in the successful creation flow |
| Song order | 2 | One Songs → album → Back flow checks both sorts and restoration |
| Sort menu | 2 | Existing selection/reversal flows also check initial state and year ordering |
| Animation and presentation details | 8 | Drop progress, direction, sequence and intermediate timing checks; remove a card reopen assertion that never checked its claimed opacity regression |
| Smart playlist preview timing | 2 | Drop wall-clock debounce thresholds; keep preview results, editing and saving |
| Album row chunking | 2 | Combine full-row, trailing-row and empty-input checks |
| **Total** | **31** | |

No screenshot comparison or fixed control-size assertions were found in the
reviewed mobile tests. Scroll-index tests now check that the requested album is
visible rather than exactly aligned to a pixel. Scroll-position restoration,
gesture input, focus, real command/binding flows, and virtualization remain
covered because these affect how the app works.

The production extractions keep persistence, availability and navigation in
their existing view models. The new helpers cover only data construction or
list operations; the unit tests do not create the app harness or pump Avalonia.
The iOS suite includes both projects' source trees automatically, so relocation
requires no runner-specific source list changes.

The broader pass removes 50 mobile-related methods and 55 other UI-project
methods. It removes the following lower-priority coverage. These are
intentional scope reductions; several tests cover valid but small behaviors
rather than defects in the tests themselves.

| Area | Methods removed | Coverage retained |
| --- | ---: | --- |
| Mobile tabs, navigation variations and menus | 22 | Tab selection/persistence, album/artist navigation, playlist rename and deletion confirmation |
| Mobile filtering | 5 | Input binding, filtering, closing, leaving a filtered list, and restoring it with Back |
| Mobile playlist creation/editing | 9 | Creation/submission, cancellation, preview/save/edit, rejected saves, and Vibe playback |
| Swipe, sheet, rubber-band and fling variations | 12 | Committed/cancelled gestures, direction rejection, seek-bar capture, and stopping a fling without accidental playback |
| Logging and log viewer preferences/empty states | 20 | Filtering, export, source selection, incremental updates and stale/failed fetch handling |
| Row presentation and icon timing | 12 | Availability changes, selection restoration, notification wiring and spinner disposal |
| Column customization and Home preferences | 20 | Reordering with hidden columns, filtering/hiding/resetting columns, shelf behavior and virtualization |
| Queue forwarding/page details | 5 | Unit-tested queue mutations plus three real queue view/binding/drag tests |
| **Total** | **105** | |

Playback coverage is separated by what it exercises:

| Moved to shared project | Methods | Execution |
| --- | ---: | --- |
| Queue insertion/removal/reordering | 12 | Ordinary Facts with fake audio |
| Auto-advance, repeat, shuffle and failure limits | 14 | Ordinary Facts; existing callback hooks run inline, with no polling or dispatcher pumping |
| Queue persistence/restoration/clearing | 3 | Ordinary Facts using isolated scratch storage |
| Per-track playback options | 15 | Ordinary Facts/Theory with fake audio and inline callbacks |
| Queue rows, duplicate slots, played flags and activation | 6 | Ordinary Facts constructing playback and `QueueViewModel` directly; no app harness |
| Playback pipeline integration | 16 | Real player/coordinator orchestration with fake decoders and sinks; retains the headless dispatcher |
| **Total** | **66** | **50 ordinary unit/shared behavior methods and 16 dispatcher-backed integration methods** |

`PlaybackDispatcherTests` keeps three focused checks for real backend event
marshalling: an end event racing a user-selected song, an unexpected output
stop preserving the queue, and removing a volume adjustment after a stop.
`QueuePageTests` keeps the real list binding and pointer-driven queue drops.
`MobileQueueLineUpTests` retains representative mobile menu command integration.
The retained tests that only invoke commands are described as command tests,
without claiming to simulate a drag or tap.

The broader pass changes test code and documentation only; it introduces no
production behavior changes or new scheduling abstractions.

The third pass moves all 298 remaining ordinary test methods (after two
redundant checks are removed) into the shared project and converts another 33
Avalonia test methods to ordinary Facts/Theories. No ordinary Fact/Theory methods
remain in the UI project. This is an execution distinction: the shared project
also has integration coverage, including scratch-file/tag reading, persistence,
service registration and real timer behavior. Moving an already ordinary test
changes its classification, without promising a runtime improvement.

| Third-pass area moved to shared | Methods |
| --- | ---: |
| Home shelf rules, album progress and resume commands | 41 |
| Selection, keyboard and gesture state machines | 50 |
| Menu construction, playlist management and drop placement | 33 |
| Track metadata, row/tile reuse, current-track state and art identity | 63 |
| Settings, equalizer and column models | 53 |
| Updates, renaming and crash-report notice | 19 |
| Device/sidebar state, history, logging, iTunes offer decisions, service registration and subscriptions | 72 |
| **Total** | **331** |

The 33 actual conversions are AI settings (16), explicit Home rebuilds (4),
album-progress/playback behavior (7), current-track metadata and repeat/shuffle
notifications (5), and server emoji forwarding (1). Playback tests use the
existing inline callback hooks and dispose their collaborators. Timer delivery,
position-event marshalling, asynchronous art delivery and the Home shelf hold
remain in dedicated dispatcher fixtures. Mixed files get distinct UI class names
so the combined iOS assembly includes every method exactly once.

The third pass also removes seven methods and consolidates one:

- Drop the equalizer band-count check; first-run defaults already checks it.
- Drop the crash notice existence check; both response flows require it.
- Drop the app-harness test that only toggles playback-bar placement in settings.
- Drop the two-dialog and non-dialog tooltip variations; retain the main modal
  open/close regression.
- Drop mobile Track Info's play-count/date and server-name display checks;
  retain batch editing and saving while stepping through songs.
- Fold the screen-revisit population check into the entrance/population flow,
  reusing its window and library setup.

Validation after the third pass: 2,553 shared cases and 435 UI cases pass, with
zero failures or skips. The UI runner count falls from 830 to 435; theory data
rows explain why this differs from the 339-method reduction. Total cases fall
by eight, from 2,996 to 2,988. The combined iOS execution assembly is also built
to check source inclusion and class-name collisions; this does not execute tests
on an iOS runtime.

The fourth pass converts another 33 Avalonia test methods into ordinary shared
tests, preserving their behavior coverage:

| Area | Methods | Change |
| --- | ---: | --- |
| Artist icon metadata | 4 | Construct `ArtistIcons` directly; no image loading or dispatcher |
| Library browser model | 21 | Keep the existing fake browse host; await rebuilds without a UI session |
| Recently Played rows | 2 | Construct Home directly with a fixed clock for row reuse and layout data |
| Forced-sync result messages | 6 | Extract `ForceSyncResultMessage`; test success and failure wording directly |
| **Total** | **33** | |

The browser's filtered-grid regression now checks both the immediate publication
and the scheduled rebuild. The sync wording checks also cover explicit
unreachable and unusable-catalog failures. The UI suite keeps discovery,
initiator selection, trust confirmation and message forwarding, with spinner
cleanup checked after both successful and failed syncs. Its discovered-server
fixture now stays alive until the caller finishes, and is owned by the test
fixture so failed setup assertions still dispose its timers and subscriptions.

Only result formatting moves in production; the sync decisions and displayed
messages stay the same. The combined iOS assembly picks up the relocated sources
without changes to its source lists.

Validation after the fourth pass: 2,588 shared cases and 403 UI cases pass, with
zero failures or skips. The UI runner has 32 fewer cases; the two extra sync
failure cases and the additional spinner-failure row bring the combined count
to 2,991, three more than before this pass. The combined iOS execution assembly
also builds successfully; this checks source inclusion, not iOS runtime behavior.
