using Avalonia.Controls;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Manager;
using Flower.Models;
using Flower.ViewModels;

namespace Flower.Views;

public partial class MainView : UserControl
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;

    public MainView()
    {
        InitializeComponent();

        _playlistControlViewModel = Ioc.Default.GetService<PlaylistControlViewModel>()!;
    }

    private void DataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is DataGrid dataGrid)
        { 
            if (dataGrid.SelectedItem is Track selectedTrack)
            {
                _playlistControlViewModel.Play(selectedTrack);
                e.Handled = true;
            }
        }
    }
}
