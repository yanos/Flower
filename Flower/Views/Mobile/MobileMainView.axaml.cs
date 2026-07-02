using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class MobileMainView : UserControl
{
    // Touch drag-to-reorder for the playlist track list: the desktop equivalent
    // (MusicListView) starts dragging immediately anywhere on the row with a small
    // 4px threshold, which fights normal touch scrolling. Here dragging only starts
    // from a dedicated handle icon, with a larger threshold before it visually kicks in.
    private const double DragThreshold = 10.0;
    private TrackRowViewModel? _draggedRow;
    private double _dragStartY;
    private bool _isDragging;

    public MobileMainView()
    {
        InitializeComponent();
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || handle.DataContext is not TrackRowViewModel row)
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

        var topLeft = container.TranslatePoint(new Point(0, 0), ContentGrid) ?? default;
        return index >= TrackListBox.ItemCount ? topLeft.Y + container.Bounds.Height : topLeft.Y;
    }
}
