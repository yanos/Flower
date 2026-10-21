using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Flower.Controls;

// Dragging the rows of a short list into a new order by a handle - the
// Navigation Bar page's tabs, and Home's shelves in Settings on either head.
//
// The row under the finger is lifted (.lifted, on the Border that is its
// template's root) and follows it, and the rows it passes step aside into the
// place it left, each on a short slide - all of it transforms on the rows as
// they stand. Nothing in the list moves until the finger comes up, and then
// once, through the move it was given: a collection reshuffled under a
// captured pointer recycles the very container the capture is on.
//
// Same wiring as TrackRowDragReorder, and for the same reasons: tunnel routed
// over the list and keyed off the handle the press landed on (a Border of
// class dragHandle), so the drag starts only from the handle and a press
// anywhere else in a row is left to the page's scrolling and the row's own
// controls. Every row must be realized - a plain StackPanel of a few - which
// is what makes the arithmetic one row height per place.
//
// Handling the press is not enough to keep it, though. A ScrollViewer's
// ScrollGestureRecognizer is handed every touch whether or not something
// handled it, and once the finger has travelled a few pixels it captures the
// pointer for itself - which took it off the handle, ended the drag and
// scrolled the page instead, as soon as the list was on a page tall enough to
// scroll (Settings; the Navigation Bar page fits on a phone, so it never
// showed there). So every event of a drag of ours is kept from the
// recognizers (PreventGestureRecognition), and the page stays still under it.
public sealed class HandleDragReorder
{
    // How long a row takes to step aside for the one being dragged past it.
    private static readonly TimeSpan StepAsideDuration = TimeSpan.FromMilliseconds(160);

    private readonly ItemsControl _list;
    private readonly Func<object?, int> _indexOf;
    private readonly Action<object, int> _move;

    private object? _item;
    private readonly List<Control> _containers = new();
    private readonly List<TranslateTransform> _offsets = new();
    private Border? _lifted;
    private int _from;
    private int _to;
    private double _startY;
    private double _rowHeight;

    // indexOf: where a row's item stands in the list, or -1 for one that
    // cannot be dragged. move: the item, and the place it was dropped at.
    public HandleDragReorder(ItemsControl list, Func<object?, int> indexOf, Action<object, int> move)
    {
        _list = list;
        _indexOf = indexOf;
        _move = move;

        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HandleUnder(e.Source as Visual) is not { DataContext: { } item } handle)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        var from = _indexOf(item);
        if (from < 0 || !CollectContainers(_list.ItemCount))
            return;

        _item = item;
        _from = from;
        _to = from;
        _startY = e.GetPosition(_list).Y;
        _rowHeight = _containers[from].Bounds.Height;
        Lift();

        e.Pointer.Capture(handle);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_item == null)
            return;

        e.PreventGestureRecognition();

        // Held to the list: the row can go as far as either end and no
        // further, which is also as far as it can be dropped.
        var travel = Math.Clamp(e.GetPosition(_list).Y - _startY,
            -_from * _rowHeight,
            (_containers.Count - 1 - _from) * _rowHeight);
        _offsets[_from].Y = travel;

        var to = Math.Clamp((int)Math.Round(_from + travel / _rowHeight), 0, _containers.Count - 1);
        if (to == _to)
            return;

        _to = to;
        for (var i = 0; i < _offsets.Count; i++)
        {
            if (i == _from)
                continue;

            _offsets[i].Y = i > _from && i <= to ? -_rowHeight
                : i < _from && i >= to ? _rowHeight
                : 0;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only a release ending a drag of ours - see TrackRowDragReorder's,
        // where letting go of a capture that was never taken cost the buttons
        // under the finger their Click.
        if (_item is not { } item)
            return;

        var to = _to;
        var from = _from;
        e.PreventGestureRecognition();
        e.Pointer.Capture(null);
        EndDrag();

        if (to != from)
            _move(item, to);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private Border? HandleUnder(Visual? source)
    {
        for (var visual = source; visual != null && !ReferenceEquals(visual, _list); visual = visual.GetVisualParent())
        {
            if (visual is Border { Classes: var classes } border && classes.Contains("dragHandle"))
                return border;
        }

        return null;
    }

    private bool CollectContainers(int count)
    {
        _containers.Clear();
        for (var i = 0; i < count; i++)
        {
            if (_list.ContainerFromIndex(i) is not Control container)
            {
                _containers.Clear();
                return false;
            }

            _containers.Add(container);
        }

        return true;
    }

    // The dragged row follows the finger exactly, so its offset has no
    // transition; the rest slide.
    private void Lift()
    {
        _offsets.Clear();
        for (var i = 0; i < _containers.Count; i++)
        {
            var offset = new TranslateTransform();
            if (i != _from)
            {
                offset.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = TranslateTransform.YProperty,
                        Duration = StepAsideDuration,
                        Easing = new CubicEaseOut(),
                    },
                ];
            }

            _offsets.Add(offset);
            _containers[i].RenderTransform = offset;
        }

        var dragged = _containers[_from];
        dragged.ZIndex = 1;
        _lifted = (dragged as ContentPresenter)?.Child as Border;
        _lifted?.Classes.Add("lifted");
    }

    // Everything back where it stands. A drop then moves the rows for real,
    // before the next frame is drawn.
    private void EndDrag()
    {
        if (_item == null)
            return;

        foreach (var container in _containers)
        {
            container.RenderTransform = null;
            container.ZIndex = 0;
        }

        _lifted?.Classes.Remove("lifted");
        _lifted = null;
        _containers.Clear();
        _offsets.Clear();
        _item = null;
    }
}
