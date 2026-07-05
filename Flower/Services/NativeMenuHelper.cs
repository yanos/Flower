using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Flower.Services;

// NativeMenu (the macOS menu bar) is resolved per-window, not app-wide - only
// MainWindow declares one (see MainWindow.axaml). Without this, focusing any
// other window (Settings, Track Info, a confirm dialog, ...) blanks the menu
// bar down to a bare "Avalonia Application" fallback with no other menus,
// since that window has never had a NativeMenu of its own.
public static class NativeMenuHelper
{
    public static void InheritFromMainWindow(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow } &&
            !ReferenceEquals(mainWindow, window))
            NativeMenu.SetMenu(window, NativeMenu.GetMenu(mainWindow));
    }
}
