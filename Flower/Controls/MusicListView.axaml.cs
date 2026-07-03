using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Models;
using Flower.ViewModels;

using FlowerTrack = Flower.Models.Track;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Controls;

public partial class MusicListView : UserControl
{
    private readonly ColumnManager _columnManager;
    private readonly MusicListPanel _panel;

    private IReadOnlyList<TrackRowViewModel> _items = Array.Empty<TrackRowViewModel>();

    // ── Sort state (set by MainView/MainViewModel via SortColumn / SortAscending) ─
    public string  SortColumn    { get; set; } = "TrackNumber";
    public bool    SortAscending { get; set; } = true;

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<TrackRowViewModel>? RowActivated;     // double-tap / Enter
    public event EventHandler<TrackRowViewModel>? RowContextMenu;    // right-click on row
    public event EventHandler?                    HeaderContextMenu; // right-click on header

    // ── Selection ─────────────────────────────────────────────────────────────
    private TrackRowViewModel? _selectedRow;
    public TrackRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_selectedRow == value)
                return;
            if (_selectedRow != null)
                SetRowSelected(_selectedRow, false);
            _selectedRow = value;
            if (_selectedRow != null)
                SetRowSelected(_selectedRow, true);
            var track = _selectedRow?.Track;
            SetAndRaise(SelectedTrackProperty, ref _selectedTrack, track);
        }
    }

    private FlowerTrack? _selectedTrack;
    public static readonly DirectProperty<MusicListView, FlowerTrack?> SelectedTrackProperty =
        AvaloniaProperty.RegisterDirect<MusicListView, FlowerTrack?>(
            nameof(SelectedTrack),
            o => o._selectedTrack,
            (o, v) => o.SetSelectedTrack(v),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public FlowerTrack? SelectedTrack
    {
        get => _selectedTrack;
        set => SetAndRaise(SelectedTrackProperty, ref _selectedTrack, value);
    }

    private void SetSelectedTrack(FlowerTrack? track)
    {
        _selectedTrack = track;
        // Sync row selection
        var row = _items.FirstOrDefault(r => r.Track.Path == track?.Path);
        if (row != _selectedRow)
        {
            if (_selectedRow != null)
                SetRowSelected(_selectedRow, false);
            _selectedRow = row;
            if (_selectedRow != null)
                SetRowSelected(_selectedRow, true);
        }
    }

    private static void SetRowSelected(TrackRowViewModel row, bool selected)
        => row.IsSelected = selected;

    // ── Sort command callback ─────────────────────────────────────────────────
    public event EventHandler<string>? SortRequested; // string = column id

    // ── Drag-to-reorder (enabled by the host only while a playlist is shown) ────
    public bool AllowReorder { get; set; }
    public event EventHandler<(FlowerTrack dragged, FlowerTrack? insertBefore)>? RowReordered;

    private readonly Border _dropIndicator = new()
    {
        Height              = 2,
        Background          = Brushes.DodgerBlue,
        IsVisible           = false,
        IsHitTestVisible    = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment   = VerticalAlignment.Top,
    };

    private TrackRowViewModel? _draggedRow;
    private Point _dragStartPoint;
    private bool  _isDragging;
    private const double DragThreshold = 4.0;

    // ── Column header drag-to-reorder ───────────────────────────────────────────
    private readonly Border _columnDropIndicator = new()
    {
        Width               = 2,
        Background          = Brushes.DodgerBlue,
        IsVisible           = false,
        IsHitTestVisible    = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment   = VerticalAlignment.Stretch,
    };

    private MusicColumnDefinition? _draggedColumn;
    private double _columnDragStartX;
    private bool   _isColumnDragging;

    // ── Drag-onto-playlist (enabled by the host everywhere except a playlist's own
    // view, where AllowReorder already owns the drag gesture for reordering within
    // it) - see MainView.axaml.cs's OnTrackDragMoved/OnTrackDragEnded for the
    // receiving end.
    //
    // Deliberately NOT built on Avalonia's native DragDrop.DoDragDrop: on macOS
    // that starts a real NSDraggingSession, which is a system-level, cross-
    // application drag. A drop that misses our own target doesn't just fail
    // silently - if the pointer strays outside our window at all (easy to do
    // dragging toward a sidebar at the window's edge), the OS can hand the
    // dangling drag session to whatever's underneath. Confirmed in practice: a
    // missed drop over the sidebar got picked up by Music.app, which created its
    // own "Untitled Playlist" from it - a completely different app's playlist,
    // not a bug in this app's own data at all, but exactly as confusing as one.
    // Pointer capture (same mechanism the reorder-within-playlist gesture above
    // already uses) never leaves Avalonia's own input pipeline, so this can't
    // leak into another application no matter where the pointer ends up.
    private TrackRowViewModel? _crossDragRow;
    private Point _crossDragStartPoint;
    private bool  _isCrossDragging;

    public event EventHandler<(FlowerTrack track, Point position)>? TrackDragMoved;
    public event EventHandler<(FlowerTrack track, Point position)>? TrackDragEnded;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MusicListView()
    {
        InitializeComponent();
        _columnManager = Ioc.Default.GetService<ColumnManager>()!;
        _panel         = new MusicListPanel
        {
            ClipToBounds      = false,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var contentHost = new Grid();
        contentHost.Children.Add(_panel);
        contentHost.Children.Add(_dropIndicator);
        Scroller.Content = contentHost;

        _columnManager.ColumnsChanged += (_, _) => BuildHeader();

        Scroller.ScrollChanged += (_, _) =>
            _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height);

        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height);
        };

        // Handle pointer / keyboard on the panel
        _panel.PointerPressed     += Panel_PointerPressed;
        _panel.PointerMoved       += Panel_PointerMoved;
        _panel.PointerReleased    += Panel_PointerReleased;
        _panel.PointerCaptureLost += (_, _) => EndDrag();
        _panel.DoubleTapped       += Panel_DoubleTapped;
        _panel.ContextRequested   += Panel_ContextRequested;

        // Column header drag-to-reorder: capture/move/release are wired once,
        // here, on HeaderBorder - a stable element that outlives any single
        // BuildHeader() call. The per-cell PointerPressed handlers (added fresh
        // each BuildHeader(), see MakeHeaderCell) only set _draggedColumn and
        // capture the pointer to HeaderBorder; capturing the individual header
        // cell instead would break as soon as anything rebuilt the header mid-
        // drag (e.g. a column width binding update), silently dropping capture
        // and collapsing the gesture into a plain click.
        HeaderBorder.PointerMoved       += HeaderBorder_PointerMoved;
        HeaderBorder.PointerReleased    += HeaderBorder_PointerReleased;
        HeaderBorder.PointerCaptureLost += (_, _) => EndColumnDrag();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        BuildHeader();
    }

    // ── Scroll position ───────────────────────────────────────────────────────

    public double GetScrollOffsetY() => Scroller.Offset.Y;

    public void SetScrollOffsetY(double y)
    {
        // Force a layout pass first so the ScrollViewer's extent reflects the
        // items just assigned via SetItems, otherwise the offset gets clamped
        // against the stale (pre-update) extent.
        Scroller.UpdateLayout();
        Scroller.Offset = new Vector(0, y);
    }

    // Selects the row for `track` and centers it in the viewport (used by "go to
    // currently playing track"). Returns false if the track isn't in the current items.
    public bool ScrollToTrack(FlowerTrack track)
    {
        int index = -1;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Track.Path == track.Path) { index = i; break; }
        if (index < 0)
            return false;

        SelectedRow = _items[index];
        Focus();

        Scroller.UpdateLayout();
        double rowTop     = index * TrackRowViewModel.RowHeight;
        double target     = rowTop - (Scroller.Viewport.Height - TrackRowViewModel.RowHeight) / 2;
        double maxOffset  = Math.Max(0, _items.Count * TrackRowViewModel.RowHeight - Scroller.Viewport.Height);
        Scroller.Offset   = new Vector(0, Math.Clamp(target, 0, maxOffset));
        return true;
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    public void SetItems(IReadOnlyList<TrackRowViewModel> items)
    {
        _items = items;
        _panel.SetItems(items);
        // Re-apply selection (path-based match)
        if (_selectedTrack != null)
        {
            var match = items.FirstOrDefault(r => r.Track.Path == _selectedTrack.Path);
            if (match != null) { _selectedRow = match; }
        }
    }

    // ── Header bar ────────────────────────────────────────────────────────────

    private StackPanel? _headerPanel;
    private readonly Grid _headerHost = new();

    private void BuildHeader()
    {
        _headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var separatorBrush = GetSeparatorBrush();

        // Spacer for the art column (matches ColumnDefinitions="80,*" in TrackRowControl)
        _headerPanel.Children.Add(new Border { Width = TrackRowViewModel.ArtColumnWidth });

        foreach (var col in _columnManager.VisibleColumns)
            _headerPanel.Children.Add(MakeHeaderCell(col, separatorBrush));

        // _headerHost is a single, stable Grid reused across every BuildHeader()
        // call (which can happen several times in a row - e.g. Reorder() changes
        // every column's Order, each change triggering its own rebuild). Clearing
        // and re-adding through the SAME Grid properly detaches _columnDropIndicator
        // (a single reused Border) from its previous parent; creating a brand new
        // host Grid each time and adding the still-parented indicator to it threw
        // "already has a visual parent".
        _headerHost.Children.Clear();
        _headerHost.Children.Add(_headerPanel);
        _headerHost.Children.Add(_columnDropIndicator);

        HeaderBorder.Child = _headerHost;
        HeaderBorder.ContextRequested += (_, e) => { HeaderContextMenu?.Invoke(this, EventArgs.Empty); e.Handled = true; };
    }

    // Gap index (0..VisibleColumns.Count) that `headerX` falls into, measured
    // against the header as it's actually rendered right now - which, mid-drag,
    // still shows the dragged column occupying its original slot (only the
    // drop-indicator overlay moves; the cells themselves don't rearrange until
    // drop). Must NOT exclude the dragged column from the width sum: doing so
    // desyncs this from the real on-screen positions by exactly that column's
    // width, which showed up as the indicator sitting inside a cell instead of
    // at its edge.
    private int FullGapIndexAt(double headerX)
    {
        double cursor = TrackRowViewModel.ArtColumnWidth;
        var cols = _columnManager.VisibleColumns.ToList();
        for (int i = 0; i < cols.Count; i++)
        {
            if (headerX < cursor + cols[i].Width / 2)
                return i;
            cursor += cols[i].Width;
        }
        return cols.Count;
    }

    // x-offset, in that same real-layout coordinate space, where a given gap index begins.
    private double FullGapX(int gapIndex)
    {
        double cursor = TrackRowViewModel.ArtColumnWidth;
        var cols = _columnManager.VisibleColumns.ToList();
        for (int i = 0; i < gapIndex && i < cols.Count; i++)
            cursor += cols[i].Width;
        return cursor;
    }

    // ColumnManager.Reorder's newVisibleIndex is defined relative to the
    // sequence with the dragged column already removed (see its own doc
    // comment) - convert a gap index from the full (dragged-column-included)
    // sequence into that convention.
    private int ToExcludingIndex(int fullGapIndex, MusicColumnDefinition excluding)
    {
        var all = _columnManager.VisibleColumns.ToList();
        int draggedIndex = all.IndexOf(excluding);
        return fullGapIndex > draggedIndex ? fullGapIndex - 1 : fullGapIndex;
    }

    private void HeaderBorder_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedColumn == null)
            return;
        double x = e.GetPosition(_headerPanel).X;
        if (!_isColumnDragging)
        {
            if (Math.Abs(x - _columnDragStartX) < DragThreshold)
                return;
            _isColumnDragging = true;
            _columnDropIndicator.IsVisible = true;
        }
        int gapIndex = FullGapIndexAt(x);
        _columnDropIndicator.Margin = new Thickness(FullGapX(gapIndex), 0, 0, 0);
    }

    private void HeaderBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedColumn == null)
            return;

        // Snapshot everything needed before releasing capture below:
        // Capture(null) synchronously fires PointerCaptureLost on HeaderBorder,
        // which runs EndColumnDrag() reentrantly and nulls _draggedColumn /
        // resets _isColumnDragging - reading them only after Capture(null) NRE'd
        // on a now-null column and silently always took the "not dragging"
        // branch (the very bug this snapshot avoids).
        var    column      = _draggedColumn;
        bool   wasDragging = _isColumnDragging;
        int    dropIndex   = ToExcludingIndex(FullGapIndexAt(e.GetPosition(_headerPanel).X), column);

        e.Pointer.Capture(null);
        EndColumnDrag(); // idempotent - PointerCaptureLost above likely already ran this

        if (wasDragging)
        {
            // Deferred: Reorder() changes every column's Order, and each change
            // synchronously rebuilds the header (Columns.PropertyChanged ->
            // ColumnManager.ColumnsChanged -> BuildHeader(), replacing
            // HeaderBorder.Child) - doing that reentrantly, still inside the
            // PointerReleased dispatch for the very element being replaced,
            // crashed Avalonia's routed-event dispatch. Posting it lets this
            // handler - and the event route calling it - finish first.
            Dispatcher.UIThread.Post(() => _columnManager.Reorder(column, dropIndex));
        }
        else
            SortRequested?.Invoke(this, column.Id);

        e.Handled = true;
    }

    private void EndColumnDrag()
    {
        _draggedColumn = null;
        _isColumnDragging = false;
        _columnDropIndicator.IsVisible = false;
    }

    private static IBrush GetSeparatorBrush()
    {
        if (Application.Current?.TryFindResource("SystemControlForegroundBaseMediumLowBrush", out var res) == true &&
            res is IBrush brush)
            return brush;
        return Brushes.Gray;
    }

    private Control MakeHeaderCell(MusicColumnDefinition col, IBrush separatorBrush)
    {
        // Sort arrow
        var arrow = new TextBlock
        {
            Text              = SortAscending ? "↑" : "↓",
            IsVisible         = col.Id == SortColumn,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 10,
            Margin            = new Thickness(2, 0, 4, 0),
            Opacity           = 0.7,
        };
        _sortArrows[col.Id] = arrow;

        var label = new TextBlock
        {
            Text              = col.Header,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 11,
            FontWeight        = FontWeight.SemiBold,
            Opacity           = 0.7,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        var labelArea = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
        };
        labelArea.Children.Add(label);
        labelArea.Children.Add(arrow);

        // Resize handle on the right edge
        var handle = new Border
        {
            Width             = 5,
            Background        = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor            = new Cursor(StandardCursorType.SizeWestEast),
        };
        SetupResizeHandle(handle, col);

        var separator = new Border
        {
            Width               = 1,
            Background          = separatorBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible    = false,
        };

        var outer = new Grid
        {
            // Transparent (not null) background so the whole cell - not just the
            // text glyphs - is hit-test visible for the sort click below.
            Background = Brushes.Transparent,
        };
        outer.Bind(WidthProperty, new Avalonia.Data.Binding(nameof(col.Width)) { Source = col });
        outer.Children.Add(labelArea);
        outer.Children.Add(separator);
        outer.Children.Add(handle);

        // Press-and-drag a header cell horizontally to reorder columns; a plain
        // click (no drag past DragThreshold) sorts by it instead - same
        // click-vs-drag split as the track rows use for reordering a playlist.
        // The capture target is HeaderBorder (see HeaderBorder_PointerMoved/
        // Released below), not this cell - see the comment where those are wired
        // up in the constructor for why.
        outer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(outer).Properties.IsLeftButtonPressed)
                return;
            _draggedColumn      = col;
            _columnDragStartX   = e.GetPosition(_headerPanel).X;
            _isColumnDragging   = false;
            e.Pointer.Capture(HeaderBorder);
            e.Handled = true;
        };

        col.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicColumnDefinition.IsVisible))
                BuildHeader(); // rebuild when visibility changes
        };

        return outer;
    }

    private readonly Dictionary<string, TextBlock> _sortArrows = new();

    public void UpdateSortIndicators(string sortColumn, bool ascending)
    {
        SortColumn    = sortColumn;
        SortAscending = ascending;
        foreach (var (id, arrow) in _sortArrows)
        {
            arrow.IsVisible = id == sortColumn;
            arrow.Text      = ascending ? "↑" : "↓";
        }
    }

    private static void SetupResizeHandle(Border handle, MusicColumnDefinition col)
    {
        double startX    = 0;
        double startWidth = 0;
        bool   dragging  = false;

        handle.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                return;
            dragging   = true;
            startX     = e.GetPosition(null).X; // screen coords — stable during resize
            startWidth = col.Width;
            e.Pointer.Capture(s as IInputElement);
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;
            double delta = e.GetPosition(null).X - startX;
            col.Width = Math.Max(col.MinWidth, startWidth + delta);
        };

        handle.PointerReleased += (_, _) => dragging = false;
        handle.PointerCaptureLost += (_, _) => dragging = false;
    }

    // ── Pointer events ────────────────────────────────────────────────────────

    private TrackRowViewModel? HitTestRow(Point panelPoint)
    {
        // panelPoint is already in the panel's local coordinate space (scroll included)
        int index = (int)Math.Floor(panelPoint.Y / TrackRowViewModel.RowHeight);
        if (index < 0 || index >= _items.Count)
            return null;
        return _items[index];
    }

    private void Panel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt  = e.GetPosition(_panel);
        var row = HitTestRow(pt);
        if (row == null)
            return;
        SelectedRow = row;
        Focus();

        if (AllowReorder && e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed)
        {
            _draggedRow     = row;
            _dragStartPoint = pt;
            e.Pointer.Capture(_panel);
        }
        else if (e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed)
        {
            // Not in a playlist view, so this press could become a drag-onto-a-
            // sidebar-playlist gesture instead - see Panel_PointerMoved's threshold
            // check below. Captured immediately (unlike the old DragDrop-based
            // version) so a normal click/double-tap still works below threshold,
            // and once the threshold is crossed this control keeps receiving
            // move/release regardless of where the pointer physically ends up.
            _crossDragRow        = row;
            _crossDragStartPoint = pt;
            e.Pointer.Capture(_panel);
        }
    }

    private void Panel_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_crossDragRow != null)
        {
            var pt = e.GetPosition(_panel);
            if (!_isCrossDragging)
            {
                var dx = pt.X - _crossDragStartPoint.X;
                var dy = pt.Y - _crossDragStartPoint.Y;
                if (dx * dx + dy * dy < DragThreshold * DragThreshold)
                    return;
                _isCrossDragging = true;
            }
            TrackDragMoved?.Invoke(this, (_crossDragRow.Track, e.GetPosition(this)));
            return;
        }

        if (_draggedRow == null)
            return;
        var movedPt = e.GetPosition(_panel);

        if (!_isDragging)
        {
            if (Math.Abs(movedPt.Y - _dragStartPoint.Y) < DragThreshold)
                return;
            _isDragging = true;
            _dropIndicator.IsVisible = true;
        }

        int index = InsertionIndexAt(movedPt.Y);
        _dropIndicator.Margin = new Thickness(0, Math.Max(0, index * TrackRowViewModel.RowHeight - 1), 0, 0);
    }

    private void Panel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isCrossDragging && _crossDragRow != null)
            TrackDragEnded?.Invoke(this, (_crossDragRow.Track, e.GetPosition(this)));

        if (_isDragging && _draggedRow != null)
        {
            var pt = e.GetPosition(_panel);
            int index = InsertionIndexAt(pt.Y);
            var insertBefore = index < _items.Count ? _items[index] : null;
            if (insertBefore != _draggedRow)
                RowReordered?.Invoke(this, (_draggedRow.Track, insertBefore?.Track));
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void EndDrag()
    {
        _draggedRow = null;
        _isDragging = false;
        _dropIndicator.IsVisible = false;
        _crossDragRow = null;
        _isCrossDragging = false;
    }

    private int InsertionIndexAt(double panelY)
        => Math.Clamp((int)Math.Round(panelY / TrackRowViewModel.RowHeight), 0, _items.Count);

    private void Panel_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var pt  = e.GetPosition(_panel);
        var row = HitTestRow(pt);
        if (row == null)
            return;
        SelectedRow = row;
        RowActivated?.Invoke(this, row);
    }

    private void Panel_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(_panel, out var pt))
        {
            var row = HitTestRow(pt);
            if (row != null)
            {
                SelectedRow = row;
                RowContextMenu?.Invoke(this, row);
                e.Handled = true;
            }
        }
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Return when _selectedRow != null:
                RowActivated?.Invoke(this, _selectedRow);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0)
            return;
        int current = _selectedRow == null ? -1 : IndexOf(_items, _selectedRow);
        int next = Math.Clamp(current + delta, 0, _items.Count - 1);
        if (next == current)
            return;
        SelectedRow = _items[next];
        EnsureVisible(next);
    }

    private static int IndexOf(IReadOnlyList<TrackRowViewModel> list, TrackRowViewModel item)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == item)
                return i;
        return -1;
    }

    private void EnsureVisible(int index)
    {
        double top    = index * TrackRowViewModel.RowHeight;
        double bottom = top + TrackRowViewModel.RowHeight;
        double vpTop  = Scroller.Offset.Y;
        double vpBot  = vpTop + Scroller.Viewport.Height;

        if (top < vpTop)
            Scroller.Offset = new Vector(0, top);
        else if (bottom > vpBot)
            Scroller.Offset = new Vector(0, bottom - Scroller.Viewport.Height);
    }
}
