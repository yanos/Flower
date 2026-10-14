using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Services;
using Flower.ViewModels;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - MainView.axaml
    // binds it to MainViewModel.OutputDevice. Same arrangement as
    // VolumeControl, which sits right beside it in the player bar.
    public partial class OutputDeviceControl : UserControl
    {
        public OutputDeviceControl()
        {
            InitializeComponent();
        }

        // The device list, as a menu: one item per device, the one in use
        // ticked - the tick is IAudioManager's own answer, not which item was
        // last clicked. Devices are re-enumerated on every open rather than
        // watched for hotplug - see OutputDeviceControlViewModel.Refresh.
        private void PickerButton_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not OutputDeviceControlViewModel viewModel)
                return;

            viewModel.Refresh();

            // Upward, for Avalonia's own menu: the button is in the player
            // bar, which can sit at the very bottom of the window.
            var menu = new ContextMenu { Placement = PlacementMode.Top };
            foreach (var device in viewModel.Devices)
            {
                var item = new MenuItem
                {
                    Header = device.Name,
                    Icon = device.IsSelected ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 12, Height = 12 } : null,
                };
                item.Click += (_, _) => viewModel.Select(device);
                menu.Items.Add(item);
            }

            PlatformContextMenu.Open(menu, PickerButton);
        }
    }
}
