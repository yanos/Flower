using System;

using Avalonia;
using Avalonia.Controls;

namespace Flower.Controls;

// Shows a fraction of its child's height, from the top: 0 nothing, 1 all of
// it. Animating Reveal (a DoubleTransition) opens and closes the child to
// whatever height it really is, where animating Height needs a number to head
// for - which the Albums grid's open album used to get by estimating its song
// rows at 34px each, and was wrong whenever the rows were not. The child is
// always measured and arranged at its full height and clipped to what shows.
public sealed class RevealDecorator : Decorator
{
    public static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<RevealDecorator, double>(nameof(Reveal));

    static RevealDecorator()
    {
        AffectsMeasure<RevealDecorator>(RevealProperty);
        ClipToBoundsProperty.OverrideDefaultValue<RevealDecorator>(true);
    }

    public double Reveal
    {
        get => GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    // The child's whole height, however much of it shows - where it will end
    // once Reveal reaches 1.
    public double FullHeight => Child?.DesiredSize.Height ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
            return default;
        child.Measure(availableSize.WithHeight(double.PositiveInfinity));
        var reveal = Math.Clamp(Reveal, 0, 1);
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * reveal);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, FullHeight));
        return finalSize;
    }
}
