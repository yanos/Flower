using System.Windows.Input;

using Avalonia.Controls;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class HomeScreenView : UserControl, IAlbumTileHost
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
}
