using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

using Flower.ViewModels;

namespace Flower.Views;

public partial class EqualizerWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public EqualizerWindow()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => TraceCurve();
    }

    public EqualizerWindow(EqualizerViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private List<Point> _traced = [];

    // The curve through the bands' thumbs, from where each one is once laid
    // out - which is wherever Fluent's track puts it, ends included - so a
    // drag moves the curve with the thumb. Set only when a thumb has moved,
    // since setting it lays the window out again.
    private void TraceCurve()
    {
        var points = new List<Point>();
        foreach (var thumb in BandList.GetVisualDescendants().OfType<Thumb>())
        {
            if (thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), Curve) is { } centre)
                points.Add(centre);
        }
        if (points.SequenceEqual(_traced))
            return;
        _traced = points;
        Curve.Data = SmoothCurve(points);
    }

    // A monotone cubic through the points (Fritsch-Carlson): smooth at each
    // one, flat at a band higher or lower than both its neighbours, and never
    // swinging past a thumb the way a plain spline would between a boost and
    // a cut.
    private static StreamGeometry SmoothCurve(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        if (points.Count < 2)
            return geometry;

        var n = points.Count;
        var slopes = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
            slopes[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);

        // Each point's tangent: the harmonic mean of the slopes either side,
        // or flat where they disagree in sign; the ends follow their one slope.
        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            var (before, after) = (slopes[i - 1], slopes[i]);
            tangents[i] = before * after <= 0 ? 0 : 2 * before * after / (before + after);
        }

        using var context = geometry.Open();
        context.BeginFigure(points[0], isFilled: false);
        for (var i = 0; i < n - 1; i++)
        {
            var third = (points[i + 1].X - points[i].X) / 3;
            context.CubicBezierTo(
                new Point(points[i].X + third, points[i].Y + tangents[i] * third),
                new Point(points[i + 1].X - third, points[i + 1].Y - tangents[i + 1] * third),
                points[i + 1]);
        }
        context.EndFigure(isClosed: false);
        return geometry;
    }

    private void Reset_Click(object? sender, RoutedEventArgs e) => (DataContext as EqualizerViewModel)?.Reset();
}
