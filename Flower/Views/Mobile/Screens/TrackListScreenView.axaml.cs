using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class TrackListScreenView : UserControl
{
    // Touch drag-to-reorder for the playlist track list: the desktop equivalent
    // (MusicListView) starts dragging immediately anywhere on the row with a small
    // 4px threshold, which fights normal touch scrolling. Here dragging only starts
    // from a dedicated handle icon, with a larger threshold before it visually kicks in.
    private const double DragThreshold = 10.0;
    private TrackRowViewModel? _draggedRow;
    private double _dragStartY;
    private bool _isDragging;

    // The rows/header this instance actually renders - deliberately NOT a
    // direct binding to Main.Rows/AlbumDetailRows/CurrentAlbumHeader on the
    // shared MobileMainViewModel (see the XAML's own comment). While this is
    // the CURRENT screen (ObserveLive), these track the live VM exactly as
    // the old direct bindings did. Once demoted to "one back" by
    // ScreenStackPanel (Freeze), they stop tracking anything and hold
    // whatever MobileNavigationFrame.FrozenRows/FrozenHeader captured at the
    // moment this screen was left - the mechanism that lets a kept-alive
    // instance sit there unchanged, ready to be revealed by a swipe, rather
    // than following whatever the live VM now shows (which, since Main.Rows
    // is a single shared collection, could by then be a completely different
    // album/playlist's tracks - see MobileNavigationFrame's own doc comment).
    public static readonly StyledProperty<IReadOnlyList<TrackRowViewModel>> DisplayRowsProperty =
        AvaloniaProperty.Register<TrackListScreenView, IReadOnlyList<TrackRowViewModel>>(
            nameof(DisplayRows), defaultValue: Array.Empty<TrackRowViewModel>());

    public static readonly StyledProperty<AlbumTileViewModel?> DisplayHeaderProperty =
        AvaloniaProperty.Register<TrackListScreenView, AlbumTileViewModel?>(nameof(DisplayHeader));

    public static readonly StyledProperty<bool> IsAlbumModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsAlbumMode));

    public IReadOnlyList<TrackRowViewModel> DisplayRows
    {
        get => GetValue(DisplayRowsProperty);
        private set => SetValue(DisplayRowsProperty, value);
    }

    public AlbumTileViewModel? DisplayHeader
    {
        get => GetValue(DisplayHeaderProperty);
        private set => SetValue(DisplayHeaderProperty, value);
    }

    public bool IsAlbumMode
    {
        get => GetValue(IsAlbumModeProperty);
        private set => SetValue(IsAlbumModeProperty, value);
    }

    private MobileMainViewModel? _observedVm;

    public TrackListScreenView()
    {
        InitializeComponent();

        // TrackRowTemplate's drag handle can't wire these via XAML event
        // attributes (it's a class-less ResourceDictionary - see the
        // template's own comment), so they're attached here instead, tunnel
        // routed off the ListBox itself and keyed off e.Source - the same
        // technique MobileMainView.axaml.cs already uses for its swipe gesture.
        TrackListBox.AddHandler(PointerPressedEvent, DragHandle_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerMovedEvent, DragHandle_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerReleasedEvent, DragHandle_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerCaptureLostEvent, DragHandle_PointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Called by ScreenStackPanel whenever this instance is the CURRENT
    // screen - subscribes to the shared VM so DisplayRows/DisplayHeader/
    // IsAlbumMode keep following it live, exactly like the old direct
    // bindings did (a rescan/download completing while sitting on this
    // screen still needs to show up immediately, not just at the next
    // navigation change).
    public void ObserveLive(MobileMainViewModel vm)
    {
        if (ReferenceEquals(_observedVm, vm))
        {
            RefreshFromLive();
            return;
        }

        Detach();
        _observedVm = vm;
        vm.PropertyChanged += OnObservedVmChanged;
        vm.Main.PropertyChanged += OnObservedVmChanged;
        RefreshFromLive();
    }

    // Called by ScreenStackPanel when this instance is demoted to "one
    // back" - stops following the live VM and freezes at exactly what the
    // frame captured on the way out. See this class's own DisplayRows doc
    // comment for why.
    public void Freeze(MobileNavigationFrame frame)
    {
        Detach();
        DisplayRows = frame.FrozenRows ?? Array.Empty<TrackRowViewModel>();
        DisplayHeader = frame.FrozenHeader;
        IsAlbumMode = frame.IsAlbumTrackList;
    }

    // ScreenControlFactory calls this when evicting a cached instance from
    // its LRU, so a control nobody references anymore doesn't leak a live
    // subscription to the VM.
    public void Detach()
    {
        if (_observedVm == null)
            return;
        _observedVm.PropertyChanged -= OnObservedVmChanged;
        _observedVm.Main.PropertyChanged -= OnObservedVmChanged;
        _observedVm = null;
    }

    private void OnObservedVmChanged(object? sender, PropertyChangedEventArgs e) => RefreshFromLive();

    private void RefreshFromLive()
    {
        if (_observedVm == null)
            return;
        IsAlbumMode = _observedVm.IsShowingAlbumTrackList;
        DisplayRows = IsAlbumMode ? _observedVm.AlbumDetailRows : _observedVm.Main.Rows;
        DisplayHeader = _observedVm.CurrentAlbumHeader;
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Border { Classes: { } classes } handle || !classes.Contains("dragHandle"))
            return;
        if (handle.DataContext is not TrackRowViewModel row)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedRow = row;
        _dragStartY = e.GetPosition(TrackListBox).Y;
        _isDragging = false;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void DragHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedRow == null)
            return;
        var y = e.GetPosition(TrackListBox).Y;

        if (!_isDragging)
        {
            if (Math.Abs(y - _dragStartY) < DragThreshold)
                return;
            _isDragging = true;
            DropIndicator.IsVisible = true;
        }

        int index = InsertionIndexAt(y);
        DropIndicator.Margin = new Thickness(0, IndicatorOffsetFor(index), 0, 0);
    }

    private void DragHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedRow != null && DataContext is MobileMainViewModel vm)
        {
            int index = InsertionIndexAt(e.GetPosition(TrackListBox).Y);
            var insertBefore = TrackListBox.ContainerFromIndex(index)?.DataContext as TrackRowViewModel;
            if (insertBefore != _draggedRow)
                vm.ReorderCurrentPlaylistTrack(_draggedRow.Track, insertBefore?.Track);
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void DragHandle_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _draggedRow = null;
        _isDragging = false;
        DropIndicator.IsVisible = false;
    }

    // Hit-tests realized row containers directly rather than assuming a fixed row
    // height, since mobile rows (unlike desktop's uniform MusicListView) size to content.
    private int InsertionIndexAt(double listY)
    {
        int count = TrackListBox.ItemCount;
        for (int i = 0; i < count; i++)
        {
            if (TrackListBox.ContainerFromIndex(i) is not Control container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), TrackListBox)?.Y ?? 0;
            if (listY < top + container.Bounds.Height / 2)
                return i;
        }
        return count;
    }

    private double IndicatorOffsetFor(int index)
    {
        var container = TrackListBox.ContainerFromIndex(index)
            ?? (index > 0 ? TrackListBox.ContainerFromIndex(index - 1) : null);
        if (container == null)
            return 0;

        var topLeft = container.TranslatePoint(new Point(0, 0), this) ?? default;
        return index >= TrackListBox.ItemCount ? topLeft.Y + container.Bounds.Height : topLeft.Y;
    }
}
