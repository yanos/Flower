using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flower.ViewModels;

namespace Flower.Controls;

/// <summary>
/// Virtualized panel for the track list.  Only creates TrackRowControl instances for the
/// visible viewport plus a small overdraw buffer.  Album-group leaders whose art spans into
/// the visible range are always kept in the rendered set.
/// </summary>
/// <remarks>
/// Album art is always drawn at its full size, which needs <see cref="MinAlbumGroupRows"/>
/// rows of height. An album run shorter than that is followed by empty "phantom" row slots
/// so the next song starts below its art. Phantoms exist only in this panel's layout - the
/// item list, selection and play order never see them - so every index/Y conversion goes
/// through <see cref="RowTop"/>, <see cref="RowIndexAt"/> and <see cref="InsertionIndexAt"/>.
/// </remarks>
public class MusicListPanel : Panel
{
    private readonly ColumnManager _columnManager;

    private IReadOnlyList<TrackRowViewModel> _items = Array.Empty<TrackRowViewModel>();
    private int[] _groupLeader = [];
    // Row-height slot each row sits in; a row's slot exceeds its index by the
    // phantom slots padding the album runs above it.
    private int[] _rowSlot = [];
    private int _slotCount;
    private double _scrollOffset;
    private double _viewportHeight = 600; // reasonable default before first layout
    private double _viewportWidth  = 800; // reasonable default before first layout

    // Index of each active child's source row; parallel to Children.
    private readonly List<int> _activeIndex = new();

