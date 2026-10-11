using System.Windows.Input;

using Avalonia.Controls;

using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class ArtistAlbumGridScreenView : UserControl, IAlbumTileHost
{
    public ArtistAlbumGridScreenView()
    {
        InitializeComponent();
    }

    public ICommand? OpenAlbumCommand => (DataContext as MobileMainViewModel)?.SelectArtistAlbumCommand;
}
