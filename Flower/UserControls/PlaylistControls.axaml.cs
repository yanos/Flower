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

        public PlaylistControls()
        {
            InitializeComponent();

            _vm = Ioc.Default.GetService<PlaylistControlViewModel>()!;
            DataContext = _vm;
        }

        private void PlayOrPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _vm.PlayOrPause();
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
