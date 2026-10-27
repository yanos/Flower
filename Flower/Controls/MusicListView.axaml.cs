using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using FlowerTrack = Flower.Models.Track;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Controls;

public partial class MusicListView : UserControl
{
    private readonly ColumnManager _columnManager;

    // Exposed so MainView's column menu and Column Selector window use this
    // control's instance instead of resolving a second reference to the same
    // singleton.
    public ColumnManager ColumnManager => _columnManager;
    private readonly MusicListPanel _panel;

    private IReadOnlyList<TrackRowViewModel> _items = Array.Empty<TrackRowViewModel>();

    // What the list was last handed - for tests asking which rows a page shows.
    internal IReadOnlyList<TrackRowViewModel> Items => _items;

    // ── Sort state (set by MainView/MainViewModel via SortColumn / SortAscending) ─
    public string  SortColumn    { get; set; } = "TrackNumber";
    public bool    SortAscending { get; set; } = true;

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<TrackRowViewModel>? RowActivated;     // double-tap / Enter
    public event EventHandler<TrackRowViewModel>? RowDownloadRequested; // the row's own download icon
    public event EventHandler<TrackRowViewModel>? RowContextMenu;    // right-click on row
    public event EventHandler?                    HeaderContextMenu; // right-click on header

    // ── Selection ─────────────────────────────────────────────────────────────
    // What a press does to the selection lives in TrackRowSelection; this
    // control turns pointer and keyboard input into calls on it, and keeps the
    // TwoWay SelectedTrack binding in step.
    private readonly TrackRowSelection _selection = new();

    public IReadOnlyList<FlowerTrack> SelectedTracks => _selection.Tracks;

    // The rows behind SelectedTracks, for a caller that needs the per-row state
    // and not just the tracks (the right-click Download item, whose progress
    // shows on the rows themselves).
    public IReadOnlyList<TrackRowViewModel> SelectedRows => _selection.Rows;

    public TrackRowViewModel? SelectedRow
    {
        get => _selection.Current;
        set
        {
            if (_selection.Current == value && _selection.Count <= 1)
                return;
            _selection.SelectOnly(value);
            RaiseSelectedTrackChanged(_selection.Current?.Track);
        }
    }

    // SelectedTrackProperty is TwoWay-bound to MainViewModel.SelectedTrack, which
    // forwards through PlaylistControlViewModel and raises PropertyChanged, which
    // the binding then writes straight back into this control - synchronously,
    // within the same call. Left unguarded, that echo re-enters SetSelectedTrack
    // and (since it can't otherwise tell an echo apart from a genuine external
    // "select this track" request) stomps a multi-selection back down to one row
    // right after a Shift- or Primary-press just built it. This flag lets
    // SetSelectedTrack recognize and ignore its own echo.
    private bool _isRaisingSelectedTrack;

    private void RaiseSelectedTrackChanged(FlowerTrack? track)
    {
        _isRaisingSelectedTrack = true;
        try { SetAndRaise(SelectedTrackProperty, ref _selectedTrack, track); }
        finally { _isRaisingSelectedTrack = false; }
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
        if (_isRaisingSelectedTrack)
            return; // echo of our own change via the TwoWay binding - see RaiseSelectedTrackChanged
        var row = _selection.RowFor(track);
        if (row != _selection.Current)
            _selection.SelectOnly(row);
    }

    // ── Sort command callback ─────────────────────────────────────────────────
    public event EventHandler<string>? SortRequested; // string = column id

    // ── Drag-to-reorder (enabled by the host while a playlist or the queue is shown) ────
    public bool AllowReorder { get; set; }

    // Rows rather than tracks, because the queue can hold one song twice and
    // a track cannot say which copy was dragged. A row whose CanBeDragged is
    // false - the queue's song playing - stays put.
    public event EventHandler<(TrackRowViewModel dragged, TrackRowViewModel? insertBefore)>? RowReordered;

    private readonly Border _dropIndicator = new()
    {
        Height              = 2,
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
        IsVisible           = false,
        IsHitTestVisible    = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment   = VerticalAlignment.Stretch,
    };

    // Click-vs-drag and where the gaps are; this control turns the pointer
    // into calls on it and draws the indicator.
    private readonly ColumnHeaderDrag _headerDrag;

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
    private IReadOnlyList<FlowerTrack>? _crossDragTracks; // snapshotted once, at threshold-crossing

