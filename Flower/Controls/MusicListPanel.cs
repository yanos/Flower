using System;
using System.Collections.Generic;
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
/// Album art is always drawn at its full size, which needs <see cref="ColumnManager.AlbumArtHeight"/>
/// of height. With <see cref="ColumnManager.AlwaysShowAlbumArt"/> on, an album run shorter
/// than that is followed by empty "phantom" space, exactly as much as its art needs, so the
/// next song starts below its art; off, that run shows no art instead (TrackRowControl) and
/// nothing is padded. So rows sit at any height, not only at whole multiples of a row's. Phantoms exist only in this panel's layout - the
/// item list, selection and play order never see them - so every index/Y conversion goes
/// through <see cref="RowTop"/>, <see cref="RowIndexAt"/> and <see cref="InsertionIndexAt"/>.
/// </remarks>
public class MusicListPanel : Panel
{
    private readonly ColumnManager _columnManager;

    private IReadOnlyList<TrackRowViewModel> _items = Array.Empty<TrackRowViewModel>();
    private int[] _groupLeader = [];
    // Where each row's top sits; below a padded album it is lower than its
    // index's multiple of the row height by the phantom space above it.
    private double[] _rowTop = [];
    private double _extentHeight;

    private const double RowHeight = TrackRowViewModel.RowHeight;
    private double _scrollOffset;
    private double _viewportHeight = 600; // reasonable default before first layout
    private double _viewportWidth  = 800; // reasonable default before first layout
    private double _scrollOffsetX;

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
    }

    // Subscribed to the app-wide ColumnManager only while attached, so a panel
    // thrown away is not kept alive and re-laid-out by it - see MusicListView's
    // own OnAttachedToVisualTree. A panel that was away catches up on return.
    private bool _missedColumnChanges;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _columnManager.ColumnsChanged += OnColumnsChanged;
        foreach (var col in _columnManager.Columns)
            col.PropertyChanged += OnColumnPropertyChanged;
        if (_missedColumnChanges)
        {
            _missedColumnChanges = false;
            OnColumnsChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _columnManager.ColumnsChanged -= OnColumnsChanged;
        foreach (var col in _columnManager.Columns)
            col.PropertyChanged -= OnColumnPropertyChanged;
        _missedColumnChanges = true;
    }

    // Column reorder/hide-show changes the set of visible columns (and hence
    // total content width); resize changes width directly. Either one can turn
    // a horizontal scrollbar on/off, so both must re-measure.
    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        // Hiding the art column, or no longer always showing it, removes the
        // reason for phantom rows, and a new art size changes how many.
        BuildSlots();
        RefreshActiveSet();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnColumnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicColumnDefinition.Width))
            InvalidateMeasure();
    }

    public double ExtentHeight => _extentHeight;

    // Top of row `index`; `index == Count` is the bottom of the last row, where
    // a drop at the end of the list goes.
    public double RowTop(int index)
    {
        if (_rowTop.Length == 0)
            return 0;
        return index < _rowTop.Length ? _rowTop[index] : _rowTop[^1] + RowHeight;
    }

    // The row under `y`, or -1 for phantom space or outside the list.
    public int RowIndexAt(double y)
    {
        int i = FirstRowAtOrBelow(y, inclusive: false) - 1;
        return i >= 0 && y >= _rowTop[i] && y < _rowTop[i] + RowHeight ? i : -1;
    }

    // Where a row dropped at `y` would be inserted: before the row `y` is in
    // the top half of, or after the one it is in the bottom half of. A drop
    // inside an album's phantom padding therefore inserts after that album,
    // not into it.
    public int InsertionIndexAt(double y) => FirstRowAtOrBelow(y - RowHeight / 2, inclusive: true);

    // The first row whose top is below `y` - or at it, when `inclusive`.
    private int FirstRowAtOrBelow(double y, bool inclusive)
    {
        int lo = 0, hi = _rowTop.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (inclusive ? _rowTop[mid] < y : _rowTop[mid] <= y)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private double ContentWidth
    {
        get
        {
            // Over Columns rather than VisibleColumns, which sorts: the sum
            // does not care about order, and this runs on every measure.
            // The "..." cell after the last column counts too, or the
            // scroller stops short of it and the rows' backgrounds end
            // before it does.
            var width = _columnManager.ArtColumnWidth + TrackRowControl.MenuCellWidth;
            foreach (var column in _columnManager.Columns)
            {
                if (column.IsVisible)
                    width += column.Width;
            }
            return width;
        }
    }

    // A row's own download icon was clicked - see TrackDownloadButton.
    public event EventHandler<TrackRowViewModel>? RowDownloadRequested;
    public event EventHandler<TrackRowViewModel>? RowMenuRequested;

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

    public void SetViewport(double scrollOffset, double viewportHeight, double viewportWidth, double scrollOffsetX = 0)
    {
        bool changed = Math.Abs(_scrollOffset - scrollOffset) > 0.5
                    || Math.Abs(_viewportHeight - viewportHeight) > 0.5
                    || Math.Abs(_viewportWidth - viewportWidth) > 0.5
                    || Math.Abs(_scrollOffsetX - scrollOffsetX) > 0.5;
        _scrollOffset   = scrollOffset;
        _viewportHeight = viewportHeight;
        _viewportWidth  = viewportWidth;
        _scrollOffsetX  = scrollOffsetX;
        if (changed)
        {
            RefreshActiveSet();
            InvalidateArrange();
            InvalidateMeasure(); // content narrower than viewport still needs to fill it
        }
    }

    // ── Active-set management ─────────────────────────────────────────────────

    // Which song each pooled row shows. A row keeps its song for as long as
    // that song stays in the window, and only the rows whose songs left it are
    // handed the ones arriving: scrolling one row along changes one row,
    // where handing them out by position (row 0 the first song in the window,
    // row 1 the next...) gave every row on screen a new song every time - the
    // whole visible list re-read and re-drawn for each row scrolled past.
    private void RefreshActiveSet()
    {
        var (leader, first, last) = ComputeRenderRange();
        var span = Math.Max(0, last - first + 1);
        var needed = span + (leader >= 0 ? 1 : 0);

        // Grow the pool if needed (never shrink - just hide extras).
        while (Children.Count < needed)
        {
            var ctrl = new TrackRowControl(_columnManager);
            // Wired once per pooled control, not per row: the control outlives
            // any one row (see the pool above), and forwards whichever row it
            // currently holds.
            ctrl.DownloadRequested += (_, row) => RowDownloadRequested?.Invoke(this, row);
            ctrl.MenuRequested += (_, row) => RowMenuRequested?.Invoke(this, row);
            ctrl.HighlightChanged += (_, _) => RefreshHighlightShape();
            _activeIndex.Add(-1);
            Children.Add(ctrl);
        }

        // Which songs in the window a row already shows; the rest of the rows
        // are let go of theirs.
        if (_shown.Length < span)
            _shown = new bool[Math.Max(span, _shown.Length * 2)];
        Array.Clear(_shown, 0, span);
        var leaderShown = false;
        for (int slot = 0; slot < Children.Count; slot++)
        {
            var index = _activeIndex[slot];
            if (index >= first && index <= last)
                _shown[index - first] = true;
            else if (index >= 0 && index == leader)
                leaderShown = true;
            else
                _activeIndex[slot] = -1;
        }

        _placing = true;
        var free = 0;
        if (leader >= 0 && !leaderShown)
            Place(leader, ref free);
        for (int i = 0; i < span; i++)
        {
            if (!_shown[i])
                Place(first + i, ref free);
        }

        for (int slot = 0; slot < Children.Count; slot++)
            Children[slot].IsVisible = _activeIndex[slot] >= 0;

        _placing = false;
        RefreshHighlightShape();
    }

    private bool[] _shown = [];

    // Set while RefreshActiveSet hands rows their songs: each one handed a
    // song may go lit or dark, and the shapes are redrawn once at the end
    // rather than for every row.
    private bool _placing;

    // A lit row - picked, playing or pointed at - touching another lit row
    // shares a straight edge with it, so a run of them reads as one shape
    // rounded only at its ends. Here rather than in the row, which sees only
    // its own song: this is what knows its neighbours, and whether an album's
    // phantom padding sits between them. Rerun when a row goes lit or dark
    // (TrackRowControl.HighlightChanged) or the rows on screen change.
    public void RefreshHighlightShape()
    {
        if (_placing)
            return;

        int hovered = -1;
        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (_activeIndex[slot] >= 0 && Children[slot].IsPointerOver)
                hovered = _activeIndex[slot];
        }

        for (int slot = 0; slot < Children.Count; slot++)
        {
            int index = _activeIndex[slot];
            if (index < 0)
                continue;
            ((TrackRowControl)Children[slot]).JoinHighlight(
                above: LitAndTouching(index - 1, hovered),
                below: LitAndTouching(index, hovered));
        }
    }

    // Whether rows `index` and `index + 1` are both lit and sit one directly
    // on top of the other.
    private bool LitAndTouching(int index, int hovered) =>
        index >= 0 && index + 1 < _items.Count
        && IsLit(index, hovered) && IsLit(index + 1, hovered)
        && _rowTop[index + 1] == _rowTop[index] + RowHeight;

    private bool IsLit(int index, int hovered) =>
        index == hovered || _items[index].IsSelected || _items[index].IsCurrentlyPlaying;

    // Hands the song at `index` to the next row without one.
    private void Place(int index, ref int free)
    {
        while (_activeIndex[free] >= 0)
            free++;
        _activeIndex[free] = index;
        Children[free].DataContext = _items[index];
    }

    // The rows the window covers, plus a little overdraw, and the leader of
    // the album the window opens partway into, whose art spans down into view
    // (-1 when there is none). Only that one album's leader can be above the
    // window: any album starting inside it has its leader inside it too.
    private (int Leader, int First, int Last) ComputeRenderRange()
    {
        if (_items.Count == 0)
            return (-1, 0, -1);

        double top    = Math.Max(0, _scrollOffset);
        double bottom = top + _viewportHeight + 3 * RowHeight;
        // The row the viewport opens on - or, where it opens on phantom
        // space, the row above it: that row is off screen, but its group's
        // art hangs down into the gap.
        int first = Math.Max(0, FirstRowAtOrBelow(top, inclusive: false) - 1);

        int last = first - 1;
        while (last + 1 < _items.Count && _rowTop[last + 1] < bottom)
            last++;

        var leader = first <= last ? _groupLeader[first] : -1;
        return (leader, first, last);
    }

    // Lays the rows out, padding every album run shorter than its art
    // (ColumnManager.AlbumArtHeight) with phantom space after its last row
    // where the art always shows - exactly the art's height, not whole rows,
    // so the gap under a cover is the same at every cover size. Runs are read
    // off IsFirstInAlbumGroup, like _groupLeader, rather than AlbumGroupSize.
    private void BuildSlots()
    {
        double minHeight = _columnManager.PadsShortAlbums ? _columnManager.AlbumArtHeight : 0;
        var tops = new double[_items.Count];
        double y = 0;
        double runTop = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].IsFirstInAlbumGroup && i > 0)
            {
                y = Math.Max(y, runTop + minHeight);
                runTop = y;
            }
            tops[i] = y;
            y += RowHeight;
        }
        if (_items.Count > 0)
            y = Math.Max(y, runTop + minHeight);

        _rowTop = tops;
        _extentHeight = y;
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

        // Columns wider than the viewport scroll sideways, but a row's
        // highlight stays inside what is on screen, so its ends are rounded
        // wherever the columns are scrolled to rather than cut off square by
        // the viewport's edge. Here, ahead of measuring the rows, since every
        // input (width, sideways offset, art well) re-measures the panel.
        double artWidth = _columnManager.ArtColumnWidth;
        double hiddenLeft = Math.Max(0, _scrollOffsetX - artWidth);
        double hiddenRight = Math.Max(0, w - (_scrollOffsetX + _viewportWidth));
        if (artWidth + hiddenLeft + hiddenRight > w)
        {
            hiddenLeft = 0;
            hiddenRight = 0;
        }

        foreach (Control child in Children)
        {
            if (!child.IsVisible)
                continue;
            if (child is TrackRowControl row)
                row.HideHighlightOutside(hiddenLeft, hiddenRight);
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
