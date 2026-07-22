using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

namespace Flower.Controls;

// Replaces MobileMainView.axaml's old ContentGrid (7 sibling screens toggled
// by IsVisible - see Stage 1 of the swipe-back navigation feature) with a
// container that keeps at most 2 real screen instances alive: "current" (on
// top, interactive) and "one back" (underneath, revealed by an interactive
// right-swipe - see MobileMainViewModel.PeekOneBack). Anything further back
// in history stays as plain MobileNavigationFrame data until it's promoted,
// matching the approved plan's "never mid-gesture" rule - materializing a
// control is a discrete step that only happens here, in SyncToCurrentFrame,
// never on a per-pointer-move basis.
//
// Also owns the swipe gesture itself (moved here from MobileMainView.axaml.cs
// in this stage - the natural home now that this panel owns the content
// actually being dragged). Every screen paints its own opaque background
// (AppBackgroundBrush - see each ScreenView's own root element) so one back
// can sit there fully rendered at rest without showing through gaps in
// current, and only becomes visible where current's own RenderTransform has
// slid out of the way.
public sealed class ScreenStackPanel : Panel
{
    // Same two-stage swipe detection as before this stage lived on
    // MobileMainView.axaml.cs - Stage 1 (PointerMoved, EarlyCommitThreshold)
    // decides direction early and, if horizontal, explicitly captures the
    // pointer - without that, a touch starting over a ListBox/ScrollViewer
    // races against its own ScrollGestureRecognizer/Button press machinery,
    // both also watching the same pointer and possibly grabbing it first.
    // Stage 2 (PointerReleased, SwipeThreshold) is the final go/no-go on
    // total distance for the DISCRETE cases (tab-paging, or a rightward
    // swipe with nothing to reveal) - the interactive case below instead
    // decides commit/cancel live, right where the finger let go.
    private const double EarlyCommitThreshold = 18.0;
    private const double DirectionRatio = 1.5;
    private const double SwipeThreshold = 60.0;

    // Same duration/tick-rate/easing shape as RubberBandScroll.SpringBackToZero -
    // deliberately not Avalonia's Animation/KeyFrame machinery, which throws
    // InvalidCastException on real iOS hardware when a keyframe targets a
    // plain TranslateTransform (TransformAnimator expects a
    // TransformOperations-shaped RenderTransform instead) - confirmed on
    // device, see that class's own doc comment.
    private const double EasingDurationMs = 280;

    private readonly ScreenControlFactory _factory = new();
    private Control? _current;
    private Control? _oneBack;

    private Point? _swipeStart;
    private bool _capturedForSwipe;

    // True only once the committed-to gesture is a rightward (back) swipe
    // AND there was already a real one-back screen to reveal at the moment
    // direction was decided. Every other case - leftward (tab-paging
    // forward), or a rightward swipe with no history to reveal (SwipeBack's
    // own page-to-previous-tab fallback) - stays exactly as discrete as
    // before, decided only on release past SwipeThreshold.
    private bool _isInteractiveBack;
    private DispatcherTimer? _easingTimer;

