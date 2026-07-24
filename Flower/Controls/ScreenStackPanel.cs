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
// container that keeps at most 3 real screen instances alive: "current" (on
// top, interactive), "one back" (underneath, revealed by an interactive
// right-swipe - see MobileMainViewModel.PeekOneBack) and "one forward"
// (underneath, revealed by an interactive left-swipe - see
// MobileMainViewModel.PeekOneForward, the browser-style redo counterpart to
// PeekOneBack). Anything further away in either direction stays as plain
// MobileNavigationFrame data until it's promoted, matching the approved
// plan's "never mid-gesture" rule - materializing a control is a discrete
// step that only happens here, in SyncToCurrentFrame, never on a
// per-pointer-move basis.
//
// Also owns the swipe gesture itself (moved here from MobileMainView.axaml.cs
// in an earlier stage - the natural home now that this panel owns the
// content actually being dragged). Every screen paints its own opaque
// background (AppBackgroundBrush - see each ScreenView's own root element)
// so one back/one forward can sit there fully rendered at rest without
// showing through gaps in current, and only becomes visible where current's
// own RenderTransform has slid out of the way.
//
// Each of the 3 live slots is a ScreenSlot (not the raw screen Control
// itself) - a small wrapper pairing the screen's content with its own
// sliding header (title/search box/create-playlist/download-all), so the
// gesture code below (which only ever touches _current's own RenderTransform)
// carries the header along automatically. The raw screen Control for each
// role is tracked separately (_currentInner/_oneBackInner/_oneForwardInner) -
// needed for the factory's identity-reuse check and the NavigationLeaving
// handler's TrackListScreenView pattern-match, neither of which cares about
// the wrapping slot.
public sealed class ScreenStackPanel : Panel
{
    // Same two-stage swipe detection regardless of direction - Stage 1
    // (PointerMoved, EarlyCommitThreshold) decides direction early and, if
    // horizontal, explicitly captures the pointer - without that, a touch
    // starting over a ListBox/ScrollViewer races against its own
    // ScrollGestureRecognizer/Button press machinery, both also watching the
    // same pointer and possibly grabbing it first. Stage 2 (PointerReleased,
    // SwipeThreshold) is the final go/no-go on total distance for the
    // DISCRETE cases (tab-paging either way, or a swipe with nothing to
    // reveal in that direction) - the interactive case below instead decides
    // commit/cancel live, right where the finger let go.
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

    private enum SwipeDirection { None, Back, Forward }

    private readonly ScreenControlFactory _factory = new();
    private ScreenSlot? _current;
    private ScreenSlot? _oneBack;
    private ScreenSlot? _oneForward;
    private Control? _currentInner;
    private Control? _oneBackInner;
    private Control? _oneForwardInner;

    private Point? _swipeStart;
    private bool _capturedForSwipe;

