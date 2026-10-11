using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

// UniformRowPanel with HasTallRows - the desktop's Albums grid, whose row grows
// when its album is opened in place. Every row is placed by arithmetic rather
// than by being laid out after the one above it, so what these hold is that
// the arithmetic counts the taller row: the rows under it start where it
// ends, the list is as long as its rows, and a taller row that shrinks while
// scrolled out of view gives its height back.
public class UniformRowPanelTests
{
    private const double Height = 50;

    private sealed class Item(double height) : INotifyPropertyChanged
    {
        private double _height = height;

        public double Height
        {
            get => _height;
            set
            {
                _height = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static (Window Window, ScrollViewer Scroller, ItemsControl List) Show(List<Item> items)
    {
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformRowPanel { HasTallRows = true }),
            ItemTemplate = new FuncDataTemplate<Item>((_, _) =>
            {
                var row = new Border();
                row.Bind(Avalonia.Layout.Layoutable.HeightProperty, new Binding(nameof(Item.Height)));
                return row;
            }),
        };
        var scroller = new ScrollViewer { Content = list };
        var window = new Window { Width = 400, Height = 500 };
        window.Styles.Add(new FluentTheme());
        window.Content = scroller;
        window.Show();
        Settle(window);
        return (window, scroller, list);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static double TopOf(ItemsControl list, int index) =>
        list.ContainerFromIndex(index)!.TranslatePoint(default, list.ItemsPanelRoot!)!.Value.Y;

    private static List<Item> Items() =>
        Enumerable.Range(0, 100).Select(i => new Item(i == 3 ? 300 : Height)).ToList();

    [AvaloniaFact]
    public void The_rows_under_a_taller_row_start_where_it_ends()
    {
        var (window, scroller, list) = Show(Items());

        Assert.Equal(3 * Height, TopOf(list, 3));
        Assert.Equal(3 * Height + 300, TopOf(list, 4));
        Assert.Equal(3 * Height + 300 + Height, TopOf(list, 5));
        Assert.Equal(99 * Height + 300, scroller.Extent.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Rows_far_below_a_taller_row_are_placed_past_it()
    {
        var (window, scroller, list) = Show(Items());

        scroller.Offset = new Vector(0, 60 * Height + 250);
        Settle(window);

        Assert.Equal(60 * Height + 250, TopOf(list, 60));
        window.Close();
    }

    [AvaloniaFact]
    public void A_taller_row_shrinking_out_of_view_gives_its_height_back()
    {
        var items = Items();
        var (window, scroller, list) = Show(items);
        scroller.Offset = new Vector(0, 60 * Height + 250);
        Settle(window);

        // Its album closed while the list was scrolled away from it.
        items[3].Height = Height;
        Settle(window);

        Assert.Equal(100 * Height, scroller.Extent.Height);
        Assert.Equal(70 * Height, TopOf(list, 70));
        window.Close();
    }
}
