using System.Linq;

using Avalonia.Controls;
using Avalonia.Input;

using Flower.Services;

namespace Flower.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The gesture shown for "Settings…" in the native menu bar must match
        // MainView's actual key handling (Cmd on macOS, Ctrl elsewhere), so set
        // it here instead of a static XAML "Cmd+OemComma" string.
        var settingsItem = NativeMenu.GetMenu(this)?.Items
            .OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu?.Items ?? Enumerable.Empty<NativeMenuItemBase>())
            .OfType<NativeMenuItem>()
            .FirstOrDefault(item => item.Header == "Settings…");

        if (settingsItem != null)
            settingsItem.Gesture = new KeyGesture(Key.OemComma, PlatformShortcuts.Primary);
    }
}
