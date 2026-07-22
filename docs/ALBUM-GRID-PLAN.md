# Desktop Album Grid — Design Record

Record of how desktop's Albums/Recently Added sidebar views became a mobile-style art-tile grid with inline expand/collapse. Written after the fact — the feature is implemented and tested.

## What changed

Albums/Recently Added now show the same big-art tile grid mobile already had (`AlbumGridBuilder`/`RecentlyAddedAlbumsBuilder`, pre-existing shared services — only desktop's rendering was new), replacing a plain-text `ListBox`/flat track list. Clicking a tile expands that album's songs in place (two columns, animated height); only one album is ever expanded at a time, shared across both grids. Multi-select and drag-to-playlist both work on the grid, mirroring the old list's gesture.

## Why two iterations

**Iteration 1**: a hand-rolled 2D virtualizing `Panel` (`AlbumGridPanel`, deleted) with uniform-row-height math. Broke down once expand/collapse needed variable row height and animation — building both from scratch on a custom panel was high-risk with no way to verify before shipping.

**Iteration 2** (current): row-chunked `ItemsControl` + `VirtualizingStackPanel` — the same shape mobile's own grids already use, generalized to a computed column count. `VirtualizingStackPanel` is Avalonia's proven virtualizer and natively supports variable-height items, so expand/collapse became just animating a `Border.Height` via `DoubleTransition`.

## Architecture

- `AlbumGridBuilder`/`RecentlyAddedAlbumsBuilder` (`Flower/Services/`) — group tracks into `AlbumTileViewModel`s.
- `AlbumGridRowViewModel` — one row's tiles plus `IsExpanded`/`ExpandedTracks`; re-applied after rebuild by `AlbumGridView.ApplyExpansion` rather than being part of the rebuild.
- `AlbumGridRowControl` (`Flower/Controls/`) — renders tiles + the animated two-column track border. Expansion height is a hardcoded per-row-height estimate (`TrackRowHeight = 26`), not measured from the real template (Avalonia can't animate cleanly to/from "Auto").
- `AlbumGridView` — hosts the `ItemsControl`+`VirtualizingStackPanel`, owns row-chunking (`RebuildRows`, driven by `ItemsSource`/`SizeChanged`), exposes bindable `SelectedNames`/`ExpandedName`/`ExpandedTracks`.
- `MainViewModel.ExpandedAlbumName`/`ExpandedAlbumTracks`/`ToggleAlbumExpandedCommand` — single source of truth shared by both grid instances, independent of multi-select state.
- Click vs. drag is resolved on pointer release, not press, since expanding on press would move rows under the cursor mid-drag.

## Known gaps (each independent, low-risk, not blockers)

- Resize re-chunks abruptly rather than reflowing smoothly — needs a debounce on `RebuildRows`.
- Expansion height is a hardcoded estimate, not measured from the real template — could drift if the template changes.
- No keyboard navigation in the grid yet.
- `AlbumTileViewModel` and friends still live under `Flower.ViewModels.Mobile` even though desktop depends on them directly — cosmetic namespace cleanup.

Not a gap, a confirmed design choice: only one album expanded at a time, globally — matches mobile.

## Verification

Builds clean on all targets; all existing tests pass (no new automated tests — this is UI/animation behavior). Still needs hands-on GUI testing of expand/collapse feel, resize behavior, and multi-select/drag from both grids.
