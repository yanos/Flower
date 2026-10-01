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

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

// The Navigation Bar page - see its XAML. What is here is the drag that
// reorders the bar's rows.
//
// The row under the finger is lifted (.lifted) and follows it, and the rows it
// passes step aside into the place it left, each on a short slide - all of it
// transforms on the rows as they stand. Nothing in VisibleTabRows moves until
// the finger comes up, and then once, through MobileMainViewModel.MoveTab: a
// collection reshuffled under a captured pointer recycles the very container
// the capture is on.
//
// Same wiring as TrackRowDragReorder, and for the same reasons: tunnel routed
// over the list and keyed off the handle the press landed on, so the drag
// starts only from the handle and a press anywhere else in a row is left to
// the page's scrolling and the row's switch.
public partial class NavigationBarSettingsView : UserControl
{
    // How long a row takes to step aside for the one being dragged past it.
    private static readonly TimeSpan StepAsideDuration = TimeSpan.FromMilliseconds(160);

    private MobileTabSettingRow? _row;
    private readonly List<Control> _containers = new();
    private readonly List<TranslateTransform> _offsets = new();
    private Border? _lifted;
    private int _from;
    private int _to;
    private double _startY;
    private double _rowHeight;

    public NavigationBarSettingsView()
    {
        InitializeComponent();

        VisibleList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        VisibleList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        VisibleList.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        VisibleList.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MobileMainViewModel? Vm => DataContext as MobileMainViewModel;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || HandleUnder(e.Source as Visual) is not { } handle)
            return;
        if (handle.DataContext is not MobileTabSettingRow { IsShown: true } row)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        var from = vm.VisibleTabRows.IndexOf(row);
        if (from < 0 || !CollectContainers(vm.VisibleTabRows.Count))
            return;

        _row = row;
        _from = from;
        _to = from;
        _startY = e.GetPosition(VisibleList).Y;
        _rowHeight = _containers[from].Bounds.Height;
        Lift();

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_row == null)
            return;

        // Held to the list: the row can go as far as either end and no
        // further, which is also as far as it can be dropped.
        var travel = Math.Clamp(e.GetPosition(VisibleList).Y - _startY,
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
        if (_row is not { } row)
            return;

        var to = _to;
        e.Pointer.Capture(null);
        EndDrag();

        if (to != _from)
            Vm?.MoveTab(row.Tab, to);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private Border? HandleUnder(Visual? source)
    {
        for (var visual = source; visual != null && !ReferenceEquals(visual, VisibleList); visual = visual.GetVisualParent())
        {
            if (visual is Border { Classes: var classes } border && classes.Contains("dragHandle"))
                return border;
        }

        return null;
    }

    // Every row's container, in order - all of them are realized, since the
    // list is a plain StackPanel of at most seven.
    private bool CollectContainers(int count)
    {
        _containers.Clear();
        for (var i = 0; i < count; i++)
        {
            if (VisibleList.ContainerFromIndex(i) is not Control container)
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
        if (_row == null)
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
        _row = null;
    }
}
