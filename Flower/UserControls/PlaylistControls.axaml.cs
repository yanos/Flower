using Avalonia.Controls;
using Avalonia.Data.Converters;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Manager;
using Flower.ViewModels;

namespace Flower.UserControls
{
    public partial class PlaylistControls : UserControl
    {
        private readonly PlaylistControlViewModel _vm;
        private readonly MainViewModel _mainViewModel;

        public PlaylistControls()
        {
            InitializeComponent();

            _vm = Ioc.Default.GetService<PlaylistControlViewModel>()!;
            _mainViewModel = Ioc.Default.GetService<MainViewModel>()!;
            DataContext = _vm;
        }

        // Routed through MainViewModel rather than _vm.PlayOrPause() directly so a
        // fresh play (nothing currently playing/paused) snapshots the queue from
        // whatever's currently displayed in MainView - see
        // MainViewModel.PlayOrPauseFromCurrentView.
        private void PlayOrPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _mainViewModel.PlayOrPauseFromCurrentView();
        }

        private void Next(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _vm.Next();
        }

        private void Previous(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _vm.Previous();
        }
    }
}
