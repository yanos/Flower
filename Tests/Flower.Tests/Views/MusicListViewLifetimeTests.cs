using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Views;

// ColumnManager is one instance for the whole app and outlives every track
// list showing it. A list that subscribed to it for good was kept alive by it,
// and went on rebuilding its header and every pooled row on each column change
// after its window had closed - one more of them for each list made and
// thrown away. In this suite that made resetting the column order cost half a
// second and climb from test to test. A list listens only while on screen now,
// and catches up on what it missed when it comes back.
[Collection("PlatformDataDirectory")]
public class MusicListViewLifetimeTests : PinnedDataDirectory
{
    public MusicListViewLifetimeTests() => TestIoc.EnsureConfigured();

    private static ColumnManager Columns => Ioc.Default.GetService<ColumnManager>()!;

    // BuildHeader replaces the header's row of cells, so its identity says
    // whether the header was rebuilt.
    private static Control HeaderCells(MusicListView view) =>
        ((Panel)view.FindControl<Border>("HeaderBorder")!.Child!).Children[0];

    private static void MoveFirstColumnLast()
    {
        var first = Columns.VisibleColumns.First();
        Columns.Reorder(first, Columns.VisibleColumns.Count() - 1);
    }

    [AvaloniaFact]
    public void A_closed_list_ignores_column_changes_and_catches_up_when_shown_again()
    {
        var view = new MusicListView();
        view.SetItems(Enumerable.Range(0, 5)
            .Select(i => new TrackRowViewModel { Track = new Track { Title = $"Track {i}", Path = $"/m/{i}.mp3" } })
            .ToList());
        var window = new Window { Width = 1400, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var shown = HeaderCells(view);
        MoveFirstColumnLast();
        var rebuilt = HeaderCells(view);
        Assert.NotSame(shown, rebuilt);

        window.Close();
        window.Content = null;
        MoveFirstColumnLast();
        Assert.Same(rebuilt, HeaderCells(view));

        var again = new Window { Width = 1400, Height = 600, Content = view };
        again.Show();
        Assert.NotSame(rebuilt, HeaderCells(view));
        again.Close();

        for (int i = 0; i < Columns.Columns.Count; i++)
            Columns.Columns[i].Order = i;
    }
}
