using Avalonia.Controls;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.ViewModels;

namespace Flower.UserControls
{
    public partial class CurrentlyPlayingControl : UserControl
    {
        private readonly CurrentlyPlayingControlViewModel _vm;

        public CurrentlyPlayingControl()
        {
            InitializeComponent();

            _vm = Ioc.Default.GetService<CurrentlyPlayingControlViewModel>()!;
            DataContext = _vm;
        }

        private void Shuffle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _vm.ToggleShuffle();
        }

        private void Repeat(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _vm.ToggleRepeat();
        }
    }
}
