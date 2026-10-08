using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Controls;

// Feeds a Home view's virtualizing list its rows (HomeShelfRows): works out
// the column count from the list's own width, and re-chunks the shelves when
// that count changes or Home rebuilds them - not on every pixel of a resize,
// which between two column counts only re-sizes the rows already on screen.
//
// The list's DataContext is the HomeViewModel. Each rebuild hands the list a
// new collection rather than refilling one: Avalonia keys a source's view on
// the instance, and a virtualizing panel handed the same one back can keep
// containers it had realized - see AlbumGridView.RebuildRows.
//
// A cover that moves when Home rebuilds slides there rather than jumping:
// an album picked up from Continue Playing going to the front, the covers it
// passes pushed along one place each, wrapping onto the next row. Every cover
// on screen is found by what it shows before the rows are replaced, and again
// once the new ones are laid out, and each is drawn back at its old place and
// eased into its new one. A change of column count is a resize, where every
// cover moves at once, so it does not animate.
public sealed class HomeShelfRowsHost
{
    // Quick, so the shelf has settled before the eye has finished following
    // it; ScreenStackPanel and SlidingSheet take 280 to cross a whole screen.
    private const double SlideDurationMs = 260;

    private readonly ItemsControl _list;
    private readonly Func<double, int> _columnsFor;
    private HomeViewModel? _home;
    private int _columns;

    private IDisposable? _sliding;
    private readonly List<Control> _moved = new();

    // The clock the slide runs on - a test hands in one it steps by hand, as
    // SlidingSheet's does.
    internal AnimationClock? ClockOverride { get; set; }

    private AnimationClock Clock => ClockOverride ?? AnimationClock.Current;

    public HomeShelfRowsHost(ItemsControl list, Func<double, int> columnsFor)
    {
        _list = list;
        _columnsFor = columnsFor;
        list.SizeChanged += (_, e) =>
        {
            var columns = _columnsFor(e.NewSize.Width);
            if (columns != _columns)
                Rebuild(columns);
        };
        list.DataContextChanged += (_, _) => Attach(list.DataContext as HomeViewModel);
        Attach(list.DataContext as HomeViewModel);
    }

    // The desktop's column count: as many of the Albums grid's cells as fit
    // across, at least one - AlbumGridView's own arithmetic.
    public static int DesktopColumnsFor(double width, double cellWidth) =>
        Math.Max(1, double.IsFinite(width) && width > 0 ? (int)(width / cellWidth) : 1);

    private void Attach(HomeViewModel? home)
    {
        if (ReferenceEquals(home, _home))
            return;
        if (_home != null)
            _home.ShelvesChanged -= OnShelvesChanged;
        _home = home;
        if (_home != null)
            _home.ShelvesChanged += OnShelvesChanged;
        Rebuild(_columns > 0 ? _columns : _columnsFor(_list.Bounds.Width));
    }

    private void OnShelvesChanged(object? sender, EventArgs e) => Rebuild(_columns > 0 ? _columns : _columnsFor(_list.Bounds.Width));

    private void Rebuild(int columns)
    {
        // Where each cover is drawn now, mid-slide included, so a rebuild
        // landing during one carries on from where the covers are.
        var before = columns == _columns && _list.IsAttachedToVisualTree() ? CoverPositions() : null;
        StopSliding();

        _columns = columns;
        _list.ItemsSource = _home == null ? null : HomeShelfRows.Build(_home, columns);

        if (before is { Count: > 0 })
            SlideFromAfterLayout(before);
    }

    // Each cover on screen by what it shows - a Continue Playing item, or an
    // album tile - which is the same object across a rebuild (HomeViewModel
    // reuses them) though its container is not. A cover is the container its
    // row's grid holds. A row the list has recycled is kept hidden, still
    // holding what it last showed, and says nothing about where that is.
    private Dictionary<object, (Control Cover, Point At)> CoverPositions()
    {
        var covers = new Dictionary<object, (Control, Point)>();
        foreach (var grid in _list.GetVisualDescendants().OfType<UniformGrid>())
        {
            if (!grid.IsEffectivelyVisible)
                continue;

            foreach (var cover in grid.Children)
            {
                if (cover.DataContext is { } shown && cover.TranslatePoint(default, _list) is { } at)
                    covers[shown] = (cover, at);
            }
        }
        return covers;
    }

    // Once, after the layout pass that realizes the new rows and before they
    // are first drawn, so no cover is ever seen at its new place first.
    private void SlideFromAfterLayout(Dictionary<object, (Control Cover, Point At)> before)
    {
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            _list.LayoutUpdated -= OnLayoutUpdated;
            SlideFrom(before);
        }

        _list.LayoutUpdated += OnLayoutUpdated;
    }

    private void SlideFrom(Dictionary<object, (Control Cover, Point At)> before)
    {
        var slides = new List<(TranslateTransform Offset, Vector From)>();
        Control? farthest = null;
        var farthestDistance = 0.0;

        foreach (var (shown, (cover, at)) in CoverPositions())
        {
            if (!before.TryGetValue(shown, out var was))
                continue;

            Vector from = was.At - at;
            if (Math.Abs(from.X) < 0.5 && Math.Abs(from.Y) < 0.5)
                continue;

            var offset = new TranslateTransform(from.X, from.Y);
            cover.RenderTransform = offset;
            _moved.Add(cover);
            slides.Add((offset, from));

            var distance = from.Length;
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthest = cover;
            }
        }

        if (slides.Count == 0)
            return;

        // The cover going farthest is the one the eye follows - the album
        // moving to the front - so it passes over the covers it pushes
        // along, not under them. Each row is a container of its own in the
        // list, so its row is lifted over the others too.
        if (farthest != null)
        {
            Lift(farthest);
            if (RowOf(farthest) is { } row)
                Lift(row);
        }

        _sliding = Clock.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / SlideDurationMs);
            var left = 1 - EaseOut(t);
            foreach (var (offset, from) in slides)
            {
                offset.X = from.X * left;
                offset.Y = from.Y * left;
            }

            if (t >= 1.0)
                StopSliding();
        });
    }

    private void Lift(Control control)
    {
        control.ZIndex = 1;
        _moved.Add(control);
    }

    // The container in the list's own panel that holds this cover's row.
    private Control? RowOf(Control cover)
    {
        var panel = _list.ItemsPanelRoot;
        Visual? at = cover;
        while (at != null && at.GetVisualParent() is { } parent)
        {
            if (ReferenceEquals(parent, panel))
                return at as Control;
            at = parent;
        }
        return null;
    }

    // Ends a slide, in flight or not, with every cover where layout put it.
    private void StopSliding()
    {
        _sliding?.Dispose();
        _sliding = null;
        foreach (var control in _moved)
        {
            control.RenderTransform = null;
            control.ZIndex = 0;
        }
        _moved.Clear();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
