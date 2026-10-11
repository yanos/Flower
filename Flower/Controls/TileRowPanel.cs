using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Flower.Controls;

// One row of album covers in a grid that virtualizes by the row - the phone's
// Albums grid and an artist's, and Home's shelves on both heads - laid out the
// way a UniformGrid lays them out: Columns cells across, each as wide as the
// row allows, and as tall as the tallest cover. Items that outrun Columns
// wrap onto another line of cells.
//
// It is an ItemsControl over a UniformGrid that this replaces, and the
// difference is what happens when the list hands the row a new set of albums
// to show, which it does for every row that scrolls in: an ItemsControl given
// a new ItemsSource clears its panel and fills it again, so every cover left
// the visual tree and joined it again - a new composition visual and every
// inherited value pushed down again, for each part of each cover, which was
// most of what a fling down the Albums grid allocated. Here each cover is
// built once from ItemTemplate, stays where it is, and is only handed the next
// album as its DataContext; a row with fewer albums than cells hides the
// covers it has spare rather than removing them.
//
// Each cover sits in a Decorator of its own, as an ItemsControl's would in its
// container, so there is something to move a cover by that is not the cover:
// Home slides covers to their new places (HomeShelfRowsHost) by a
// RenderTransform, and a Button - which every cover is - animates its own
// RenderTransform, so an offset set on one was taken over by its transition
// before it was ever drawn.
public sealed class TileRowPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<TileRowPanel, int>(nameof(Columns), 1);

    public static readonly StyledProperty<IReadOnlyList<object>?> ItemsProperty =
        AvaloniaProperty.Register<TileRowPanel, IReadOnlyList<object>?>(nameof(Items));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<TileRowPanel, IDataTemplate?>(nameof(ItemTemplate));

    static TileRowPanel()
    {
        AffectsMeasure<TileRowPanel>(ColumnsProperty);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IReadOnlyList<object>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemTemplateProperty)
        {
            // Covers built from another template are no use to this one.
            Children.Clear();
            Show();
        }
        else if (change.Property == ItemsProperty)
        {
            Show();
        }
    }

    private void Show()
    {
        var items = Items;
        var count = items?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var item = items![i];
            if (i < Children.Count)
            {
                var cover = Children[i];
                cover.DataContext = item;
                cover.IsVisible = true;
            }
            else if (Build(item) is { } cover)
            {
                cover.DataContext = item;
                Children.Add(cover);
            }
        }

        // Kept for the next row with more albums, but showing none, and
        // holding none.
        for (var i = count; i < Children.Count; i++)
        {
            Children[i].IsVisible = false;
            Children[i].DataContext = null;
        }

        InvalidateMeasure();
    }

    private Control? Build(object item) =>
        (ItemTemplate ?? this.FindDataTemplate(item))?.Build(item) is { } cover ? new Decorator { Child = cover } : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var cellWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        double widest = 0, tallest = 0;
        var shown = 0;
        foreach (var cover in Children)
        {
            if (!cover.IsVisible)
                continue;
            cover.Measure(new Size(cellWidth, double.PositiveInfinity));
            widest = Math.Max(widest, cover.DesiredSize.Width);
            tallest = Math.Max(tallest, cover.DesiredSize.Height);
            shown++;
        }

        var lines = Math.Max(1, (shown + columns - 1) / columns);
        var width = double.IsInfinity(cellWidth) ? widest * columns : availableSize.Width;
        return new Size(width, shown == 0 ? 0 : lines * tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var cellWidth = finalSize.Width / columns;
        double tallest = 0;
        foreach (var cover in Children)
        {
            if (cover.IsVisible)
                tallest = Math.Max(tallest, cover.DesiredSize.Height);
        }

        var index = 0;
        foreach (var cover in Children)
        {
            if (!cover.IsVisible)
                continue;
            cover.Arrange(new Rect(index % columns * cellWidth, index / columns * tallest, cellWidth, tallest));
            index++;
        }
        return finalSize;
    }
}
