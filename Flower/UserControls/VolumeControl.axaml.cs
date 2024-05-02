using Avalonia.Controls;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.ViewModels;

namespace Flower.UserControls
{
    public partial class VolumeControl : UserControl
    {
        private readonly VolumeControlViewModel _vm;

        public VolumeControl()
        {
            InitializeComponent();

            _vm = Ioc.Default.GetService<VolumeControlViewModel>()!;
            DataContext = _vm;
        }
    }
}