    public event EventHandler<(IReadOnlyList<FlowerTrack> tracks, Point position)>? TrackDragMoved;
    public event EventHandler<(IReadOnlyList<FlowerTrack> tracks, Point position)>? TrackDragEnded;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MusicListView()
    {
        InitializeComponent();
        // The drop indicators are built in field initializers, which cannot
        // bind; their colour is bound here instead, to the resource rather
        // than a copy of it, so it follows the theme like everything else.
        _dropIndicator[!Border.BackgroundProperty] = new DynamicResourceExtension("AppInkBrush");
        _columnDropIndicator[!Border.BackgroundProperty] = new DynamicResourceExtension("AppInkBrush");
        // The one container read left in this layer, and deliberately so: this
        // control is instantiated by XAML (MainView.axaml), which offers no
        // constructor to inject through and no DataContext until well after
        // the panel below has to exist. Everything under it - the panel, every
        // row - is handed the instance rather than resolving its own, and
        // MainView reads it back off ColumnManager here rather than making its
        // own call. See docs/ARCHITECTURE-REVIEW.md Tier 2.3.
        _columnManager = Ioc.Default.GetService<ColumnManager>()!;
        _headerDrag    = new ColumnHeaderDrag(_columnManager);
        _panel         = new MusicListPanel(_columnManager)
        {
            ClipToBounds      = false,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var contentHost = new Grid();
        contentHost.Children.Add(_panel);
        contentHost.Children.Add(_dropIndicator);
        Scroller.Content = contentHost;

        // Subscribed to ColumnManager only while on screen - see
        // OnAttachedToVisualTree.

        // The one scroller in the app with both axes live at once (the columns
        // go wider than the viewport), and so the only one where a two-finger
        // vertical scroll could drift sideways on its own.
        PredominantAxisScroll.SetIsEnabled(Scroller, true);

        Scroller.ScrollChanged += (_, _) => SyncScroll();

        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                SyncScroll();
        };

        // Handle pointer / keyboard on the panel
        _panel.RowDownloadRequested += (_, row) => RowDownloadRequested?.Invoke(this, row);
        // The row's "..." is a right-click by another name: the same selection
        // rule (Panel_ContextRequested), the same menu.
        _panel.RowMenuRequested += (_, row) =>
        {
            if (!row.IsSelected)
                SelectedRow = row;
            RowContextMenu?.Invoke(this, row);
        };
        _panel.PointerPressed     += Panel_PointerPressed;
        _panel.PointerMoved       += Panel_PointerMoved;
        _panel.PointerReleased    += Panel_PointerReleased;
        _panel.PointerCaptureLost += (_, _) => EndDrag();
        _panel.DoubleTapped       += Panel_DoubleTapped;
        _panel.ContextRequested   += Panel_ContextRequested;

        // Column header drag-to-reorder: capture/move/release are wired once,
        // here, on HeaderBorder - a stable element that outlives any single
        // BuildHeader() call. The per-cell PointerPressed handlers (added fresh
        // each BuildHeader(), see MakeHeaderCell) only start _headerDrag and
        // capture the pointer to HeaderBorder; capturing the individual header
        // cell instead would break as soon as anything rebuilt the header mid-
        // drag (e.g. a column width binding update), silently dropping capture
        // and collapsing the gesture into a plain click.
        HeaderBorder.PointerMoved       += HeaderBorder_PointerMoved;
        HeaderBorder.PointerReleased    += HeaderBorder_PointerReleased;
        HeaderBorder.PointerCaptureLost += (_, _) => EndColumnDrag();
        HeaderBorder.ContextRequested   += (_, e) => { HeaderContextMenu?.Invoke(this, EventArgs.Empty); e.Handled = true; };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _headerHost.RenderTransform = _headerScrollTransform;

        BuildHeader();
    }

    // Keeps the (otherwise unscrolled) header bar's horizontal position in sync
    // with Scroller's horizontal offset, and tells the panel how wide the
    // viewport currently is so it knows when content is narrower than that
    // (still fill it) versus wider (report a bigger desired width so Scroller
    // shows a horizontal scrollbar) - see MusicListPanel.MeasureOverride.
    private readonly TranslateTransform _headerScrollTransform = new();

    private void SyncScroll()
    {
        _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Viewport.Width);
        _headerScrollTransform.X = -Scroller.Offset.X;
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
            if (_items[i].Track.Id == track.Id) { index = i; break; }
        if (index < 0)
            return false;

        SelectedRow = _items[index];
        Focus();

