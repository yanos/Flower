using System.Windows.Input;

using Avalonia.Controls;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class HomeScreenView : UserControl, IAlbumTileHost, ITrackRowHost
{
    private readonly HomeShelfRowsHost _rows;

    public HomeScreenView()
    {
        InitializeComponent();
        // The width the Albums grid measures (AlbumGridColumnSizing): the
        // list's own, its padding included.
        _rows = new HomeShelfRowsHost(Rows, AlbumGridRow.ColumnsFor);
    }

    public ICommand? OpenAlbumCommand => (DataContext as MobileMainViewModel)?.SelectHomeAlbumCommand;

    // Recently Played's rows are songs from anywhere, so they carry their
    // artist and are neither an album's nor a playlist's.
    public bool IsAlbumMode => false;
    public bool IsPlaylistMode => false;
    public bool ShowsRowArtist => true;
}
