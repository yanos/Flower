using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Flower.Controls;

// A Border that is frosted glass: whatever is drawn under it shows through
// blurred, inside its rim. Every floating oval on the phone is one - the back
// and menu circles, the filter and search oval, the mini player, the tab bar,
// the busy pill - so a list running under them goes soft there rather than
// vanishing behind a solid fill. They have no fill of their own
// (IconButtons.axaml), except over a painted page such as an album's, which
// fills them with the page itself (ScreenSlot.FillWithPage); with a fill
// there is nothing to see through, and no blur is drawn.
//
// The blur is BackdropBlur's. Border.Render is sealed, so it cannot be drawn
// before the Border's own; it is a visual child of its own instead, the first,
// so it draws after the fill and rim and before the content, covering the
// whole inside of the rim whatever the Padding. It has the oval's place, size
// and transform - a mini player sliding away takes its blur with it.
//
// Styled as a Border (StyleKeyOverride), so every Border.floating style and
// every Border#Name selector reaches it unchanged; Border:frosted picks it out.
public class FrostedBorder : Border
{
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<FrostedBorder, double>(nameof(BlurRadius), 18);

    /// <summary>How soft the blur is: the header band's (ScreenSlot) unless set.</summary>
    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<FrostedBorder, IBrush?>(nameof(Tint));

    /// <summary>
    /// Washed over the blur, inside the rim: whiter in the light theme,
    /// darker in the dark (AppFrostTintBrush, set by IconButtons.axaml).
    /// </summary>
    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private readonly Frost _frost;

    public FrostedBorder()
    {
        _frost = new Frost(this);
        VisualChildren.Insert(0, _frost);
        // What styles know it by: styled as a plain Border, it matches no
        // selector of its own type.
        PseudoClasses.Add(":frosted");
    }

    protected override Type StyleKeyOverride => typeof(Border);

    protected override Size MeasureOverride(Size availableSize)
    {
        _frost.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _frost.Arrange(new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackgroundProperty || change.Property == BorderThicknessProperty
            || change.Property == CornerRadiusProperty || change.Property == BlurRadiusProperty
            || change.Property == TintProperty)
            _frost.InvalidateVisual();
    }

    private sealed class Frost : Control
    {
        private readonly FrostedBorder _owner;

        public Frost(FrostedBorder owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            if (_owner.BlurRadius <= 0 || !SeesThrough(_owner.Background))
                return;

            // Inside the rim, so the rim stays sharp over the blur.
            var rim = _owner.BorderThickness;
            var inside = new Rect(Bounds.Size).Deflate(rim);
            if (inside.Width <= 0 || inside.Height <= 0)
                return;
            // A pill or a circle: one radius at every corner, no more than
            // half its height however large it is set.
            var rounding = Math.Min(_owner.CornerRadius.TopLeft - rim.Left, Math.Min(inside.Width, inside.Height) / 2);
            rounding = Math.Max(rounding, 0);
            context.Custom(new BackdropBlur.BlurOperation(
                inside, _owner.BlurRadius, (float)rounding, sealedIn: true, alphas: null, offsets: null));
            if (_owner.Tint is { } tint)
                context.DrawRectangle(tint, null, inside, rounding, rounding);
        }

        private static bool SeesThrough(IBrush? fill) =>
            fill is null || fill.Opacity <= 0 || fill is ISolidColorBrush { Color.A: 0 };
    }
}
