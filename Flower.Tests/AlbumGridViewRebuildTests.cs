using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// AlbumGridView hands its row chunks to a plain ItemsControl over a
// VirtualizingStackPanel. It used to reuse one List for the control's lifetime,
// refilling it in place and poking the ItemsControl with
// `ItemsSource = null; ItemsSource = _rows` - which does not reliably re-realize
// anything, because Avalonia resolves a collection to an ItemsSourceView per
// instance and handing the same instance back can read as "nothing changed".
//
// The visible failure: remove the last library folder and the Albums/Recently
// Added grid keeps showing every album, with the status bar correctly reading
// 0 songs beside it, until a window resize forces a fresh layout pass. The
// realization itself can't be asserted here (a virtualizing panel needs a real
// render pass, which the headless platform does not do), so what these pin is
// the invariant the fix rests on: every rebuild hands over a *different* list.
public class AlbumGridViewRebuildTests
{
    public AlbumGridViewRebuildTests() => TestIoc.EnsureConfigured();

    private static ObservableCollection<AlbumTileViewModel> Tiles(int count) =>
        new(Enumerable.Range(0, count).Select(i => new AlbumTileViewModel
        {
            Name = $"Album {i}",
            RepresentativeTrack = new Track { Path = $"/music/{i}.mp3", Album = $"Album {i}" },
            Tracks = [],
        }));

    private static (AlbumGridView Grid, ItemsControl Rows) Show()
    {
        var grid = new AlbumGridView();
        var window = new Window { Width = 900, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        grid.UpdateLayout();
        return (grid, grid.GetControl<ItemsControl>("RowsList"));
    }

    [AvaloniaFact]
    public void Emptying_the_tiles_hands_the_items_control_a_new_empty_list()
    {
        var (grid, rowsList) = Show();
        grid.ItemsSource = Tiles(40);
        var populated = rowsList.ItemsSource;
        Assert.NotEmpty(populated!.Cast<object>());

        grid.ItemsSource = new ObservableCollection<AlbumTileViewModel>();

        Assert.NotSame(populated, rowsList.ItemsSource);
        Assert.Empty(rowsList.ItemsSource!.Cast<object>());
    }

    // Not only the empty case - every rebuild has to be a fresh instance, or
    // the same staleness shows up as a grid that keeps the *previous* view's
    // albums when the library changes under it.
    [AvaloniaFact]
    public void Every_rebuild_hands_over_a_different_list()
    {
        var (grid, rowsList) = Show();
        grid.ItemsSource = Tiles(40);
        var first = rowsList.ItemsSource;
        var firstRowCount = first!.Cast<object>().Count();

        grid.ItemsSource = Tiles(12);

        Assert.NotSame(first, rowsList.ItemsSource);
        Assert.True(rowsList.ItemsSource!.Cast<object>().Count() < firstRowCount);
    }
}
