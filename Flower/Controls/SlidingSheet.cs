using System;
using System.Linq;
using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Services;

namespace Flower.Controls;

// Which edge a sheet arrives from. FromBottom is what every sheet with a
// ChevronDown dismiss or a bottom-anchored card is already telling the user it
// does; FromRight is a pushed screen, matching ScreenStackPanel's own forward
// navigation.
public enum SheetEntrance { FromRight, FromBottom }

// A mobile sheet that arrives by sliding in and leaves the same way, instead of
// appearing and vanishing outright the way a plain IsVisible binding does.
// Wraps the sheet's own view (see MobileMainView.axaml) and owns nothing about
// its content - the sheet stays an ordinary UserControl bound to the shared
// MobileMainViewModel, this just decides when it is on screen and where.
//
// IsOpen replaces the IsVisible binding rather than sitting alongside it: the
// closing animation has to outlive the ViewModel's own "this sheet is no
// longer showing" (ActiveSheet = None), so IsVisible is driven from here, at
// the end of the slide-out, and must not also be bound to the VM.
//
// Mobile has two shapes of sheet and they cannot animate the same way:
//
//   Full-bleed (Settings, Now Playing, Track Info) - opaque edge to edge, so
//   the whole control slides and nothing fades.
//
//   A card over a dimmed backdrop (track actions, add-to-playlist, the two
//   confirmations) - here sliding the whole control would sweep the dimming
//   up the screen with it, leaving the top half undimmed mid-animation. So the
//   card alone slides (marked with SlidingSheet.IsCard - see each of those
//   views) and the sheet's own opacity fades the backdrop in behind it. The
//   card also travels its own height rather than the screen's, or a short card
//   would spend most of the animation still below the bottom edge.
//
// Same manual AnimationClock tween as ScreenStackPanel's swipe easing and
// RubberBandScroll's spring-back, and for the same reason: an Avalonia
// Animation whose keyframes target a plain TranslateTransform routes through
// TransformAnimator, which expects a TransformOperations-shaped
// RenderTransform and throws InvalidCastException on real iOS hardware - see
// RubberBandScroll's own doc comment.
public sealed class SlidingSheet : ContentControl
{
    // Matches ScreenStackPanel.EasingDurationMs - a sheet and a pushed screen
    // are the same gesture as far as the user is concerned, so they should
    // take the same time to arrive.
    private const double EasingDurationMs = 280;

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SlidingSheet, bool>(nameof(IsOpen));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly StyledProperty<SheetEntrance> EntranceProperty =
        AvaloniaProperty.Register<SlidingSheet, SheetEntrance>(nameof(Entrance));

    public SheetEntrance Entrance
    {
        get => GetValue(EntranceProperty);
        set => SetValue(EntranceProperty, value);
    }

    // A sheet normally leaves by the edge it arrived from: that is the reverse
    // of the gesture that opened it, and reads as going back. But some
    // dismissals are themselves forward navigations - Now Playing's album art
    // pushes that album onto the history - and there the sheet has to leave by
    // the opposite edge, so it and the screen arriving from the right travel
    // together as one push. Leaving rightwards instead uncovers the incoming
    // screen from its left edge, which is exactly what going back looks like.
    public static readonly StyledProperty<bool> ExitsForwardProperty =
        AvaloniaProperty.Register<SlidingSheet, bool>(nameof(ExitsForward));

    public bool ExitsForward
    {
        get => GetValue(ExitsForwardProperty);
        set => SetValue(ExitsForwardProperty, value);
    }

    // The entrance counterpart to ExitsForward: a sheet restored by a Back
    // arrives from the left, the edge the forward push shoved it out of.
    // Without it, walking back to a sheet plays as a fresh push from the right,
    // which is the one direction that means the opposite of what happened.
    // Full-bleed sheets only, same as ExitsForward - a card is never part of a
    // navigation.
    public static readonly StyledProperty<bool> EntersBackwardProperty =
        AvaloniaProperty.Register<SlidingSheet, bool>(nameof(EntersBackward));

    public bool EntersBackward
    {
        get => GetValue(EntersBackwardProperty);
        set => SetValue(EntersBackwardProperty, value);
    }

