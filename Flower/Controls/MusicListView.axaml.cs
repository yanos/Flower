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
            if (_selectedRow == value) return;
            if (_selectedRow != null) SetRowSelected(_selectedRow, false);
            _selectedRow = value;
            if (_selectedRow != null) SetRowSelected(_selectedRow, true);
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
            if (_selectedRow != null) SetRowSelected(_selectedRow, false);
            _selectedRow = row;
            if (_selectedRow != null) SetRowSelected(_selectedRow, true);
        }
    }

    private static void SetRowSelected(TrackRowViewModel row, bool selected)
        => row.IsSelected = selected;

    // ── Sort command callback ─────────────────────────────────────────────────
    public event EventHandler<string>? SortRequested; // string = column id

    // ── Constructor ───────────────────────────────────────────────────────────

    public MusicListView()
    {
        InitializeComponent();
        _columnManager = Ioc.Default.GetService<ColumnManager>()!;
        _panel         = new MusicListPanel { ClipToBounds = false };

        Scroller.Content = _panel;

        _columnManager.ColumnsChanged += (_, _) => BuildHeader();

        Scroller.ScrollChanged += (_, _) =>
            _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height);

        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height);
        };

        // Handle pointer / keyboard on the panel
        _panel.PointerPressed  += Panel_PointerPressed;
        _panel.DoubleTapped    += Panel_DoubleTapped;
        _panel.ContextRequested += Panel_ContextRequested;

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        BuildHeader();
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

    private void BuildHeader()
    {
        _headerPanel = new StackPanel { Orientation = Orientation.Horizontal };

        // Spacer for the art column (matches ColumnDefinitions="80,*" in TrackRowControl)
        _headerPanel.Children.Add(new Border { Width = TrackRowViewModel.ArtColumnWidth });

        foreach (var col in _columnManager.VisibleColumns)
            _headerPanel.Children.Add(MakeHeaderCell(col));

        HeaderBorder.Child = _headerPanel;
        HeaderBorder.ContextRequested += (_, e) => { HeaderContextMenu?.Invoke(this, EventArgs.Empty); e.Handled = true; };
    }

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

        var outer = new Grid();
        outer.Bind(WidthProperty, new Avalonia.Data.Binding(nameof(col.Width)) { Source = col });
        outer.Children.Add(labelArea);
        outer.Children.Add(handle);

        // Click on header label = sort
        outer.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(outer).Properties.IsLeftButtonPressed)
            {
                SortRequested?.Invoke(this, col.Id);
                e.Handled = true;
            }
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
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            dragging   = true;
            startX     = e.GetPosition(null).X; // screen coords — stable during resize
            startWidth = col.Width;
            e.Pointer.Capture(s as IInputElement);
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
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
        if (index < 0 || index >= _items.Count) return null;
        return _items[index];
    }

    private void Panel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt  = e.GetPosition(_panel);
        var row = HitTestRow(pt);
        if (row == null) return;
        SelectedRow = row;
        Focus();
    }

    private void Panel_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var pt  = e.GetPosition(_panel);
        var row = HitTestRow(pt);
        if (row == null) return;
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
        if (_items.Count == 0) return;
        int current = _selectedRow == null ? -1 : IndexOf(_items, _selectedRow);
        int next = Math.Clamp(current + delta, 0, _items.Count - 1);
        if (next == current) return;
        SelectedRow = _items[next];
        EnsureVisible(next);
    }

    private static int IndexOf(IReadOnlyList<TrackRowViewModel> list, TrackRowViewModel item)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == item) return i;
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