        Scroller.UpdateLayout();
        double rowTop     = _panel.RowTop(index);
        double target     = rowTop - (Scroller.Viewport.Height - TrackRowViewModel.RowHeight) / 2;
        double maxOffset  = Math.Max(0, _panel.ExtentHeight - Scroller.Viewport.Height);
        Scroller.Offset   = new Vector(0, Math.Clamp(target, 0, maxOffset));
        return true;
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    public void SetItems(IReadOnlyList<TrackRowViewModel> items)
    {
        _items = items;
        _panel.SetItems(items);

        _selection.SetItems(items, _selectedTrack);
    }

    // ── Header bar ────────────────────────────────────────────────────────────

    private StackPanel? _headerPanel;
    private readonly Grid _headerHost = new();

    private void BuildHeader()
    {
        _headerPanel = new StackPanel { Orientation = Orientation.Horizontal };

        // Spacer for the art well, matching the row grid's own first column
        // (see TrackRowControl.BuildCells) - zero-wide when the art is turned
        // off, so the header does not sit 80px right of its cells.
        _headerPanel.Children.Add(new Border { Width = _columnManager.ArtColumnWidth });

        foreach (var col in _columnManager.VisibleColumns)
            _headerPanel.Children.Add(MakeHeaderCell(col));

        // Over the rows' "..." cell (TrackRowControl.BuildMenuCell).
        _headerPanel.Children.Add(new Border { Width = TrackRowControl.MenuCellWidth });

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
    }