    // None until the committed-to gesture direction actually has something
    // to reveal (CanGoBack for a rightward swipe, CanGoForward for a
    // leftward one) - every other case (tab-paging either way, or a swipe
    // with nothing to reveal in that direction) stays exactly as discrete as
    // before, decided only on release past SwipeThreshold.
    private SwipeDirection _interactiveDirection = SwipeDirection.None;
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
                if (_currentInner is TrackListScreenView currentTrackList)
                    currentTrackList.Freeze(leavingFrame);
            };
            vm.NavigationChanged += (_, _) => SyncToCurrentFrame(vm);
            SyncToCurrentFrame(vm);
        }
    }

    public void SyncToCurrentFrame(MobileMainViewModel vm)
    {
        var currentFrame = vm.CurrentFrame;
        var currentInner = _factory.GetOrCreate(currentFrame);
        currentInner.DataContext = vm;
        if (currentInner is TrackListScreenView currentTrackList)
            currentTrackList.ObserveLive(vm);

        var backFrame = vm.PeekOneBack;
        var forwardFrame = vm.PeekOneForward;
        var backInner = backFrame != null ? PrepareInert(backFrame, vm) : null;
        var forwardInner = forwardFrame != null ? PrepareInert(forwardFrame, vm) : null;

        // Extremely rare coincidence (the back and forward frames happen to
        // share a ScopeKey - e.g. the same album reachable both ways), where
        // the factory's cache would hand back the SAME control instance for
        // both roles. A Control can only ever have one visual parent, so
        // keep it in just one role rather than wrapping it in two slots and
        // fighting over which one actually hosts it.
        if (forwardInner != null && ReferenceEquals(forwardInner, backInner))
            forwardInner = null;

        var current = WrapSlot(_current, _currentInner, currentInner, vm);
        var back = backInner != null ? WrapSlot(_oneBack, _oneBackInner, backInner, vm) : null;
        var forward = forwardInner != null ? WrapSlot(_oneForward, _oneForwardInner, forwardInner, vm) : null;

        current.Frame = currentFrame;
        current.IsVisible = true;
        current.IsHitTestVisible = true;
        // A brand new transform every sync, never a reused/shared one - this
        // only ever runs as the result of an actual completed navigation
        // (dragging alone never fires NavigationChanged), so whichever slot
        // is (re)confirmed as current here should always start clean at
        // X=0, regardless of what transform it carried the last time it was
        // current (e.g. a cancelled drag, or reuse after being the outgoing
        // side of an earlier swipe).
        current.RenderTransform = new TranslateTransform();
        if (currentFrame.IsSearchScreen)
            current.FocusSearchBox();

        if (back != null)
        {
            back.Frame = backFrame;
            back.IsVisible = true;
            back.IsHitTestVisible = false;
            back.RenderTransform = null;
        }
        if (forward != null)
        {
            forward.Frame = forwardFrame;
            forward.IsVisible = true;
            forward.IsHitTestVisible = false;
            forward.RenderTransform = null;
        }

        bool unchanged = ReferenceEquals(_current, current) && ReferenceEquals(_oneBack, back) && ReferenceEquals(_oneForward, forward);

        _current = current;
        _oneBack = back;
        _oneForward = forward;
        _currentInner = currentInner;
        _oneBackInner = backInner;
        _oneForwardInner = forwardInner;

        if (unchanged)
            return;

        // Both "underneath" slots first (render order doesn't matter between
        // the two of them - only one is ever actually uncovered at a time,
        // depending on which direction is being dragged), current last (on
        // top) - a plain Panel stacks children full-bleed in collection
        // order, same as ContentGrid's own default Z-order before this
        // container existed.
        Children.Clear();
        if (back != null)
            Children.Add(back);
        if (forward != null)
            Children.Add(forward);
        Children.Add(current);
    }

    // Reuses the existing slot for a role if it's still wrapping the exact
    // same raw screen control (the common no-navigation-happened resync, or
    // a role whose underlying screen genuinely didn't change), otherwise
    // builds a fresh ScreenSlot and reparents the raw control into it -
    // ScreenSlot.SetContent's own defensive detach handles the case where
    // that control is moving roles (e.g. a swiped-forward "one forward"
    // becoming "current") and still had a prior slot as its visual parent.
    private static ScreenSlot WrapSlot(ScreenSlot? existingSlot, Control? existingInner, Control inner, MobileMainViewModel vm)
    {
        if (existingSlot != null && ReferenceEquals(existingInner, inner))
            return existingSlot;

        var slot = new ScreenSlot { DataContext = vm };
        slot.SetContent(inner);
        return slot;
    }

    // Materializes/refreshes the raw screen control for a non-current (back
    // or forward) slot - always visible (so it can be uncovered) but never
    // hit-testable (it's a preview, not an interactive screen, even
    // mid-gesture).
    private Control PrepareInert(MobileNavigationFrame frame, MobileMainViewModel vm)
    {
        var control = _factory.GetOrCreate(frame);
        control.DataContext = vm;
        if (control is TrackListScreenView trackList)
            trackList.Freeze(frame);
        return control;
    }

    // Lets MobileMainView's Search-tab-icon re-tap handler reach the current
    // slot's search box - tapping the Search tab icon while already on it is
    // a no-op as far as SelectedTab's own setter is concerned (see
    // MobileMainViewModel), so it never re-fires NavigationChanged/the
    // auto-focus above.
    public void FocusSearchBoxIfShowing()
    {
        if (_current?.Frame?.IsSearchScreen == true)
            _current.FocusSearchBox();
    }

    // Lets the fixed back button (MobileMainView's own overlay) play the
    // same slide-off animation as an interactive swipe-back, instead of
    // calling MobileMainViewModel.BackCommand directly and cutting straight
    // to the destination screen - reuses CommitInteractive exactly as a
    // completed drag would, just starting from X=0 rather than wherever a
    // finger let go.
    public void AnimateGoBack()
    {
        if (DataContext is MobileMainViewModel { CanGoBack: true } vm)
            CommitInteractive(vm, SwipeDirection.Back);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _swipeStart = e.GetPosition(this);
        _capturedForSwipe = false;
        _interactiveDirection = SwipeDirection.None;
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
            if (DataContext is MobileMainViewModel vm && _current?.RenderTransform is TranslateTransform)
            {
                if (dx > 0 && vm.CanGoBack)
                    _interactiveDirection = SwipeDirection.Back;
                else if (dx < 0 && vm.CanGoForward)
                    _interactiveDirection = SwipeDirection.Forward;
            }
            e.Handled = true;
        }

        if (_interactiveDirection != SwipeDirection.None && _current?.RenderTransform is TranslateTransform transform)
        {
            var width = Math.Max(1, Bounds.Width);
            // Clamped so the reveal only ever goes as far as fully
            // uncovering whichever screen is underneath, never past it and
            // never into the opposite direction (a gesture that wobbles
            // back past its own start just holds current at 0, fully
            // covering both).
            transform.X = _interactiveDirection == SwipeDirection.Back
                ? Math.Clamp(dx, 0, width)
                : Math.Clamp(dx, -width, 0);
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

        var direction = _interactiveDirection;
        _interactiveDirection = SwipeDirection.None;

        if (DataContext is not MobileMainViewModel vm)
            return;

        var end = e.GetPosition(this);
        var dx = end.X - start.X;

        if (direction != SwipeDirection.None)
        {
            // Distance only for now (matching the discrete case's own
            // threshold) - a velocity estimate for a fast flick under this
            // distance would be a reasonable follow-up, not required for
            // the reveal itself to work correctly.
            var committed = direction == SwipeDirection.Back ? dx > SwipeThreshold : dx < -SwipeThreshold;
            if (committed)
                CommitInteractive(vm, direction);
            else
                CancelInteractive();
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
        if (_interactiveDirection != SwipeDirection.None)
        {
            _interactiveDirection = SwipeDirection.None;
            CancelInteractive();
        }
    }

    // Eases the outgoing (current) screen the rest of the way off-screen in
    // whichever direction was committed to, then commits the actual
    // navigation (MobileMainViewModel.CommitSwipeBack/CommitSwipeForward,
    // same as GoBack()/GoForward()) - only once it's fully off-screen, so
    // the state mutation and the SyncToCurrentFrame resync it triggers never
    // race the animation still in flight. The revealed one-back/one-forward
    // slot is already sitting at X=0 with nothing further to animate -
    // promoting it to "current" is exactly what SyncToCurrentFrame's own
    // fresh-transform assignment above already does, once the commit call's
    // RaiseNavigationChanged fires it.
    private void CommitInteractive(MobileMainViewModel vm, SwipeDirection direction)
    {
        if (_current?.RenderTransform is not TranslateTransform transform)
            return;
        var width = Math.Max(1, Bounds.Width);
        var target = direction == SwipeDirection.Back ? width : -width;
        EaseTransform(transform, target, () =>
        {
            // Fire-and-forget, same async-void shape SwipeBack()/SwipeForward()/
            // BackCommand already use elsewhere - nothing here needs to wait on it.
            if (direction == SwipeDirection.Back)
                _ = vm.CommitSwipeBack();
            else
                _ = vm.CommitSwipeForward();
        });
    }

    private void CancelInteractive()
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
