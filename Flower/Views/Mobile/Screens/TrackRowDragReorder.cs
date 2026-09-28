using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Views.Mobile.Screens;

// Touch drag-to-reorder over a list of TrackRowTemplate rows - a playlist's
// tracks (TrackListScreenView) and the queue (QueueScreenView). The desktop
// equivalent (MusicListView) starts dragging immediately anywhere on the row
// with a small 4px threshold, which fights normal touch scrolling. Here
// dragging only starts from the row's handle, with a larger threshold before
// it visually kicks in.
//
// TrackRowTemplate's drag handle can't wire these via XAML event attributes
// (it's a class-less ResourceDictionary - see the template's own comment), so
// they're attached to the list instead, tunnel routed and keyed off e.Source -
// the same technique MobileMainView.axaml.cs uses for its swipe gesture.
internal sealed class TrackRowDragReorder
{
    private const double DragThreshold = 10.0;

    private readonly ItemsControl _list;
    private readonly Visual _indicatorSpace;
    private readonly Border _indicator;
    private readonly Action<TrackRowViewModel, TrackRowViewModel?> _drop;

    private TrackRowViewModel? _draggedRow;
    private double _dragStartY;
    private bool _isDragging;

    /// <param name="list">The list the rows are in.</param>
    /// <param name="indicatorSpace">What <paramref name="indicator"/>'s top margin is measured from.</param>
    /// <param name="indicator">The line drawn where the row will land.</param>
    /// <param name="drop">Called with the dragged row and the row it goes before, or null for the end.</param>
    public TrackRowDragReorder(ItemsControl list, Visual indicatorSpace, Border indicator,
        Action<TrackRowViewModel, TrackRowViewModel?> drop)
    {
        _list = list;
        _indicatorSpace = indicatorSpace;
        _indicator = indicator;
        _drop = drop;

        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Border { Classes: { } classes } handle || !classes.Contains("dragHandle"))
            return;
        if (handle.DataContext is not TrackRowViewModel { CanBeDragged: true } row)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedRow = row;
        _dragStartY = e.GetPosition(_list).Y;
        _isDragging = false;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedRow == null)
            return;
        var y = e.GetPosition(_list).Y;

        if (!_isDragging)
        {
            if (Math.Abs(y - _dragStartY) < DragThreshold)
                return;
            _isDragging = true;
            _indicator.IsVisible = true;
        }

        int index = InsertionIndexAt(y);
        _indicator.Margin = new Thickness(0, IndicatorOffsetFor(index), 0, 0);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only a release that ends a drag of ours. This handler tunnels over
        // the whole list, so it sees every release inside it - and a playlist's
        // header lives inside it too (ScreenScroll.Header), buttons and all.
        // Releasing the capture on a press that was never a drag took the
        // pointer off whatever the finger was on before that control's own
        // release handler ran, and a Button that has lost capture raises no
        // Click: the header's play, shuffle, add-to-playlist and download did
        // nothing at all on a playlist, while the same markup worked on an
        // album, whose header is not inside a list.
        if (_draggedRow == null)
            return;

        if (_isDragging)
        {
            int index = InsertionIndexAt(e.GetPosition(_list).Y);
            var insertBefore = _list.ContainerFromIndex(index)?.DataContext as TrackRowViewModel;
            if (insertBefore != _draggedRow)
                _drop(_draggedRow, insertBefore);
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _draggedRow = null;
        _isDragging = false;
        _indicator.IsVisible = false;
    }

    // Hit-tests realized row containers directly rather than assuming a fixed row
    // height, since mobile rows (unlike desktop's uniform MusicListView) size to content.
    private int InsertionIndexAt(double listY)
    {
        int count = _list.ItemCount;
        for (int i = 0; i < count; i++)
        {
            if (_list.ContainerFromIndex(i) is not Control container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), _list)?.Y ?? 0;
            if (listY < top + container.Bounds.Height / 2)
                return i;
        }
        return count;
    }

    private double IndicatorOffsetFor(int index)
    {
        var container = _list.ContainerFromIndex(index)
            ?? (index > 0 ? _list.ContainerFromIndex(index - 1) : null);
        if (container == null)
            return 0;

        var topLeft = container.TranslatePoint(new Point(0, 0), _indicatorSpace) ?? default;
        return index >= _list.ItemCount ? topLeft.Y + container.Bounds.Height : topLeft.Y;
    }
}
