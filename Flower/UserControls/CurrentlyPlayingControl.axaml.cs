using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - MainView.axaml
    // binds it to MainViewModel.NowPlaying. Nothing here reaches into the
    // container: see docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    public partial class CurrentlyPlayingControl : UserControl
    {
        public CurrentlyPlayingControl()
        {
            InitializeComponent();
        }

        private CurrentlyPlayingControlViewModel? ViewModel => DataContext as CurrentlyPlayingControlViewModel;

        private void Shuffle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ViewModel?.ToggleShuffle();
        }

        private void Repeat(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ViewModel?.ToggleRepeat();
        }
    }
}
