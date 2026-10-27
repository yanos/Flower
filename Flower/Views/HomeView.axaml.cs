using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.ViewModels;

namespace Flower.Views;

public partial class HomeView : UserControl
{
    // The Albums grid's cell: a 180 tile and its 8 either side.
    private const double CellWidth = 180 + 16;

    private readonly HomeShelfRowsHost _rows;

    // For a test to step the covers' slide by hand.
    internal HomeShelfRowsHost ShelfRows => _rows;

    public HomeView()
    {
        InitializeComponent();
        _rows = new HomeShelfRowsHost(Rows, width => HomeShelfRowsHost.DesktopColumnsFor(width, CellWidth),
            songColumns: 2, historyLink: true);
    }

    // A Recently Played song's menu, from its "..." or a right-click - the
    // song menu MainView builds for the track list.
    private void Song_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: TrackRowViewModel row } song)
        {
            this.FindAncestorOfType<MainView>()?.OpenRecentlyPlayedMenu(row, song);
            e.Handled = true;
        }
    }

    private void SongMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrackRowViewModel row } button)
            this.FindAncestorOfType<MainView>()?.OpenRecentlyPlayedMenu(row, button);
    }

    private void Song_DownloadRequested(object? sender, DownloadIndicatorViewModel indicator)
    {
        if (indicator is TrackRowViewModel row && this.FindDataContext<MainViewModel>() is { } vm)
            _ = vm.Downloads.DownloadRowAsync(row);
    }
}
