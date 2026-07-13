# Desktop Album Grid — Design Record

Record of how desktop's Albums/Recently Added sidebar views became a mobile-style
art-tile grid with inline expand/collapse, and why the implementation went through two
different architectures before landing on the current one. Written after the fact (the
feature is implemented and tested, not proposed) — closest precedent for this kind of
doc is `CROSS-PLATFORM-PLAN.md`'s "what shipped instead" treatment of its own item #3.

## What changed

Albums used to show a plain-text list of album names (a `ListBox`) next to the main
track list; Recently Added showed a flat, `DateAdded`-sorted track list. Both now show
the same big-art tile grid mobile already had (`AlbumGridBuilder`/
`RecentlyAddedAlbumsBuilder`, both pre-existing shared services — only the desktop-side
rendering was new). Clicking a tile expands that album's songs in place, in two columns,
directly below its row, with an animated height transition; clicking it again collapses
it. Only one album is ever expanded at a time, shared across both grids (expanding one
on Recently Added and then switching to Albums shows it still expanded there). Multi-select
(Ctrl/Cmd-click, Shift-range) and drag-to-playlist both work directly on the grid,
restoring the old plain-text list's own drag gesture (see `SubList_PointerPressed` in
`MainView.axaml.cs`, which the new gesture directly mirrors).

## Why two iterations

**Iteration 1** was a genuinely 2D virtualizing `Panel` (`AlbumGridPanel`, since
deleted) — hand-rolled measure/arrange math, tile columns computed from the current
viewport width, an active-tile pool mirroring `MusicListPanel`'s own uniform-row-height
virtualization for the main track list. This worked for a flat, always-uniform-height
grid.

It broke down the moment inline expand/collapse was added. Uniform-row-height math has
no notion of "this one row is taller right now," and a hand-rolled panel gets no
animation support for free — building both variable-height virtualization *and* a smooth
height transition from scratch, for a hand-rolled `Panel`, was real, high-risk custom
work with no way to visually verify it before shipping.

**Iteration 2** (current) replaced the custom panel with a row-chunked `ItemsControl` +
`VirtualizingStackPanel` — the same shape mobile's own Albums/Recently Added grids
already use in `MobileMainView.axaml` (rows of a fixed tile count, `VirtualizingStackPanel`
handling the actual virtualization), generalized from mobile's fixed 2-per-row to a
column count computed from the current width. `VirtualizingStackPanel` is Avalonia's own,
already-proven virtualizer and natively supports variable-height items, so expand/collapse
became a matter of animating one `Border`'s `Height` via a standard `DoubleTransition` —
no custom virtualization or animation code needed at all.

## Architecture

- **`AlbumGridBuilder`/`RecentlyAddedAlbumsBuilder`** (`Flower/Services/`, pre-existing,
  shared with mobile) — group tracks into `AlbumTileViewModel`s, alphabetical vs.
  by-recency.
- **`AlbumGridRowViewModel`** (`Flower/ViewModels/`) — one row's worth of tiles, plus
  `IsExpanded`/`ExpandedTracks` (and computed `Column1Tracks`/`Column2Tracks`, split
  top-to-bottom-then-next-column). Rebuilt whenever the tile list or column count
  changes; `IsExpanded`/`ExpandedTracks` are re-applied after any rebuild by
  `AlbumGridView.ApplyExpansion`, not part of the rebuild itself, so expanding a row
  doesn't require touching every row's `Tiles` list.
- **`AlbumGridRowControl`** (`Flower/Controls/`) — renders one row: a horizontal strip
  of `AlbumTileControl`s, plus a `Border` (the two-column track list) whose `Height`
  animates 0 ↔ a precomputed target via `DoubleTransition` whenever `IsExpanded`
  changes. The target height is computed from track count (`TrackRowHeight = 26`) rather
  than measured from the real template, since animating cleanly to/from Avalonia's "Auto"
  isn't natively supported — see Known gaps below.
- **`AlbumGridView`** (`Flower/Controls/`) — hosts the `ItemsControl`+
  `VirtualizingStackPanel`, owns row-chunking (`RebuildRows`, driven by `ItemsSource`
  changes and `SizeChanged`), and exposes `SelectedNames`/`ExpandedName`/`ExpandedTracks`
  bindable properties that push state onto the underlying `AlbumTileViewModel`/
  `AlbumGridRowViewModel` instances (`ApplySelection`/`ApplyExpansion`) — the same
  "mutable flag on a per-rebuild-fresh instance" pattern `TrackRowViewModel.IsSelected`
  already established for the main track list, not a new one.
- **`MainViewModel.ExpandedAlbumName`/`ExpandedAlbumTracks`/`ToggleAlbumExpandedCommand`**
  — single source of truth for which album (if any) is expanded, shared by both grid
  instances in `MainView.axaml` so expansion state is consistent regardless of which grid
  a tile was clicked in. Independent of `SelectedSubItems` (Ctrl/Shift multi-select for
  drag) — a plain click toggles expansion only; a modified click multi-selects only.
- **Click vs. drag**: resolved on pointer *release*, not press (`MainView.axaml.cs`'s
  `_albumGridPendingActivate`) — toggling expansion immediately on press would hide/resize
  the grid before a drag gesture had a chance to start, since expanding pushes rows down
  under the cursor.

## Known gaps, deliberately deferred

Not blockers — each is independent and fixable without touching the others or this
architecture.

- **Resize re-chunks abruptly, not a smooth reflow.** `AlbumGridView.RebuildRows` throws
  away and rebuilds every row the moment `SizeChanged` reports a width delta > 1px, with
  no debounce — during a live window-drag this could feel like a "snap" rather than a
  fluid reflow. Fix: debounce `RebuildRows` (a short `DispatcherTimer`, matching how
  `MainViewModel` already debounces `ScheduleFilter`/`ScheduleContentSync` elsewhere) so
  it only re-chunks once resizing pauses.
- **Expansion height is a hardcoded estimate**, not derived from the real rendered
  track-row template (`AlbumGridRowControl.TrackRowHeight = 26`). If that template's
  font/padding changes later, this drifts out of sync silently. Fix: measure one real
  track row once (off-screen) to derive the constant, or move it to a named resource both
  the template and the code-behind read from.
- **No keyboard navigation** in the grid (arrow keys between tiles, Enter to
  expand/collapse) — `MusicListView`/the old `SubList` both had keyboard handling the
  grid doesn't yet. Purely additive whenever it's worth doing; doesn't touch the
  row/virtualization architecture.
- **`AlbumTileViewModel` and friends still live under `Flower.ViewModels.Mobile`**, even
  though desktop now depends on them directly (`using Flower.ViewModels.Mobile;` from
  several desktop files). Cosmetic/organizational, not functional — a mechanical
  namespace move (`Flower.ViewModels.Mobile.AlbumTileViewModel` →
  `Flower.ViewModels.AlbumTileViewModel`) whenever it's worth the churn.

**Not a gap** — a confirmed design choice: only one album can be expanded at a time,
globally (not one per grid). Matches mobile, which never shows two things expanded at
once either.

## Verification

Builds clean on net10.0, net9.0, Flower.Desktop, and Flower.iOS; all 141 existing tests
pass. No new automated tests were added — this is UI interaction/animation behavior, not
something the existing xUnit suite covers. Still needs hands-on GUI testing: expand/collapse
animation feel, behavior mid-window-resize, multi-select + drag-to-playlist from both
grids, and the keyboard-navigation gap noted above.
