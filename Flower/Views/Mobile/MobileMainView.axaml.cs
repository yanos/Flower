using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Logging;
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

    // Two-stage swipe detection. Stage 1 (PointerMoved, EarlyCommitThreshold)
    // decides direction early and, if horizontal, explicitly captures the
    // pointer on ContentGrid - this is the part that actually makes the
    // gesture reliable: without an explicit capture, a touch that starts over
    // a ListBox/ScrollViewer races against that control's own internal
    // ScrollGestureRecognizer (used for ordinary list scrolling) and a row's
    // own Button press machinery, both of which are also watching the same
    // pointer and may grab it first depending on exactly which pixel the
    // touch landed on - observed in practice as swipes that only "sometimes"
    // register depending on where onscreen they started. Explicitly capturing
    // the pointer the moment horizontal intent is clear makes Flower the sole
    // recipient of the rest of the gesture regardless of what's underneath,
    // matching how a real edge-swipe-style gesture (e.g. Safari/Reddit-style
    // back-swipe) claims the touch outright rather than competing for it.
    // Stage 2 (PointerReleased, SwipeThreshold) is the final go/no-go on
    // total distance travelled - a captured-but-short drag (finger lifted
    // before crossing SwipeThreshold) simply cancels, same as a partial
    // iOS edge-swipe-back that doesn't complete.
    private const double EarlyCommitThreshold = 18.0;
    private const double DirectionRatio = 1.5;
    private const double SwipeThreshold = 60.0;

    private Point? _contentSwipeStart;
    private bool _capturedForSwipe;

    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(MobileMainView).FullName!);

    public MobileMainView()
    {
        InitializeComponent();

        ContentGrid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _contentSwipeStart = e.GetPosition(ContentGrid);
            _capturedForSwipe = false;
            Logger.LogDebug("Swipe: PointerPressed at {Position}", _contentSwipeStart);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        ContentGrid.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_contentSwipeStart is not { } start || _capturedForSwipe)
                return;

            var current = e.GetPosition(ContentGrid);
            var dx = current.X - start.X;
            var dy = current.Y - start.Y;
            if (Math.Abs(dx) < EarlyCommitThreshold && Math.Abs(dy) < EarlyCommitThreshold)
                return;

            if (Math.Abs(dx) > Math.Abs(dy) * DirectionRatio)
            {
                Logger.LogDebug("Swipe: committing to horizontal, capturing pointer");
                e.Pointer.Capture(ContentGrid);
                _capturedForSwipe = true;
                e.Handled = true;
            }
            else
            {
                // Vertical/ambiguous drag - this is an ordinary scroll, not a
                // swipe; abandon tracking so PointerReleased doesn't act on it,
                // and never capture so the ScrollViewer keeps handling it normally.
                _contentSwipeStart = null;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        ContentGrid.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            var wasCaptured = _capturedForSwipe;
            if (wasCaptured)
                e.Pointer.Capture(null);
            _capturedForSwipe = false;

            if (_contentSwipeStart is not { } start)
            {
                Logger.LogDebug("Swipe: PointerReleased with no matching PointerPressed - ignoring");
                return;
            }
            _contentSwipeStart = null;
            if (!wasCaptured)
                return;
            if (DataContext is not MobileMainViewModel vm)
                return;

            var end = e.GetPosition(ContentGrid);
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            Logger.LogDebug("Swipe: PointerReleased at {Position}, dx={Dx}, dy={Dy}", end, dx, dy);
            if (Math.Abs(dx) > SwipeThreshold)
            {
                Logger.LogInformation("Swipe detected: {Direction}", dx > 0 ? "back" : "forward");
                if (dx > 0)
                    vm.SwipeBack();
                else
                    vm.SwipeForward();
            }

            // Already captured (and therefore already the sole recipient of
            // this gesture) means nothing underneath can turn this release
            // into a tap regardless of whether it crossed SwipeThreshold -
            // suppress unconditionally, not just on a successful swipe.
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        ContentGrid.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            // Defensive: if something else forcibly steals the capture mid-
            // gesture, don't get stuck thinking we still own it.
            _capturedForSwipe = false;
            _contentSwipeStart = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Auto-focuses SearchBox the moment it actually becomes the visible search
    // surface - covers both the dedicated Search tab (always shows it) and the
    // Songs tab's own toggleable box (see MobileMainViewModel.IsShowingSearchBox),
    // so tapping either one drops the keyboard straight in rather than requiring
    // a second tap on the box itself. Posted rather than called inline - IsVisible's
    // own binding update from the same PropertyChanged notification hasn't
    // necessarily been applied yet at this point, and Avalonia won't focus a
    // control that's still invisible.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MobileMainViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MobileMainViewModel.IsShowingSearchBox) && vm.IsShowingSearchBox)
                    Dispatcher.UIThread.Post(() => SearchBox.Focus());
            };
        }
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
