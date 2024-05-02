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
    }
}
