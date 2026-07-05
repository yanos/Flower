using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _appSettings;
    private readonly ColumnManager _columnManager;
    private readonly Library _library;

    // Tracks the window's bounds while in WindowState.Normal, since that's
    // what should be restored on the next launch - saving the bounds reported
    // while maximized/minimized would lose the size the user actually set.
    private double     _normalWidth;
    private double     _normalHeight;
    private PixelPoint _normalPosition;

    public MainWindow()
    {
        InitializeComponent();

        _appSettings    = Ioc.Default.GetService<AppSettings>()!;
        _columnManager  = Ioc.Default.GetService<ColumnManager>()!;
        _library        = Ioc.Default.GetService<Library>()!;
        _normalWidth    = Width;
        _normalHeight   = Height;
        _normalPosition = Position;

        RestoreWindowGeometry();

        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
                _normalPosition = e.Point;
        };
        Resized += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalWidth  = e.ClientSize.Width;
                _normalHeight = e.ClientSize.Height;
            }
        };
        Closing += (_, _) =>
        {
            SaveWindowGeometry();
            _columnManager.Flush();

            // A track that just naturally finished (PlaylistControlViewModel.EndReached
            // increments PlayCount and kicks off a fire-and-forget SaveAsync) may not
            // have hit disk yet - flush the in-memory state synchronously so quitting
            // right after a play doesn't silently lose that increment. See LibraryStore.Save.
            new LibraryStore().Save(_library.Tracks);
        };

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

    private void RestoreWindowGeometry()
    {
        if (_appSettings.WindowWidth is double w && _appSettings.WindowHeight is double h)
        {
            Width         = w;
            Height        = h;
            _normalWidth  = w;
            _normalHeight = h;
        }

        // Only restore the saved position if it still lands on a currently
        // connected screen - otherwise (e.g. an external monitor got
        // unplugged) the window would open off-screen and be unreachable.
        if (_appSettings.WindowX is double x && _appSettings.WindowY is double y)
        {
            var candidate = new PixelPoint((int)x, (int)y);
            if (Screens.All.Any(s => s.Bounds.Contains(candidate)))
            {
                Position        = candidate;
                _normalPosition = candidate;
            }
        }

        if (_appSettings.WindowIsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowGeometry()
    {
        // _normalWidth/_normalHeight are seeded from Width/Height at
        // construction time, which read as NaN until the first layout pass
        // resolves a real size - if the window closes before any Resized
        // event ever fires (e.g. closed within the first instant), they'd
        // still be NaN here. NaN/Infinity can't be written as JSON, so only
        // persist them if they ended up finite; otherwise leave whatever was
        // already saved (or nothing, on a first run) rather than crashing.
        if (double.IsFinite(_normalWidth) && double.IsFinite(_normalHeight))
        {
            _appSettings.WindowWidth  = _normalWidth;
            _appSettings.WindowHeight = _normalHeight;
        }

        _appSettings.WindowX           = _normalPosition.X;
        _appSettings.WindowY           = _normalPosition.Y;
        _appSettings.WindowIsMaximized = WindowState == WindowState.Maximized;

        new AppSettingsStore().Save(_appSettings);
    }
}
