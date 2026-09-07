using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
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
        easing = Clock.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            apply(EaseOut(t));

            if (t >= 1.0)
            {
                apply(1.0);
                easing!.Dispose();
                if (ReferenceEquals(_easing, easing))
                    _easing = null;
                onFinished?.Invoke();
            }
        });
        _easing = easing;
    }

    private void StopEasing()
    {
        _easing?.Dispose();
        _easing = null;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
