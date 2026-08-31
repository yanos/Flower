using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - MainView.axaml
    // binds it to MainViewModel.OutputDevice. Same arrangement as
    // VolumeControl, which sits right beside it in the status bar.
    public partial class OutputDeviceControl : UserControl
    {
        public OutputDeviceControl()
        {
            InitializeComponent();
        }

        // Devices are re-enumerated on every open rather than watched for
        // hotplug - see OutputDeviceControlViewModel.Refresh.
        private void FlyoutOpening(object? sender, System.EventArgs e)
        {
            (DataContext as OutputDeviceControlViewModel)?.Refresh();
        }

        private void DeviceClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: OutputDeviceItemViewModel device }
                && DataContext is OutputDeviceControlViewModel viewModel)
            {
                viewModel.Select(device);
            }

            PickerButton.Flyout?.Hide();
        }
    }
}
