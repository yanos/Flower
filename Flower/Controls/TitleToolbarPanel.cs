using System;

using Avalonia;
using Avalonia.Controls;

namespace Flower.Controls;

// A heading and its row of buttons: side by side, the buttons at the right
// edge, while the whole heading fits beside them; otherwise the heading on a
// line of its own, trimmed to the width if it must be, and the buttons under
// it across the width, for a toolbar that keeps some of itself at the right.
// The desktop artist's name, whose length is anybody's, over Play, Shuffle and
// Download, with Sort and the filter at the right. Two children, heading first.
public class TitleToolbarPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TitleToolbarPanel, double>(nameof(Spacing), 16);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private bool _stacked;

    static TitleToolbarPanel()
    {
        AffectsMeasure<TitleToolbarPanel>(SpacingProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
            return base.MeasureOverride(availableSize);

        var title = Children[0];
        var toolbar = Children[1];
        toolbar.Measure(Size.Infinity);
        title.Measure(Size.Infinity);

        var width = availableSize.Width;
        _stacked = title.DesiredSize.Width + Spacing + toolbar.DesiredSize.Width > width;
        if (!_stacked)
            return new Size(Math.Min(width, title.DesiredSize.Width + Spacing + toolbar.DesiredSize.Width),
                Math.Max(title.DesiredSize.Height, toolbar.DesiredSize.Height));

        title.Measure(new Size(width, double.PositiveInfinity));
        return new Size(Math.Min(width, Math.Max(title.DesiredSize.Width, toolbar.DesiredSize.Width)),
            title.DesiredSize.Height + Spacing + toolbar.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
            return base.ArrangeOverride(finalSize);

        var title = Children[0];
        var toolbar = Children[1];
        if (_stacked)
        {
            title.Arrange(new Rect(0, 0, finalSize.Width, title.DesiredSize.Height));
            toolbar.Arrange(new Rect(0, title.DesiredSize.Height + Spacing,
                finalSize.Width, toolbar.DesiredSize.Height));
            return finalSize;
        }

        var height = Math.Max(title.DesiredSize.Height, toolbar.DesiredSize.Height);
        title.Arrange(new Rect(0, (height - title.DesiredSize.Height) / 2,
            finalSize.Width - toolbar.DesiredSize.Width - Spacing, title.DesiredSize.Height));
        toolbar.Arrange(new Rect(finalSize.Width - toolbar.DesiredSize.Width, (height - toolbar.DesiredSize.Height) / 2,
            toolbar.DesiredSize.Width, toolbar.DesiredSize.Height));
        return finalSize;
    }
}