    // The ColumnManager is passed in by MusicListView (which has already
    // resolved it) rather than service-located here; the Ioc fallback stays
    // only for a panel constructed without one. See ARCHITECTURE-REVIEW.md
    // Tier 2.3 - and it is what lets MusicListPanelTests exercise this
    // without a live container.
    public MusicListPanel(ColumnManager? columnManager = null)
    {
        _columnManager = columnManager ?? Ioc.Default.GetService<ColumnManager>()!;

        // Column reorder/hide-show changes the set of visible columns (and
        // hence total content width); resize changes width directly. Either
        // one can turn a horizontal scrollbar on/off, so both must re-measure.
        _columnManager.ColumnsChanged += (_, _) =>
        {
            // Hiding the art column removes the reason for phantom rows, and
            // showing it brings them back.
            BuildSlots();
            RefreshActiveSet();
            InvalidateMeasure();
            InvalidateArrange();
        };
        foreach (var col in _columnManager.Columns)
            col.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MusicColumnDefinition.Width))
                    InvalidateMeasure();
            };
    }

    // Full-size art is ArtMaxSize plus the art cell's 2px top margin: 78px,
    // which three 28px rows hold and two do not.
    public const int MinAlbumGroupRows = 3;

    public double ExtentHeight => _slotCount * TrackRowViewModel.RowHeight;

    // Top of row `index`; `index == Count` is the bottom of the last row, where
    // a drop at the end of the list goes.
    public double RowTop(int index)
    {
        if (_rowSlot.Length == 0)
            return 0;
        int slot = index < _rowSlot.Length ? _rowSlot[index] : _rowSlot[^1] + 1;
        return slot * TrackRowViewModel.RowHeight;
    }

    // The row under `y`, or -1 for a phantom slot or outside the list.
    public int RowIndexAt(double y)
    {
        int slot = (int)Math.Floor(y / TrackRowViewModel.RowHeight);
        int i = FirstRowAtOrAfterSlot(slot);
        return i < _rowSlot.Length && _rowSlot[i] == slot ? i : -1;
    }

    // Where a row dropped at `y` would be inserted: before the first row whose
    // top is at or below the nearest row boundary. A boundary inside an album's
    // phantom padding therefore inserts after that album, not into it.
    public int InsertionIndexAt(double y)
    {
        int boundary = (int)Math.Round(y / TrackRowViewModel.RowHeight);
        return FirstRowAtOrAfterSlot(boundary);
    }

    private int FirstRowAtOrAfterSlot(int slot)
    {
        int lo = 0, hi = _rowSlot.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_rowSlot[mid] < slot)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private double ContentWidth =>
        _columnManager.ArtColumnWidth + _columnManager.VisibleColumns.Sum(c => c.Width);

    // A row's own download icon was clicked - see TrackDownloadButton.
    public event EventHandler<TrackRowViewModel>? RowDownloadRequested;

    public void SetItems(IReadOnlyList<TrackRowViewModel> items)
    {
        _items = items;
        _groupLeader = BuildGroupLeaderIndex(items);
        BuildSlots();
        // The new list may reuse the same indices as the old one (e.g. switching
        // albums while scrolled near the top), so force every active slot to
        // re-bind its DataContext rather than relying on the index comparison.
        for (int i = 0; i < _activeIndex.Count; i++) _activeIndex[i] = -1;
        RefreshActiveSet();
        InvalidateMeasure();
    }

    public void SetViewport(double scrollOffset, double viewportHeight, double viewportWidth)
    {
        bool changed = Math.Abs(_scrollOffset - scrollOffset) > 0.5
                    || Math.Abs(_viewportHeight - viewportHeight) > 0.5
                    || Math.Abs(_viewportWidth - viewportWidth) > 0.5;
        _scrollOffset   = scrollOffset;
        _viewportHeight = viewportHeight;
        _viewportWidth  = viewportWidth;
        if (changed)
        {
            RefreshActiveSet();
            InvalidateArrange();
            InvalidateMeasure(); // content narrower than viewport still needs to fill it
        }
    }

    // ── Active-set management ─────────────────────────────────────────────────

    private void RefreshActiveSet()
    {
        var indices = ComputeRenderIndices();

        // Grow the Children pool if needed (never shrink — just hide extras).
        // DataContext must be set BEFORE Children.Add so the compiled binding never sees
        // the inherited MainViewModel DataContext during the brief insertion window.
        while (Children.Count < indices.Count)
        {
            int slot = Children.Count;
            var ctrl = new TrackRowControl(_columnManager);
            // Wired once per pooled control, not per row: the control outlives
            // any one row (see the pool above), and forwards whichever row it
            // currently holds.
            ctrl.DownloadRequested += (_, row) => RowDownloadRequested?.Invoke(this, row);
            ctrl.DataContext = _items[indices[slot]];
            _activeIndex.Add(indices[slot]);
            Children.Add(ctrl);
        }

        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (slot < indices.Count)
            {
                int rowIdx = indices[slot];
                if (_activeIndex[slot] != rowIdx)
                {
                    ((TrackRowControl)Children[slot]).DataContext = _items[rowIdx];
                    _activeIndex[slot] = rowIdx;
                }
                Children[slot].IsVisible = true;
            }
            else
            {
                Children[slot].IsVisible = false;
                _activeIndex[slot] = -1;
            }
        }
    }

    private List<int> ComputeRenderIndices()
    {
        if (_items.Count == 0)
            return [];

        int firstSlot = Math.Max(0, (int)Math.Floor(_scrollOffset / TrackRowViewModel.RowHeight));
        int lastSlot  = firstSlot + (int)Math.Ceiling(_viewportHeight / TrackRowViewModel.RowHeight) + 3;
        // Starting one row early when the viewport opens on a phantom slot:
        // that row is off screen, but its group's art hangs down into the gap.
        int first = FirstRowAtOrAfterSlot(firstSlot);
        if (first > 0 && (first == _items.Count || _rowSlot[first] > firstSlot))
            first--;

        var set = new SortedSet<int>();
        for (int i = first; i < _items.Count && _rowSlot[i] < lastSlot; i++)
        {
            set.Add(i);
            // Ensure the album-group leader is always rendered so its art spans down visually
            var leader = _groupLeader[i];
            if (leader >= 0)
                set.Add(leader);
        }
        return [.. set];
    }

    // Lays the rows out in slots, padding every album run shorter than
    // MinAlbumGroupRows with phantom slots after its last row. Runs are read
    // off IsFirstInAlbumGroup, like _groupLeader, rather than AlbumGroupSize.
    private void BuildSlots()
    {
        bool pad = _columnManager.ShowAlbumArt;
        var slots = new int[_items.Count];
        int slot = 0;
        int runLength = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].IsFirstInAlbumGroup && i > 0)
            {
                if (pad && runLength < MinAlbumGroupRows)
                    slot += MinAlbumGroupRows - runLength;
                runLength = 0;
            }
            slots[i] = slot++;
            runLength++;
        }
        if (pad && _items.Count > 0 && runLength < MinAlbumGroupRows)
            slot += MinAlbumGroupRows - runLength;

        _rowSlot = slots;
        _slotCount = slot;
    }

    // Index of the album-group leader for each row, or -1 for a row that is
    // its own leader (nothing extra to render).
    //
    // This used to be a backscan from every visible row up to its leader,
    // making the per-scroll cost O(viewport x group size) rather than
    // O(viewport) - and it runs on every single scroll event. The grouping only
    // changes when the item list does, so compute it once here instead. See
    // docs/ARCHITECTURE-REVIEW.md Tier 1.5.
    private static int[] BuildGroupLeaderIndex(IReadOnlyList<TrackRowViewModel> items)
    {
        var leaders = new int[items.Count];
        int current = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsFirstInAlbumGroup)
            {
                current = i;
                leaders[i] = -1;
            }
            else
            {
                leaders[i] = current;
            }
        }

        return leaders;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // Hosted in a ScrollViewer with HorizontalScrollBarVisibility="Auto", so
        // availableSize.Width arrives as infinity - report our true desired width
        // (the sum of all column widths) so the ScrollViewer knows to show a
        // horizontal scrollbar once that exceeds the viewport, falling back to
        // the viewport's own width so rows/selection highlight still fill it
        // when there are few enough columns to fit.
        double w = Math.Max(ContentWidth, _viewportWidth);

        foreach (Control child in Children)
        {
            if (child.IsVisible)
                child.Measure(new Size(w, TrackRowViewModel.RowHeight));
        }

        return new Size(w, ExtentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (!Children[slot].IsVisible)
                continue;
            double y = RowTop(_activeIndex[slot]);
            Children[slot].Arrange(new Rect(0, y, finalSize.Width, TrackRowViewModel.RowHeight));
        }
        return new Size(finalSize.Width, ExtentHeight);
    }
}
