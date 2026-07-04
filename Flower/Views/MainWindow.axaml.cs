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

        // The gestures shown in the native menu bar must match MainView's actual
        // key handling (Cmd on macOS, Ctrl elsewhere), so set them here instead
        // of static XAML "Cmd+..." strings.
        var topLevelItems = NativeMenu.GetMenu(this)?.Items
            .OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu?.Items ?? Enumerable.Empty<NativeMenuItemBase>())
            .OfType<NativeMenuItem>()
            .ToList() ?? [];

        var settingsItem = topLevelItems.FirstOrDefault(item => item.Header == "Settings…");
        if (settingsItem != null)
            settingsItem.Gesture = new KeyGesture(Key.OemComma, PlatformShortcuts.Primary);

        var selectColumnsItem = topLevelItems.FirstOrDefault(item => item.Header == "Select Columns…");
        if (selectColumnsItem != null)
            selectColumnsItem.Gesture = new KeyGesture(Key.J, PlatformShortcuts.Primary);

        // Mirrors the standard macOS Window > Zoom behavior: resizes/repositions
        // the window to fill the available screen space, toggling back to its
        // prior size/position on a second click. This is WindowState.Maximized,
        // not WindowState.FullScreen - the latter animates into its own macOS
        // Space and hides the menu bar, which isn't what "Zoom" does.
        var zoomItem = topLevelItems.FirstOrDefault(item => item.Header == "Zoom");
        if (zoomItem != null)
            zoomItem.Click += (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
