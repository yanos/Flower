using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

using CommunityToolkit.Mvvm.DependencyInjection;

using Material.Icons;

using Microsoft.Extensions.Logging;

using Flower.Controls;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _appSettings;
    private readonly ColumnManager _columnManager;
    private readonly AppSettingsStore _appSettingsStore;

    // Tracks the window's bounds while in WindowState.Normal, since that's
    // what should be restored on the next launch - saving the bounds reported
    // while maximized/minimized would lose the size the user actually set.
    private double     _normalWidth;
    private double     _normalHeight;
    private PixelPoint _normalPosition;

    // Parameterless for Avalonia's XAML compiler, which resolves a constructor
    // by arity - it does not consider optional parameters, and this window has
    // its own .axaml. Every real construction goes through the overload below,
    // from App.Bootstrap, which is where the container lives.
    // See docs/ARCHITECTURE-REVIEW.md Tier 2.3.
#pragma warning disable CS8618
    public MainWindow() : this(null, null, null) { }
#pragma warning restore CS8618

    public MainWindow(
        AppSettings? appSettings,
        ColumnManager? columnManager,
        AppSettingsStore? appSettingsStore)
    {
        InitializeComponent();

        _appSettings    = appSettings!;
        _columnManager  = columnManager!;
        _appSettingsStore = appSettingsStore!;
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
            // Captures whichever sidebar view + scroll position is showing
            // right now, synchronously (MainViewModel.SaveLastView), same
            // "must complete before the process actually exits" reasoning as
            // SaveWindowGeometry below - restored on the next launch by
            // MainViewModel.BuildSidebarItems/MainView.axaml.cs's own
            // SeedRestoredViewState.
            //
            // Referenced by name (MainContent), not Content as MainView - the
            // Window's actual Content is the DockPanel wrapping both MainView
            // and NativeMenuBar (added for Windows/Linux's in-window menu
            // fallback - see MainWindow.axaml), so that cast always silently
            // evaluated to null via ?. and this line never actually ran,
            // meaning the sidebar view/scroll position was never once saved.
            MainContent.SaveCurrentViewState();
            SaveWindowGeometry();
            _columnManager.Flush();

            // No library save here any more. This existed to catch a play
            // count that a debounced stats write had not flushed yet, and it
            // paid for two rows with a whole-library upsert; Library writes
            // every mutation through on the spot now (see its ITrackStore), so
            // by the time we get here there is nothing outstanding to rescue.
            // iOS and Android never had this hook to begin with, which is the
            // other half of why the deferred write had to go.

            // Serilog buffers writes - flush them so the last few lines of this
            // session (often the most useful ones, if something just went wrong)
            // aren't lost the same way library.json saves used to be.
            AppLogging.Shutdown();
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

        WireMenuBar(topLevelItems);

        // Mirrors the standard macOS Window > Zoom behavior: resizes/repositions
        // the window to fill the available screen space, toggling back to its
        // prior size/position on a second click. This is WindowState.Maximized,
        // not WindowState.FullScreen - the latter animates into its own macOS
        // Space and hides the menu bar, which isn't what "Zoom" does.
        var zoomItem = topLevelItems.FirstOrDefault(item => item.Header == "Zoom");
        if (zoomItem != null)
            zoomItem.Click += (_, _) =>
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // On macOS "About Flower" and "Settings…" live in the application menu
        // (App.axaml's NativeMenu, shown under the bold app-name title). The
        // in-window NativeMenuBar that Windows/Linux get only renders this
        // window's menu, so mirror that application menu here as a leading
        // "Flower" menu - otherwise About/Settings would be unreachable from
        // any menu on those platforms.
        if (!System.OperatingSystem.IsMacOS())
        {
            var windowMenu = NativeMenu.GetMenu(this);
            if (windowMenu != null)
            {
                var aboutItem = new NativeMenuItem("About Flower");
                _menuIcons.Add((aboutItem, MaterialIconKind.InformationOutline));
                aboutItem.Click += (_, _) => new AboutWindow().Show();

                var settingsMenuItem = new NativeMenuItem("Settings…")
                {
                    Gesture = new KeyGesture(Key.OemComma, PlatformShortcuts.Primary),
                };
                _menuIcons.Add((settingsMenuItem, MaterialIconKind.CogOutline));
                // The window's own DataContext is the MainViewModel (set by
                // App.Bootstrap), so the menu reaches it the same way every
                // binding in the XAML does.
                settingsMenuItem.Click += (_, _) =>
                    (DataContext as ViewModels.MainViewModel)?.OpenSettingsCommand?.Execute(null);

                var flowerMenu = new NativeMenuItem("Flower") { Menu = new NativeMenu() };
                flowerMenu.Menu.Items.Add(aboutItem);
                flowerMenu.Menu.Items.Add(new NativeMenuItemSeparator());
                flowerMenu.Menu.Items.Add(settingsMenuItem);
                windowMenu.Items.Insert(0, flowerMenu);

                // Menu-item icons are a Windows/Linux convention (macOS menus
                // stay text-only, so this lives in the non-macOS branch).
                // Repeat/Shuffle get none: they're CheckBox toggles, and the
                // managed menu renders the checkmark in the icon slot.
                void SetIcon(string header, MaterialIconKind kind)
                {
                    var item = topLevelItems.FirstOrDefault(i => i.Header == header);
                    if (item != null)
                        _menuIcons.Add((item, kind));
                }

                SetIcon("Play/Pause",      MaterialIconKind.PlayPause);
                SetIcon("Next Track",      MaterialIconKind.SkipNext);
                SetIcon("Previous Track",  MaterialIconKind.SkipPrevious);
                SetIcon("New Playlist",    MaterialIconKind.PlaylistPlus);
                SetIcon("Rename Playlist", MaterialIconKind.PlaylistEdit);
                SetIcon("Delete Playlist", MaterialIconKind.PlaylistRemove);
                SetIcon("Select Columns…", MaterialIconKind.ViewColumn);
                SetIcon("Zoom",            MaterialIconKind.WindowMaximize);
                SetIcon("Go to Current Song", MaterialIconKind.Crosshairs);
                SetIcon("Go to Current Album", MaterialIconKind.Album);
                SetIcon("Go to Current Artist", MaterialIconKind.AccountMusicOutline);
                SetIcon("Clear Queue",     MaterialIconKind.PlaylistRemove);
                SetIcon("Increase Volume", MaterialIconKind.VolumePlus);
                SetIcon("Decrease Volume", MaterialIconKind.VolumeMinus);
                SetIcon("Info",        MaterialIconKind.InformationOutline);
                SetIcon("Play Next",       MaterialIconKind.PlaylistPlay);
                SetIcon("Add to Queue",    MaterialIconKind.PlaylistMusicOutline);
                SetIcon("Locate File",     MaterialIconKind.FolderOpenOutline);
                SetIcon("Download",        MaterialIconKind.CloudDownloadOutline);
                SetIcon("Play Playlist Next",    MaterialIconKind.PlaylistPlay);
                SetIcon("Add Playlist to Queue", MaterialIconKind.PlaylistMusicOutline);
                SetIcon("Edit Rules…",     MaterialIconKind.Tune);
                SetIcon("Home",            MaterialIconKind.HomeOutline);
                SetIcon("Search",          MaterialIconKind.Magnify);
                SetIcon("Queue",           MaterialIconKind.PlaylistMusicOutline);
                SetIcon("History",         MaterialIconKind.ClockTimeEightOutline);
                SetIcon("Songs",           MaterialIconKind.Music);
                SetIcon("Albums",          MaterialIconKind.Album);
                SetIcon("Artists",         MaterialIconKind.AccountOutline);
                SetIcon("Filter",          MaterialIconKind.Magnify);

                PaintMenuIcons();
                ActualThemeVariantChanged += (_, _) => PaintMenuIcons();
            }
        }
    }

    // The menu bar's icons, kept so they can be drawn again in the other
    // theme's ink. A menu icon is a bitmap, not a control, so it inherits no
    // colour and repaints itself on nothing.
    private readonly List<(NativeMenuItem Item, MaterialIconKind Kind)> _menuIcons = [];

    private void PaintMenuIcons()
    {
        IBrush ink = this.TryFindResource("PaletteTextPrimary", ActualThemeVariant, out var value) && value is Color color
            ? new ImmutableSolidColorBrush(color)
            : Brushes.Black;
        foreach (var (item, kind) in _menuIcons)
            item.Icon = CreateMenuIcon(kind, ink);
    }

    // The menu bar's entries that act on the view - what is on screen and
    // selected there (MainView), or which page shows - rather than through a
    // command binding to the view model. Their shortcuts are MainView's own
    // keys (MainView_PreviewKeyDown), shown here; on macOS the menu takes the
    // key first and runs the same method.
    private void WireMenuBar(List<NativeMenuItem> items)
    {
        NativeMenuItem? Item(string header) => items.FirstOrDefault(i => i.Header == header);
        ViewModels.MainViewModel? Vm() => DataContext as ViewModels.MainViewModel;

        void On(string header, System.Action click, Key? key = null, KeyModifiers extra = KeyModifiers.None)
        {
            if (Item(header) is not { } item)
                return;
            if (key is { } k)
                item.Gesture = new KeyGesture(k, PlatformShortcuts.Primary | extra);
            item.Click += (_, _) => click();
        }

        // Controls
        On("Go to Current Song", () => _ = Vm()?.GoToCurrentlyPlayingTrackAsync(), Key.L);
        On("Go to Current Album", () => _ = Vm()?.GoToCurrentlyPlayingAlbumAsync());
        On("Go to Current Artist", () => _ = Vm()?.GoToCurrentlyPlayingArtistAsync());
        On("Clear Queue", () => Vm()?.Queue.Clear());
        On("Increase Volume", () => MainContent.ChangeVolume(10), Key.Up);
        On("Decrease Volume", () => MainContent.ChangeVolume(-10), Key.Down);

        // Song
        On("Info", MainContent.GetInfo, Key.I);
        On("Play Next", () => Vm()?.PlayNext(MainContent.SelectedSongs()));
        On("Add to Queue", () => Vm()?.AddToQueue(MainContent.SelectedSongs()));
        On("Locate File", MainContent.LocateSelectedFile);
        On("Download", MainContent.DownloadSelectedSongs);

        // Playlist - the one selected in the sidebar.
        Playlist? Selected() => Vm()?.SelectedSidebarItem?.Playlist;
        List<Track> SelectedSongs() => Selected() is { } playlist && Vm() is { } vm
            ? PlaylistTree.TracksOf(playlist, vm.Library.Playlists).ToList()
            : [];
        // Inside the folder selected, the way Music.app's does, or at the top.
        On("New Folder", () => _ = Vm()?.CreatePlaylistFolder(Selected() is { IsFolder: true } folder ? folder : null));
        On("Play Playlist Next", () =>
        {
            if (SelectedSongs() is { Count: > 0 } songs)
                Vm()?.PlayNext(songs);
        });
        On("Add Playlist to Queue", () =>
        {
            if (SelectedSongs() is { Count: > 0 } songs)
                Vm()?.AddToQueue(songs);
        });
        On("Edit Rules…", () =>
        {
            if (Selected() is { IsSmart: true } playlist)
                Vm()?.EditSmartPlaylist(playlist);
        });
        On("Convert to Ordinary Playlist", () =>
        {
            if (Selected() is { IsSmart: true } playlist)
                _ = Vm()?.ConvertPlaylistToOrdinary(playlist);
        });

        // View - the sidebar's Library rows, Cmd/Ctrl+1 to 6 in its order,
        // and Search, Cmd/Ctrl+F.
        foreach (var (kind, key) in MainView.SectionKeys)
        {
            var section = kind;
            On(section.ToString(), () => MainContent.ShowSection(section), key);
        }
        On("Filter", MainContent.FocusFilter, Key.K);
        // Back and Forward run by their commands; only their keys are set here.
        if (Item("Back") is { } back)
            back.Gesture = new KeyGesture(Key.OemOpenBrackets, PlatformShortcuts.Primary);
        if (Item("Forward") is { } forward)
            forward.Gesture = new KeyGesture(Key.OemCloseBrackets, PlatformShortcuts.Primary);

        // Greyed while there is nothing for them to act on, worked out as
        // each menu opens - and only then, so a menu that somehow never
        // reports opening leaves them enabled, doing nothing, rather than
        // greyed for good.
        void WhenOpened(string anyHeader, System.Action update)
        {
            var menu = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>()
                .Select(top => top.Menu)
                .FirstOrDefault(m => m?.Items.OfType<NativeMenuItem>().Any(i => i.Header == anyHeader) == true);
            if (menu == null)
                return;
            menu.Opening += (_, _) => update();
        }

        void Enable(string header, bool enabled)
        {
            if (Item(header) is { } item)
                item.IsEnabled = enabled;
        }

        WhenOpened("Go to Current Song", () =>
        {
            var playing = Vm()?.NowPlaying.CurrentlyPlayingTrack;
            Enable("Go to Current Song", playing != null);
            Enable("Go to Current Album", !string.IsNullOrEmpty(playing?.Album));
            Enable("Go to Current Artist", !string.IsNullOrEmpty(playing?.Artists));
            Enable("Clear Queue", Vm()?.Queue.CanClear == true);
        });
        WhenOpened("Info", () =>
        {
            var songs = MainContent.SelectedSongs();
            var vm = Vm();
            Enable("Info", songs.Count > 0 || vm?.NowPlaying.CurrentlyPlayingTrack != null);
            Enable("Play Next", songs.Count > 0);
            Enable("Add to Queue", songs.Count > 0);
            Enable("Locate File", LocalFileDeletion.LocalFiles(songs).Count > 0);
            Enable("Download", vm != null && !vm.Downloads.IsBulkDownloading && songs.Any(vm.Availability.IsDownloadable));
        });
        WhenOpened("Edit Rules…", () =>
        {
            var playlist = Selected();
            var hasSongs = SelectedSongs().Count > 0;
            Enable("Play Playlist Next", hasSongs);
            Enable("Add Playlist to Queue", hasSongs);
            Enable("Edit Rules…", playlist is { IsSmart: true } && Vm()?.SmartPlaylists != null);
            Enable("Convert to Ordinary Playlist", playlist is { IsSmart: true });
        });
    }

    // NativeMenuItem.Icon wants a Bitmap rather than a control, so rasterize
    // the Material icon's 24x24 path geometry into a 16x16 bitmap (the
    // context draws in raw pixels, hence the plain 96-DPI 1:1 setup).
    // Drawn in the menu's own text colour - it used to be black whatever the
    // theme, which in the dark one is black on the menu's near-black.
    private static RenderTargetBitmap CreateMenuIcon(MaterialIconKind kind, IBrush ink)
    {
        var geometry = Geometry.Parse(MaterialIconDataProvider.GetData(kind));
        var bitmap = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        using (context.PushTransform(Matrix.CreateScale(16.0 / 24.0, 16.0 / 24.0)))
            context.DrawGeometry(ink, null, geometry);
        return bitmap;
    }

    // A flower:// link opened from outside the app (PairingLinkInbox), handled
    // the way the address box handles one pasted into it: find the server at
    // whichever of the link's addresses answers as it, ask, pair. Brought to
    // the front first - on Windows and Linux the link reached this instance
    // from a launch the user just made, which has already exited.
    //
    // The server's own page is shown before asking, with the link in its code
    // box, because that is where a refused code is reported (PairingCodeError)
    // and where the pairing's progress shows afterwards.
    public async void OpenPairingLink(PairingInvite invite)
    {
        try
        {
            if (DataContext is not ViewModels.MainViewModel vm)
                return;

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();

            if (vm.PairingLinkRefusal(invite) is { } refusal)
            {
                await ConfirmDialogWindow.ShowMessageAsync(this, "Pairing Link Not Used", refusal);
                return;
            }

            var result = await vm.AddManualServerAsync(invite.ToString());
            if (result.Server is not { } server)
            {
                await ConfirmDialogWindow.ShowMessageAsync(
                    this, "Could Not Reach the Server",
                    result.Failure ?? "None of the addresses in that link answered.");
                return;
            }

            vm.ShowServerPage(server.Fingerprint, invite.ToString());

            var alias = string.IsNullOrWhiteSpace(server.Alias) ? "this server" : server.Alias;
            var confirmed = await ConfirmDialogWindow.ShowAsync(
                this,
                $"Pair With \"{alias}\"?",
                $"This device's library view will be replaced by \"{alias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted. "
                + $"The link you opened authorizes this device on \"{alias}\" immediately.",
                "Pair");
            if (confirmed)
                vm.PairWithServer(server, invite.ToString());
        }
        catch (System.Exception ex)
        {
            AppLogging.CreateLogger<MainWindow>().LogError(ex, "Opening a pairing link failed");
        }
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

        _appSettingsStore.Save(_appSettings);
    }
}
