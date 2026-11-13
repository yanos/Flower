# Mobile test cleanup

The cleanup removes or consolidates 31 test methods and relocates 35 methods
from `Flower.Tests.UI` to `Flower.Tests`. The UI project has 66 fewer test
methods. Counts here refer to methods, with a theory counted once; runner case
counts include its individual data rows.

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
