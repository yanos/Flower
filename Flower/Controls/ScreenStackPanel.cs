using System;
using System.Linq;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Services;
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
// so the revealed one can sit there fully rendered at rest without showing
// through gaps in current, and only becomes visible where current's own
// RenderTransform has slid out of the way. Which is also why exactly one of
// one-back/one-forward is ever IsVisible at a time (see Reveal): they are both
// full-bleed and opaque, so with both showing, the one that happens to be on
// top is what a gesture uncovers regardless of the direction it is going.
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
    private IDisposable? _easing;

    // Jumps whatever easing is in flight straight to its end (and runs its
    // completion, if any) - see EaseTransform. A finger landing on a screen
    // that is still sliding in or out has to find it settled, since the
    // gesture code below drives the very same TranslateTransform and would
    // otherwise fight the easing for it frame by frame.
    private Action? _finishEasing;

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
            vm.ScrollToTopRequested += (_, _) => ScrollCurrentToTop();
            vm.NavigationLeaving += (_, leavingFrame) =>
            {
                // A navigation takes the screen away; its glide to the top
                // must not keep writing to a list that is no longer current.
                _scrollToTop?.Dispose();
                _scrollToTop = null;
                // Read before freezing, which rebinds the rows the list shows.
                if (_currentInner != null && ScrollerOf(_currentInner) is { } scroller)
                    _scrollOffsets.AddOrUpdate(leavingFrame, new StrongBox<Vector>(scroller.Offset));
                if (_currentInner is TrackListScreenView currentTrackList)
                    currentTrackList.Freeze(leavingFrame);
            };
            vm.NavigationChanged += (_, _) => SyncToCurrentFrame(vm);
            SyncToCurrentFrame(vm);
        }
    }

    // Raised once the screen a navigation lands on is at rest: straight away
    // when it cuts, or when its entrance slide ends. A back step has already
    // done its sliding before the navigation commits, so it lands at rest too.
    // MobileMainView shows and hides its back button off this rather than off
    // CanGoBack, which changes as the navigation starts - so the button would
    // otherwise pop in over a screen still sliding into place.
    public event EventHandler? Settled;

    // Raised as a screen starts to move: a drag uncovering the one behind it,
    // a back step easing it off, an entrance sliding one in. Settled follows
    // once it stops, a cancelled drag included. MobileMainView hides its empty
    // state in between, since that sits over the stack rather than inside a
    // screen and would otherwise hang in place over one sliding away.
    public event EventHandler? Moving;

    private void RaiseSettled() => Settled?.Invoke(this, EventArgs.Empty);

    private void RaiseMoving() => Moving?.Invoke(this, EventArgs.Empty);

    public void SyncToCurrentFrame(MobileMainViewModel vm)
    {
        var currentFrame = vm.CurrentFrame;
        var currentInner = _factory.GetOrCreate(currentFrame);
        currentInner.DataContext = vm;
        if (currentInner is TrackListScreenView currentTrackList)
            currentTrackList.ObserveLive(vm);

        var backFrame = vm.PeekOneBack;
        var forwardFrame = vm.PeekOneForward;
        // A frame sharing the current screen's ScopeKey gets the same control
        // instance back from the factory, and a Control can only ever have one
        // visual parent: wrapping it in a second slot reparents it out of the
        // current one, which is then left hosting nothing. That is a screen
        // showing its header and an empty body - the title and the back arrow
        // live on the slot, not on the screen - and it is reachable in ordinary
        // use, not only in theory: tapping the Now Playing sheet's album art
        // while already on that album's track list pushes a history entry for
        // the screen being landed on, and going back from there hands the same
        // duplicate to the forward stack. So current keeps the cached instance
        // and the colliding role gets a private, uncached one of its own
        // (PrepareInert) rather than going unmaterialized: a slot that renders
        // nothing is a hole a swipe uncovers, which reads as the wrong screen
        // just as much as showing the wrong one does.
        // Forward additionally yields to back, for the same reason: the two
        // inert frames can share a ScopeKey too (the same album reachable both
        // ways), and back is the role a gesture is far likelier to want.
        var backInner = PrepareInert(backFrame, vm, currentInner);
        var forwardInner = PrepareInert(forwardFrame, vm, currentInner, backInner);

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
        var restoredFrame = vm.ConsumeRestoredFrame();

        // Both inert slots are full-bleed and opaque, and only ONE of them can
        // be the screen a given motion uncovers - so exactly one is visible at
        // a time, chosen by the direction in flight (see Reveal). Z-order alone
        // cannot decide it: whichever is added last wins everywhere they
        // overlap, which is everywhere, so with both visible a swipe-back
        // uncovered the FORWARD screen and only snapped to the right one once
        // the commit resync ran - "the wrong view underneath, correct as soon
        // as the transition finished". Back is the resting default because it
        // is what the entrance animation below uncovers, and what the far more
        // common gesture asks for.
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
            forward.IsVisible = false;
            forward.IsHitTestVisible = false;
            forward.RenderTransform = null;
        }

        // A forward navigation (a tab tap, a picker tile, a drill-in) has an
        // outgoing screen that stays put and an incoming one that arrives over
        // it - the mirror image of a swipe-back, which slides the outgoing one
        // off a stationary destination. So the entrance is animated here, on
        // the freshly-current slot, starting off-screen on whichever side the
        // ViewModel says this navigation came from and easing to 0 with the
        // same duration/easing a committed swipe uses. Back/Forward report
        // None (their outgoing screen is animated off by CommitInteractive
        // instead), and so does the very first sync, which has no outgoing
        // screen to arrive over - both cut straight to X=0 as before.
        var transition = vm.ConsumePendingTransition();
        var entranceWidth = Bounds.Width;

        bool unchanged = ReferenceEquals(_current, current) && ReferenceEquals(_oneBack, back) && ReferenceEquals(_oneForward, forward);

        // Only a screen that has just become current starts at the top; a
        // resync over the one already showing (a rescan, a search refresh)
        // leaves it where the user has it.
        var currentOffset = restoredFrame != null
            ? SavedOffset(restoredFrame)
            : ReferenceEquals(_currentInner, currentInner) ? null : Vector.Zero;
        var scrollOffsets = new (Control?, Vector?)[]
        {
            (currentInner, currentOffset), (backInner, SavedOffset(backFrame)), (forwardInner, SavedOffset(forwardFrame)),
        };

        _current = current;
        _oneBack = back;
        _oneForward = forward;
        _currentInner = currentInner;
        _oneBackInner = backInner;
        _oneForwardInner = forwardInner;

        if (unchanged)
        {
            RestoreScrollOffsets(scrollOffsets);
            RaiseSettled();
            return;
        }

        // Both "underneath" slots first, current last (on top) - a plain Panel
        // stacks children full-bleed in collection order, same as ContentGrid's
        // own default Z-order before this container existed. Which of the two
        // sits above the other is deliberately NOT what decides the one a
        // gesture uncovers; IsVisible above does, since only one of them is
        // ever the right answer and both cover the whole panel.
        Children.Clear();
        if (back != null)
            Children.Add(back);
        if (forward != null)
            Children.Add(forward);
        Children.Add(current);

        // Before the entrance below parks the screen off to one side, for the
        // same reason its own layout pass comes first: it has to be measured
        // where it will rest.
        RestoreScrollOffsets(scrollOffsets);

        if (transition != MobileNavigationTransition.None
            && back != null
            && entranceWidth > 0
            && current.RenderTransform is TranslateTransform entrance)
        {
            // Lay the incoming screen out where it will come to REST before
            // shoving it off-screen to start from, or it does its first (and,
            // for a virtualized list, its only) measure with a viewport that
            // is entirely outside the window.
            //
            // A virtualizing panel realizes as many items as its effective
            // viewport asks for, and the effective viewport is computed
            // through the render transforms above it - so a screen measured
            // at X=width has an empty one and realizes a single row. Sliding
            // it back to 0 is a RenderTransform change, which repaints
            // without invalidating any layout, so nothing ever asks that
            // panel for the other rows again: the screen arrives holding one
            // or two entries and stays that way until some later navigation
            // happens to re-measure it. That is the "a view comes up nearly
            // empty until I switch away and back" bug, and it is a property
            // of the entrance animation rather than of any one screen - the
            // Songs list, the album grids and the artist picker all virtualize.
            //
            // Synchronous, so no frame is ever rendered with the screen at 0:
            // the transform below is set before this dispatcher job returns.
            UpdateLayout();
            entrance.X = transition == MobileNavigationTransition.FromRight ? entranceWidth : -entranceWidth;
            RaiseMoving();
            EaseTransform(entrance, 0, RaiseSettled);
        }
        else
            RaiseSettled();
    }

    // Where each screen was scrolled to when the user left it, keyed on the
    // exact frame object the ViewModel recorded for that departure - the one
    // that goes into the history and into the tab's remembered screens (see
    // MobileMainViewModel.RememberTab), and that comes back out when either
    // lands there again. By reference, not by value: the same album visited
    // twice is two departures, and only the one being returned to has a say.
    // Weak, so a frame dropped from every history takes its offset with it.
    //
    // Kept here rather than trusting each control to hold on to its own: the
    // cache evicts, the pickers' item sources are replaced as the sidebar scope
    // moves, and a ScrollViewer clamps its offset to whatever extent it has at
    // the time - any of which puts a list back at the top.
    private readonly ConditionalWeakTable<MobileNavigationFrame, StrongBox<Vector>> _scrollOffsets = new();

    // The screen's one scroller: the first showing one, outermost first. The
    // track list screen has two and hides whichever its mode does not use.
    private static ScrollViewer? ScrollerOf(Control screen) =>
        screen.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible);

    private Vector? SavedOffset(MobileNavigationFrame? frame) =>
        frame != null && _scrollOffsets.TryGetValue(frame, out var box) ? box.Value : null;

    // The screen a Back, Forward or tab restore landed on goes back to where it
    // was left, and so do both inert ones, so a swipe uncovers them there. A
    // screen a fresh navigation brings up starts at the top: the cache hands
    // back the same control for an album opened again, still scrolled wherever
    // the last visit left it.
    //
    // Set, laid out, and set again: a virtualizing list's extent is an estimate
    // grown from the rows it has realized, so the first try can be clamped
    // short of an offset the list really does reach.
    private void RestoreScrollOffsets(params (Control? Screen, Vector? Offset)[] screens)
    {
        var pending = screens.Where(s => s is { Screen: not null, Offset: not null }).ToList();
        if (pending.Count == 0)
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            UpdateLayout();
            var settled = true;
            foreach (var (screen, offset) in pending)
            {
                if (ScrollerOf(screen!) is not { } scroller || scroller.Offset == offset!.Value)
                    continue;
                scroller.Offset = offset.Value;
                settled = false;
            }
            if (settled)
                return;
        }
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
    // or forward) slot - never hit-testable (it's a preview, not an
    // interactive screen, even mid-gesture), and shown only while a motion is
    // actually uncovering it (see Reveal).
    //
    // `taken` is whatever other roles have already claimed this sync. A control
    // handed out twice is a control with one visual parent and two slots
    // wanting it, so a role that would collide gets a private instance of the
    // same screen instead. Substituting before the Freeze below matters as much
    // as before the reparenting does: freezing a control another role is using
    // live would detach it from the ViewModel and pin it to whatever rows this
    // history entry captured.
    private Control? PrepareInert(MobileNavigationFrame? frame, MobileMainViewModel vm, params Control?[] taken)
    {
        if (frame == null)
            return null;
        var control = _factory.GetOrCreate(frame);
        if (Array.Exists(taken, t => ReferenceEquals(t, control)))
            control = ScreenControlFactory.CreateDetached(frame.ScreenKind);
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
        if (DataContext is not MobileMainViewModel { CanGoBack: true } vm)
            return;
        // A destination that carries a sheet animates itself: the sheet is
        // full-bleed and arrives from the left over whatever is underneath, so
        // sliding the screen off first would spend 280ms uncovering a
        // destination the sheet is about to cover again anyway - two beats for
        // one back step. See MobileNavigationFrame.Sheet. A swipe is different
        // and keeps its slide: there the screen is moving because a finger
        // moved it.
        if (vm.PeekOneBack?.Sheet is not (null or MobileSheet.None))
        {
            vm.BackCommand.Execute(null);
            return;
        }
        CommitInteractive(vm, SwipeDirection.Back);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _finishEasing?.Invoke();
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
                Reveal(_interactiveDirection);
                if (_interactiveDirection != SwipeDirection.None)
                    RaiseMoving();
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
            {
                CancelInteractive();
                Reveal(SwipeDirection.None);
            }
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
            Reveal(SwipeDirection.None);
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
        // AnimateGoBack reaches here without any pointer having moved, so this
        // is the only thing that picks the revealed slot on the back-button
        // path.
        Reveal(direction);
        RaiseMoving();
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
            EaseTransform(transform, 0, RaiseSettled);
    }

    // Uncovers the one inert slot this motion is actually revealing and hides
    // the other, so a gesture can never expose the screen belonging to the
    // opposite direction. None restores the resting arrangement (back showing,
    // forward hidden) - covered by current either way, and already correct for
    // the next entrance animation.
    private void Reveal(SwipeDirection direction)
    {
        if (_oneBack != null)
            _oneBack.IsVisible = direction != SwipeDirection.Forward;
        if (_oneForward != null)
            _oneForward.IsVisible = direction == SwipeDirection.Forward;
    }

    private void EaseTransform(TranslateTransform transform, double target, Action? onFinished)
    {
        _easing?.Dispose();
        _finishEasing = null;

        var from = transform.X;
        var duration = TimeSpan.FromMilliseconds(EasingDurationMs);
        // Shared 60Hz clock rather than a timer per easing - see AnimationClock.
        IDisposable? easing = null;
        // The last frame's work, named so a pointer press can run it early
        // (see _finishEasing) rather than leaving a half-slid screen behind.
        // Idempotent: whichever of the two paths gets there first clears the
        // field the other would have read.
        void Finish()
        {
            if (_finishEasing == null)
                return;
            _finishEasing = null;
            transform.X = target;
            easing!.Dispose();
            if (ReferenceEquals(_easing, easing))
                _easing = null;
            onFinished?.Invoke();
        }

        easing = AnimationClock.Current.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            transform.X = from + (target - from) * EaseOut(t);

            if (t >= 1.0)
                Finish();
        });
        _easing = easing;
        _finishEasing = Finish;
    }

    // Tapping the tab already showing its first screen: the list glides back
    // to the top over the same easing a screen slides in with, rather than
    // jumping there. On the shared clock rather than an Avalonia animation
    // for the reason EasingDurationMs gives.
    private IDisposable? _scrollToTop;

    private void ScrollCurrentToTop()
    {
        _scrollToTop?.Dispose();
        if (_currentInner == null || ScrollerOf(_currentInner) is not { } scroller || scroller.Offset.Y <= 0)
            return;

        var from = scroller.Offset;
        var duration = TimeSpan.FromMilliseconds(EasingDurationMs);
        IDisposable? scrolling = null;
        scrolling = AnimationClock.Current.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            scroller.Offset = new Vector(from.X, from.Y * (1 - EaseOut(t)));
            if (t < 1.0)
                return;
            scrolling!.Dispose();
            if (ReferenceEquals(_scrollToTop, scrolling))
                _scrollToTop = null;
        });
        _scrollToTop = scrolling;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