    // What a rightward swipe on a full-bleed pushed sheet runs - the same
    // command its back arrow does (CloseSheetCommand, for both Settings and
    // Now Playing). A sheet that arrives from the right and is dismissed with
    // a back arrow is a pushed screen as far as the user is concerned, and
    // every other pushed screen goes back to the finger as well as to the
    // arrow - see ScreenStackPanel, which owns the identical gesture for the
    // screens underneath these sheets. Left unset, the sheet does not respond
    // to the gesture at all, which is the right answer for the ones that rise
    // from the bottom with a ChevronDown: their dismissal is downwards, not
    // backwards.
    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<SlidingSheet, ICommand?>(nameof(DismissCommand));

    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    // Set on the one element inside a backdrop-and-card sheet that should
    // actually move (the card itself) - see the class comment. A sheet with no
    // such element slides whole.
    public static readonly AttachedProperty<bool> IsCardProperty =
        AvaloniaProperty.RegisterAttached<SlidingSheet, Control, bool>("IsCard");

    public static void SetIsCard(Control control, bool value) => control.SetValue(IsCardProperty, value);
    public static bool GetIsCard(Control control) => control.GetValue(IsCardProperty);

    private readonly TranslateTransform _sheetTransform = new();
    private Control? _card;
    private TranslateTransform? _cardTransform;
    private IDisposable? _easing;

    // Jumps whatever easing is in flight straight to its end (running its
    // completion, if any) - see Ease. Null when nothing is animating.
    private Action? _finishEasing;

    private Point? _swipeStart;
    private bool _capturedForSwipe;

    // The clock the easing runs on. AnimationClock.Current is the process-wide
    // 60Hz timer; a test hands in one it steps by hand, so a sheet's animation
    // can be driven to completion without parking the dispatcher for the real
    // 280ms - see AnimationClock's own test constructor, and
    // MainViewModelHarness on what a test that parks it costs every test queued
    // behind it.
    internal AnimationClock? ClockOverride { get; set; }

    private AnimationClock Clock => ClockOverride ?? AnimationClock.Current;

