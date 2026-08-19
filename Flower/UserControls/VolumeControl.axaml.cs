using Avalonia.Controls;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - MainView.axaml
    // binds it to MainViewModel.Volume. Nothing here reaches into the
    // container: see docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    public partial class VolumeControl : UserControl
    {
        public VolumeControl()
        {
            InitializeComponent();
        }
    }
}