    // ColumnManager is one instance for the whole app and outlives any list
    // showing it, so a list that subscribed for good was kept alive by it, and
    // went on rebuilding a header nobody could see on every column change -
    // one more of them each time a list was made and thrown away. Subscribed
    // only while attached instead, and caught up on return, since a change
    // made while away was not heard.
    private bool _missedColumnChanges;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _columnManager.ColumnsChanged += OnColumnsChanged;
        if (_missedColumnChanges)
        {
            _missedColumnChanges = false;
            BuildHeader();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _columnManager.ColumnsChanged -= OnColumnsChanged;
        _missedColumnChanges = true;
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildHeader();

    private void HeaderBorder_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_headerDrag.Move(e.GetPosition(_headerPanel).X) is not { } indicatorX)
            return;
        _columnDropIndicator.IsVisible = true;
        _columnDropIndicator.Margin = new Thickness(indicatorX, 0, 0, 0);
    }

    private void HeaderBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Decided before the capture is released: Capture(null) synchronously
        // fires PointerCaptureLost, which ends the gesture reentrantly, and
        // reading it only after that always took the "not dragging" branch.
        var gesture = _headerDrag.Release(e.GetPosition(_headerPanel).X);
        if (gesture == null)
            return;

        e.Pointer.Capture(null);
        EndColumnDrag(); // idempotent - PointerCaptureLost above likely already ran this

        if (gesture is ColumnHeaderGesture.Reorder reorder)
        {
            // Deferred: Reorder() changes every column's Order, and each change
            // synchronously rebuilds the header (ColumnManager.ColumnsChanged ->
            // BuildHeader(), replacing HeaderBorder.Child) - doing that
            // reentrantly, still inside the PointerReleased dispatch for the
            // very element being replaced, crashed Avalonia's routed-event
            // dispatch. Posting it lets this handler - and the event route
            // calling it - finish first.
            Dispatcher.UIThread.Post(() => _columnManager.Reorder(reorder.Column, reorder.NewVisibleIndex));
        }
        else if (gesture is ColumnHeaderGesture.Sort sort)
            SortRequested?.Invoke(this, sort.ColumnId);

        e.Handled = true;
    }

    private void EndColumnDrag()
    {
        _headerDrag.End();
        _columnDropIndicator.IsVisible = false;
    }

    // MusicListView.axaml sets x:CompileBindings="False" - its columns are
    // built and rebound dynamically from ColumnManager's runtime-configurable
    // definitions, so a compile-time binding path isn't available here. The
    // path used (col.Width) is a real property on MusicColumnDefinition, kept
    // alive by direct references elsewhere, so trimming it away isn't a risk.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Binding path is a real property on MusicColumnDefinition, kept alive by direct references elsewhere in the codebase.")]
    private Control MakeHeaderCell(MusicColumnDefinition col)
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
            FontSize          = 12,
            FontWeight        = FontWeight.Medium,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AppTextSecondaryBrush");

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

        // No rule between header cells: the band is one shape, as the rows
        // under it are; the resize handle at each cell's edge still shows its
        // cursor.
        var outer = new Grid
        {
            // Transparent (not null) background so the whole cell - not just the
            // text glyphs - is hit-test visible for the sort click below.
            Background = Brushes.Transparent,
        };
        outer.Bind(WidthProperty, new Avalonia.Data.Binding(nameof(col.Width)) { Source = col });
        outer.Children.Add(labelArea);
        outer.Children.Add(handle);

        // Press-and-drag a header cell horizontally to reorder columns; a plain
        // click (no drag past ColumnHeaderDrag.DragThreshold) sorts by it instead - same
        // click-vs-drag split as the track rows use for reordering a playlist.
        // The capture target is HeaderBorder (see HeaderBorder_PointerMoved/
        // Released below), not this cell - see the comment where those are wired
        // up in the constructor for why.
        outer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(outer).Properties.IsLeftButtonPressed)
                return;
            _headerDrag.Press(col, e.GetPosition(_headerPanel).X);
            e.Pointer.Capture(HeaderBorder);
            e.Handled = true;
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
        int index = _panel.RowIndexAt(panelPoint.Y);
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
        Focus();

        bool isLeft = e.GetCurrentPoint(_panel).Properties.IsLeftButtonPressed;

        if (_selection.Press(row,
                extend: isLeft && e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                toggle: isLeft && e.KeyModifiers.HasFlag(PlatformShortcuts.Primary)))
            RaiseSelectedTrackChanged(_selection.Current?.Track);

        if (AllowReorder && isLeft && row.CanBeDragged)
        {
            _draggedRow     = row;
            _dragStartPoint = pt;
            e.Pointer.Capture(_panel);
        }
        else if (isLeft && row.IsSelected)
        {
            // Not in a playlist view, so this press could become a drag-onto-a-
            // sidebar-playlist gesture instead - see Panel_PointerMoved's threshold
            // check below. Captured immediately (unlike the old DragDrop-based
            // version) so a normal click/double-tap still works below threshold,
            // and once the threshold is crossed this control keeps receiving
            // move/release regardless of where the pointer physically ends up.
            // Gated on row.IsSelected so a Ctrl+click that just deselected the
            // last remaining row (leaving an empty selection) can't start a drag.
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
                _crossDragTracks = SelectedTracks; // snapshot once selection is final for this press
            }
            TrackDragMoved?.Invoke(this, (_crossDragTracks!, e.GetPosition(this)));
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
        _dropIndicator.Margin = new Thickness(0, Math.Max(0, _panel.RowTop(index) - 1), 0, 0);
    }

    private void Panel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isCrossDragging && _crossDragTracks != null)
            TrackDragEnded?.Invoke(this, (_crossDragTracks, e.GetPosition(this)));

        if (_isDragging && _draggedRow != null)
        {
            var pt = e.GetPosition(_panel);
            int index = InsertionIndexAt(pt.Y);
            var insertBefore = index < _items.Count ? _items[index] : null;
            if (insertBefore != _draggedRow)
                RowReordered?.Invoke(this, (_draggedRow, insertBefore));
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
        _crossDragTracks = null;
    }

    private int InsertionIndexAt(double panelY)
        => Math.Clamp(_panel.InsertionIndexAt(panelY), 0, _items.Count);

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
                // Only collapse to this row if it isn't already part of the
                // current multi-selection, so right-click can act on the whole
                // selection (see RowContextMenu's consumer in MainView).
                if (!row.IsSelected)
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
                MoveSelection(1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Return when _selection.Current is { } current:
                RowActivated?.Invoke(this, current);
                e.Handled = true;
                break;
        }
    }

    // Shift+Up/Down grows or shrinks the range from the existing anchor (the
    // same primitive as Shift+click) instead of collapsing to one row.
    private void MoveSelection(int delta, bool extend)
    {
        int next = _selection.Move(delta, extend);
        if (next < 0)
            return;
        RaiseSelectedTrackChanged(_selection.Current?.Track);
        EnsureVisible(next);
    }

    private void EnsureVisible(int index)
    {
        double top    = _panel.RowTop(index);
        double bottom = top + TrackRowViewModel.RowHeight;
        double vpTop  = Scroller.Offset.Y;
        double vpBot  = vpTop + Scroller.Viewport.Height;

        if (top < vpTop)
            Scroller.Offset = new Vector(0, top);
        else if (bottom > vpBot)
            Scroller.Offset = new Vector(0, bottom - Scroller.Viewport.Height);
    }
}