    public SlidingSheet()
    {
        RenderTransform = _sheetTransform;
        IsVisible = false;

        // Tunnel, and handledEventsToo, for the same reason ScreenStackPanel's
        // are: a touch landing on a list, a button or a scroll viewer inside
        // the sheet is watched by that control's own gesture machinery too, and
        // a bubbling handler would only hear about the ones nobody else wanted.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            if (change.GetNewValue<bool>())
                Open();
            else
                Close();
        }
    }

    private void Open()
    {
        // A close still in flight owns the same transform (and, for a card, the
        // same opacity) and would go on driving it out from under this one -
        // and finish by hiding a sheet the user has just asked for.
        StopEasing();

        // Visible before anything is measured or moved, so the first frame the
        // compositor draws already has the sheet parked off-screen (or, for a
        // card, fully transparent) rather than sitting finished for a frame.
        IsVisible = true;

        if (FindCard() is not { } card)
        {
            Opacity = 1;
            SetOffset(_sheetTransform, StartOffset(SlideDistance));
            // Parked synchronously above, but eased only once the layout this
            // IsVisible has just triggered has landed. A sheet that has been
            // hidden until now has never been arranged, so its first pass is
            // the most expensive frame it will ever have - Now Playing decodes
            // its album art in it - and layout outranks the clock's timer, so
            // an easing subscribed before that pass sees no tick at all until
            // it is over. The first tick it does see is already past the end of
            // a 280ms animation, which is the sheet appearing outright.
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsOpen)
                    return;
                var distance = SlideDistance;
                if (distance <= 0)
                {
                    // Nothing laid out to slide across (the sheet opened before
                    // its parent was ever arranged) - show it outright rather
                    // than animating from a distance that would leave it stuck
                    // off-screen.
                    StopEasing();
                    SetOffset(_sheetTransform, 0);
                    return;
                }
                var from = StartOffset(distance);
                SetOffset(_sheetTransform, from);
                Ease(p => SetOffset(_sheetTransform, from * (1 - p)), null);
            }, DispatcherPriority.Loaded);
            return;
        }

        // A card travels its own height, which it does not have until it has
        // been arranged at least once - and it never has been, having been
        // hidden until this moment. Transparent until then, so the frame or two
        // spent waiting for that layout shows nothing rather than the finished
        // sheet.
        Opacity = 0;
        var transform = CardTransform(card);
        // DispatcherPriority.Loaded runs after the layout pass (Layout being
        // the higher priority), so the card has its real height by then.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsOpen)
                return;
            var distance = card.Bounds.Height > 0 ? card.Bounds.Height : SlideDistance;
            if (distance <= 0)
            {
                StopEasing();
                transform.Y = 0;
                Opacity = 1;
                return;
            }
            transform.Y = distance;
            Ease(p =>
            {
                transform.Y = distance * (1 - p);
                Opacity = p;
            }, null);
        }, DispatcherPriority.Loaded);
    }

    private void Close()
    {
        // Same as Open: an entrance still easing must not keep driving the
        // transform this slide-out is about to take over.
        StopEasing();

        // Hidden only once it is fully off-screen, so the closing slide is
        // actually seen - IsVisible is this control's own state, not a
        // binding, precisely so it can lag the ViewModel by one animation.
        if (FindCard() is not { } card)
        {
            var distance = SlideDistance;
            if (distance <= 0)
            {
                StopEasing();
                SetOffset(_sheetTransform, 0);
                IsVisible = false;
                return;
            }
            // Only the full-bleed sheets have a forward exit - a card over a
            // dimmed backdrop is raised over the screen you were reading, not
            // a screen you can push past, so its slide-out below is always the
            // reverse of its entrance.
            var target = ExitsForward ? -distance : distance;
            var from = GetOffset(_sheetTransform);
            Ease(p => SetOffset(_sheetTransform, from + (target - from) * p), () => IsVisible = false);
            return;
        }

        var transform = CardTransform(card);
        var cardDistance = card.Bounds.Height > 0 ? card.Bounds.Height : SlideDistance;
        if (cardDistance <= 0)
        {
            StopEasing();
            Hide();
            return;
        }
        var cardFrom = transform.Y;
        var fadeFrom = Opacity;
        Ease(p =>
        {
            transform.Y = cardFrom + (cardDistance - cardFrom) * p;
            Opacity = fadeFrom * (1 - p);
        }, Hide);
    }

    // Opacity back to what a full-bleed sheet expects to find, since a card
    // sheet's own close leaves it at 0 and a reopen would otherwise animate
    // something that is never painted. The card's offset is deliberately left
    // where the slide-out put it: it is off-screen, the sheet is hidden, and
    // Open parks it again from scratch anyway.
    private void Hide()
    {
        IsVisible = false;
        Opacity = 1;
    }

    // Looked up lazily and kept: the card lives inside the wrapped view, whose
    // own logical tree is built when that view is constructed, but this control
    // can be asked before that has happened - so a miss is not cached.
    private Control? FindCard() =>
        _card ??= this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(GetIsCard);

    private TranslateTransform CardTransform(Control card)
    {
        if (_cardTransform == null)
        {
            _cardTransform = new TranslateTransform();
            card.RenderTransform = _cardTransform;
        }
        return _cardTransform;
    }

    // The sheet's own Bounds are zero until it has been arranged at least
    // once, and it is hidden (so unarranged) right up to the moment it opens -
    // hence the parent, which is arranged either way and is exactly as big,
    // this being a full-screen sheet.
    private double SlideDistance
    {
        get
        {
            var own = Entrance == SheetEntrance.FromRight ? Bounds.Width : Bounds.Height;
            if (own > 0)
                return own;
            if ((this.GetVisualParent() as Visual)?.Bounds is not { } parent)
                return 0;
            return Entrance == SheetEntrance.FromRight ? parent.Width : parent.Height;
        }
    }

    // Which side of home a full-bleed entrance starts on - see EntersBackward.
    private double StartOffset(double distance) => EntersBackward ? -distance : distance;

    private void SetOffset(TranslateTransform transform, double offset)
    {
        if (Entrance == SheetEntrance.FromRight)
            transform.X = offset;
        else
            transform.Y = offset;
    }

    private double GetOffset(TranslateTransform transform) =>
        Entrance == SheetEntrance.FromRight ? transform.X : transform.Y;

    // apply() is handed eased progress, 0 at the start of the animation and 1
    // at its end - each caller maps that onto whatever it is moving (an offset,
    // an opacity, or both at once off the one clock subscription).
    private void Ease(Action<double> apply, Action? onFinished)
    {
        StopEasing();

        var duration = TimeSpan.FromMilliseconds(EasingDurationMs);
        IDisposable? easing = null;

        // The last frame's work, named so a finger landing mid-slide can run it
        // early (see _finishEasing) instead of fighting the easing for the very
        // transform it is about to drag. Idempotent: whichever of the two paths
        // gets there first clears the field the other would have read.
        void Finish()
        {
            if (_finishEasing == null)
                return;
            _finishEasing = null;
            apply(1.0);
            easing!.Dispose();
            if (ReferenceEquals(_easing, easing))
                _easing = null;
            onFinished?.Invoke();
        }

        easing = Clock.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            apply(EaseOut(t));

            if (t >= 1.0)
                Finish();
        });
        _easing = easing;
        _finishEasing = Finish;
    }

    private void StopEasing()
    {
        _easing?.Dispose();
        _easing = null;
        _finishEasing = null;
    }

    // ── Swipe to dismiss ──────────────────────────────────────────────────
    //
    // A full-bleed sheet that arrived from the right is a pushed screen with a
    // back arrow, so it goes back to a rightward swipe as well - the same
    // gesture, thresholds and easing ScreenStackPanel gives the screens
    // underneath it, written again here rather than shared because the two move
    // different things: that panel drags one of three live slots and commits by
    // navigating, this drags the sheet itself and commits by running the
    // command its own back arrow runs.
    //
    // The commit deliberately does no animating of its own. Executing
    // DismissCommand puts ActiveSheet back to None, which lands on IsOpen and
    // calls Close() - and Close already starts its slide-out from wherever the
    // transform currently is, which is exactly where the finger let go.
    private const double EarlyCommitThreshold = 18.0;
    private const double DirectionRatio = 1.5;
    private const double SwipeThreshold = 60.0;

    // Card sheets are excluded because their dismissal is downwards over a
    // dimmed backdrop, and because the thing that slides is the card rather
    // than this control - a rightward drag would move the backdrop with it.
    private bool CanSwipeToDismiss =>
        IsOpen && Entrance == SheetEntrance.FromRight && DismissCommand != null && FindCard() == null;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A sheet still sliding in has to be found settled, or the drag below
        // and the entrance would drive the same transform frame by frame.
        _finishEasing?.Invoke();

        _capturedForSwipe = false;
        _swipeStart = CanSwipeToDismiss && !StartedOnADraggableControl(e.Source as Visual)
            ? e.GetPosition(this)
            : null;
    }

    // Now Playing's seek bar is dragged horizontally, on purpose, right across
    // the middle of the sheet - and a slider that has to be scrubbed without
    // dismissing the screen underneath is not an edge case there, it is the
    // control the user reaches for most. So a gesture that starts on any
    // range control (the seek bar, a scrollbar thumb) is that control's, and
    // this never looks at it again.
    private bool StartedOnADraggableControl(Visual? source)
    {
        for (var visual = source; visual != null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (visual is RangeBase)
                return true;
        }

        return false;
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

            // Anything vertical or ambiguous is an ordinary scroll (Settings is
            // one long scrolling list), and a leftward drag is nothing at all -
            // there is no forward from a sheet. Both abandon tracking without
            // capturing, so whatever is underneath keeps handling the pointer.
            if (dx <= 0 || Math.Abs(dx) <= Math.Abs(dy) * DirectionRatio)
            {
                _swipeStart = null;
                return;
            }

            e.Pointer.Capture(this);
            _capturedForSwipe = true;
        }

        // Clamped so the drag only ever uncovers what is behind the sheet, never
        // pulls it past its own width, and never travels left of home.
        SetOffset(_sheetTransform, Math.Clamp(dx, 0, Math.Max(1, SlideDistance)));
        e.Handled = true;
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

        e.Handled = true;

        var dx = e.GetPosition(this).X - start.X;
        if (dx > SwipeThreshold && DismissCommand is { } dismiss && dismiss.CanExecute(null))
        {
            dismiss.Execute(null);

            // Only if the command declined to actually close the sheet - it is
            // the ViewModel's decision, not this control's, and a sheet left
            // open must not be left parked halfway off the screen.
            if (!IsOpen)
                return;
        }

        SlideBackToRest();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Defensive, exactly as in ScreenStackPanel: something stealing the
        // capture mid-gesture must not leave the sheet stranded halfway.
        if (!_capturedForSwipe)
            return;

        _capturedForSwipe = false;
        _swipeStart = null;
        SlideBackToRest();
    }

    private void SlideBackToRest()
    {
        var from = GetOffset(_sheetTransform);
        if (from == 0)
            return;

        Ease(p => SetOffset(_sheetTransform, from * (1 - p)), null);
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
