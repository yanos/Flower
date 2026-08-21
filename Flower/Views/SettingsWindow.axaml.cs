using Avalonia.Controls;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The desktop frame around SettingsPanel. Everything this window used to do
// itself - the tabs, the folder list, the iTunes toggles, the device list, OK and
// Cancel - now lives in SettingsPanel and SettingsViewModel, because the server's
// browser UI renders the same screen and Avalonia has no second Window there to
// put it in.
public partial class SettingsWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) - never
    // called directly; the real constructor below is what's actually used.
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(MainViewModel viewModel) : this()
    {
        var backend = new LocalSettingsBackend(viewModel);
        Panel.Initialize(new SettingsViewModel(backend), viewModel);
        Panel.CloseRequested += (_, _) => Close();
        NativeMenuHelper.InheritFromMainWindow(this);
    }
}
