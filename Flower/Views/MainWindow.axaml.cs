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
    }
}
