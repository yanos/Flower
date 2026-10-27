using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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

    // The line through the bands' thumbs, from where each one is once laid
    // out - which is wherever Fluent's track puts it, ends included - so a
    // drag moves the line with the thumb. Set only when it has moved, since
    // setting it lays the window out again.
    private void TraceCurve()
    {
        var points = new List<Point>();
        foreach (var thumb in BandList.GetVisualDescendants().OfType<Thumb>())
        {
            if (thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), Curve) is { } centre)
                points.Add(centre);
        }
        if (Curve.Points is { } traced && points.SequenceEqual(traced))
            return;
        Curve.Points = points;
    }

    private void Reset_Click(object? sender, RoutedEventArgs e) => (DataContext as EqualizerViewModel)?.Reset();
}
