using Avalonia.Controls;
using Avalonia.Interactivity;

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

        private void Star_Click(object? sender, RoutedEventArgs e) =>
            (DataContext as CurrentlyPlayingControlViewModel)?.ToggleStar();
    }
}