    public ScreenStackPanel()
    {
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MobileMainViewModel vm)
        {
            // Fires synchronously, before ANY of the VM's navigation state
            // mutates - freezing the outgoing screen right here (rather
            // than waiting for the later NavigationChanged/SyncToCurrentFrame
            // resync) is what stops it from observing its own state turn
            // briefly inconsistent mid-transition and flashing the wrong
            // content - see MobileMainViewModel.NavigationLeaving's own doc
            // comment for the concrete bug this closes.
            vm.NavigationLeaving += (_, leavingFrame) =>
            {
                if (_current is TrackListScreenView currentTrackList)
                    currentTrackList.Freeze(leavingFrame);
            };
            vm.NavigationChanged += (_, _) => SyncToCurrentFrame(vm);
            SyncToCurrentFrame(vm);
        }
    }

    public void SyncToCurrentFrame(MobileMainViewModel vm)
    {
        var currentFrame = vm.CurrentFrame;
        var currentControl = _factory.GetOrCreate(currentFrame);
        currentControl.DataContext = vm;
        if (currentControl is TrackListScreenView currentTrackList)
            currentTrackList.ObserveLive(vm);

        var backFrame = vm.PeekOneBack;
        Control? backControl = null;
        if (backFrame != null)
        {
            backControl = _factory.GetOrCreate(backFrame);
            backControl.DataContext = vm;
            if (backControl is TrackListScreenView backTrackList)
                backTrackList.Freeze(backFrame);
        }

        currentControl.IsVisible = true;
        currentControl.IsHitTestVisible = true;
        // A brand new transform every sync, never a reused/shared one - this
        // only ever runs as the result of an actual completed navigation
        // (dragging alone never fires NavigationChanged), so whichever
        // control is (re)confirmed as current here should always start
        // clean at X=0, regardless of what transform it carried the last
        // time it was current (e.g. a cancelled drag, or reuse from the
        // factory's cache after being the outgoing side of an earlier swipe).
        currentControl.RenderTransform = new TranslateTransform();

        if (backControl != null)
        {
            backControl.IsVisible = true;
            // Never hit-testable - it's a preview revealed by dragging
            // current out of the way, not an interactive screen in its own
            // right, even mid-gesture.
            backControl.IsHitTestVisible = false;
            // One back never gets dragged itself - only ever current does -
            // so it should always sit at rest, even if this exact control
            // instance was mid-drag the last time it was current.
            backControl.RenderTransform = null;
        }

        if (ReferenceEquals(_current, currentControl) && ReferenceEquals(_oneBack, backControl))
            return;

        // One back first (renders underneath), current last (renders on
        // top) - a plain Panel stacks children full-bleed in collection
        // order, same as ContentGrid's own default Z-order before this.
        Children.Clear();
        if (backControl != null)
            Children.Add(backControl);
        Children.Add(currentControl);
        _current = currentControl;
        _oneBack = backControl;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _swipeStart = e.GetPosition(this);
        _capturedForSwipe = false;
        _isInteractiveBack = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_swipeStart is not { } start)
            return;

        var current = e.GetPosition(this);
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;

        if (!_capturedForSwipe)
        {
            if (Math.Abs(dx) < EarlyCommitThreshold && Math.Abs(dy) < EarlyCommitThreshold)
                return;

            if (Math.Abs(dx) <= Math.Abs(dy) * DirectionRatio)
            {
                // Vertical/ambiguous drag - an ordinary scroll, not a swipe;
                // abandon tracking so release doesn't act on it, and never
                // capture so whatever's underneath (a ScrollViewer/ListBox)
                // keeps handling it normally.
                _swipeStart = null;
                return;
            }

            e.Pointer.Capture(this);
            _capturedForSwipe = true;
            _isInteractiveBack = dx > 0
                && DataContext is MobileMainViewModel { CanGoBack: true }
                && _current?.RenderTransform is TranslateTransform;
            e.Handled = true;
        }

        if (_isInteractiveBack && _current?.RenderTransform is TranslateTransform transform)
        {
            // Clamped to [0, width] - the reveal only ever goes as far as
            // fully uncovering one back, never past it, and never negative
            // (a rightward-committed gesture that wobbles back past its own
            // start just holds current at 0, still fully covering one back).
            transform.X = Math.Clamp(dx, 0, Math.Max(1, Bounds.Width));
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasCaptured = _capturedForSwipe;
        if (wasCaptured)
            e.Pointer.Capture(null);
        _capturedForSwipe = false;

        if (_swipeStart is not { } start)
            return;
        _swipeStart = null;
        if (!wasCaptured)
            return;

        var wasInteractive = _isInteractiveBack;
        _isInteractiveBack = false;

        if (DataContext is not MobileMainViewModel vm)
            return;

        var end = e.GetPosition(this);
        var dx = end.X - start.X;

        if (wasInteractive)
        {
            // Distance only for now (matching the discrete case's own
            // threshold) - a velocity estimate for a fast flick under this
            // distance would be a reasonable follow-up, not required for
            // the reveal itself to work correctly.
            if (dx > SwipeThreshold)
                CommitInteractiveBack(vm);
            else
                CancelInteractiveBack();
            e.Handled = true;
            return;
        }

        if (Math.Abs(dx) > SwipeThreshold)
        {
            if (dx > 0)
                vm.SwipeBack();
            else
                vm.SwipeForward();
        }
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Defensive: if something else forcibly steals the capture mid-
        // gesture, don't get stuck thinking we still own it - and don't
        // leave the outgoing screen stranded mid-drag either.
        _capturedForSwipe = false;
        _swipeStart = null;
        if (_isInteractiveBack)
        {
            _isInteractiveBack = false;
            CancelInteractiveBack();
        }
    }

    // Eases the outgoing (current) screen the rest of the way off-screen,
    // then commits the actual navigation (MobileMainViewModel.CommitSwipeBack,
    // same as GoBack()) - only once it's fully off-screen, so the state
    // mutation and the SyncToCurrentFrame resync it triggers never race the
    // animation still in flight. The revealed one-back control is already
    // sitting at X=0 with nothing further to animate - promoting it to
    // "current" is exactly what SyncToCurrentFrame's own fresh-transform
    // assignment above already does, once CommitSwipeBack's
    // RaiseNavigationChanged fires it.
    private void CommitInteractiveBack(MobileMainViewModel vm)
    {
        if (_current?.RenderTransform is not TranslateTransform transform)
            return;
        var target = Math.Max(1, Bounds.Width);
        EaseTransform(transform, target, () =>
        {
            // Fire-and-forget, same async-void shape SwipeBack()/BackCommand
            // already use elsewhere - nothing here needs to wait on it.
            _ = vm.CommitSwipeBack();
        });
    }

    private void CancelInteractiveBack()
    {
        if (_current?.RenderTransform is TranslateTransform transform)
            EaseTransform(transform, 0, null);
    }

    private void EaseTransform(TranslateTransform transform, double target, Action? onFinished)
    {
        _easingTimer?.Stop();

        var from = transform.X;
        var start = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(EasingDurationMs);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _easingTimer = timer;
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds);
            transform.X = from + (target - from) * EaseOut(t);

            if (t >= 1.0)
            {
                transform.X = target;
                timer.Stop();
                if (ReferenceEquals(_easingTimer, timer))
                    _easingTimer = null;
                onFinished?.Invoke();
            }
        };
        timer.Start();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
