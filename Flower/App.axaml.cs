using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Logging;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views;
using Flower.Views.Mobile;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;

namespace Flower;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AboutMenuItem_OnClick(object? sender, System.EventArgs e) => new AboutWindow().Show();

    // OpenSettingsCommand just raises MainViewModel.SettingsRequested, which
    // MainView.axaml.cs is already subscribed to (the same path Cmd/Ctrl+,
    // uses) - reusing it here rather than constructing a SettingsWindow
    // directly keeps there being exactly one place that knows how to open it.
    private void SettingsMenuItem_OnClick(object? sender, System.EventArgs e) =>
        Ioc.Default.GetRequiredService<MainViewModel>().OpenSettingsCommand?.Execute(null);

    public override void OnFrameworkInitializationCompleted()
    {
        // Must run before anything below can log to a real file - classes with
        // a static logger field (Library, the *Store classes, etc.) resolve it
        // to whatever AppLogging.Initialize has configured *the first time that
        // class is touched*, so this needs to be the very first thing that happens.
        var logPath = AppLogging.Initialize();
        var logger = AppLogging.CreateLogger<App>();

        // Anything that throws without a handler further up would otherwise
        // just vanish (a console nobody's watching, or on some platforms
        // nothing at all) - log it before the process potentially dies so a
        // bug report has something to go on.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        logger.LogInformation("Flower starting. Log file: {LogPath}", logPath);

        BindingPlugins.DataValidators.RemoveAt(0);

        var libraryStore = new LibraryStore(AppLogging.CreateTypedLogger<LibraryStore>());
        var appSettingsStore = new AppSettingsStore(AppLogging.CreateTypedLogger<AppSettingsStore>());
        var appSettings = appSettingsStore.Load();
        // Before any window is created, so the very first frame already
        // renders in the saved variant instead of flashing OS-default then
        // switching.
        AppTheme.Apply(appSettings.ThemePreference);
        var importer = Importer.PlatformMusicImporter.Current ?? new Importer.Importer(AppLogging.CreateTypedLogger<Importer.Importer>());

        // Load cached library synchronously so the UI shows immediately with data
        var cachedTracks = libraryStore.Load();
        var library = new Library(cachedTracks, AppLogging.CreateTypedLogger<Library>());
        var mainPlaylist = new MainPlaylist(library.Tracks);

        var playlistStore = new PlaylistStore(AppLogging.CreateTypedLogger<PlaylistStore>());
        foreach (var playlist in playlistStore.Load(library.Tracks))
            library.AddPlaylist(playlist);

        var networkDiscovery = new NetworkDiscoveryService(AppLogging.CreateTypedLogger<NetworkDiscoveryService>());

        var deviceIdentityStore = new DeviceIdentityStore(AppLogging.CreateTypedLogger<DeviceIdentityStore>());
        var deviceNicknameStore = new DeviceNicknameStore(AppLogging.CreateTypedLogger<DeviceNicknameStore>());
        var trustedPeerStore = new TrustedPeerStore(AppLogging.CreateTypedLogger<TrustedPeerStore>());
        var playlistSyncStateStore = new PlaylistSyncStateStore(AppLogging.CreateTypedLogger<PlaylistSyncStateStore>());

        // One shared, mutable identity object rather than separate fingerprint/
        // alias strings handed to each service - MainViewModel.DeviceAlias edits
        // it in place (and persists via DeviceIdentityStore) when the user renames
        // this device in Settings, and every service below reads .Alias live off
        // the same instance, so the new name takes effect immediately without
        // needing to reconstruct or restart anything.
        var deviceIdentity = deviceIdentityStore.Load();
        var clientLogStore = new ClientLogStore();
        var syncHttpServer = new SyncHttpServer(deviceIdentity, appSettings, library, playlistStore, trustedPeerStore, clientLogStore, AppLogging.CreateTypedLogger<SyncHttpServer>());
        var playlistSyncService = new PlaylistSyncService(library, deviceIdentity, appSettings, playlistStore, playlistSyncStateStore, deviceNicknameStore, AppLogging.CreateTypedLogger<PlaylistSyncService>());
        var librarySyncService = new LibrarySyncService(library, deviceIdentity, appSettings, libraryStore, InMemoryLogStore.Instance, AppLogging.CreateTypedLogger<LibrarySyncService>());
        var libraryDownloadService = new LibraryDownloadService(library, deviceIdentity, appSettings, libraryStore, AppLogging.CreateTypedLogger<LibraryDownloadService>());

        Ioc.Default.ConfigureServices(
            new ServiceCollection()
                .AddSingleton<IAudioManager>(new VlcAudioManager())
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton(appSettings)
                .AddSingleton(deviceIdentity)
                .AddSingleton<ColumnManager>()
                .AddSingleton(importer)
                .AddSingleton(networkDiscovery)
                .AddSingleton(syncHttpServer)
                .AddSingleton(playlistSyncService)
                .AddSingleton(librarySyncService)
                .AddSingleton(libraryDownloadService)
                .AddSingleton(libraryStore)
                .AddSingleton(appSettingsStore)
                .AddSingleton(playlistStore)
                .AddSingleton(deviceIdentityStore)
                .AddSingleton(deviceNicknameStore)
                .AddSingleton(trustedPeerStore)
                .AddSingleton(playlistSyncStateStore)
                .AddSingleton(clientLogStore)
                .AddSingleton(InMemoryLogStore.Instance)
                .AddSingleton<MainViewModel>()
                .AddSingleton<VolumeControlViewModel>()
                .AddSingleton<CurrentlyPlayingControlViewModel>()
                .AddSingleton<MobileMainViewModel>()
                .AddSingleton<LogViewModel>()
                .AddLogging(builder => builder.AddSerilog())
                .BuildServiceProvider());

        var mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            // Avalonia's DBus integration can tear down after the dispatcher
            // has already stopped, and its observers then throw an unhandled
            // TaskCanceledException on a thread-pool thread, crashing the
            // process on an otherwise clean quit (AvaloniaUI/Avalonia#19523,
            // open as of 11.3.x). By the time Exit is raised every save has
            // already run (MainWindow.Closing flushes settings, columns, the
            // library, and the log), so end the process here before the DBus
            // teardown can race the dead dispatcher.
            if (OperatingSystem.IsLinux())
            {
                desktop.Exit += (_, _) =>
                {
                    AppLogging.Shutdown();
                    Environment.Exit(0);
                };
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MobileMainView
            {
                DataContext = Ioc.Default.GetRequiredService<MobileMainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();

        // See SYNC-PLAN.md: mDNS discovery + the start of the real sync protocol.
        // SyncHttpServer starts first so networkDiscovery can advertise whichever
        // port it actually bound (see SyncHttpServer.Start for why that can differ
        // from SyncHttpServer.DefaultPort).
        PlatformMulticastLock.Current?.Acquire();
        syncHttpServer.Start();
        networkDiscovery.Start(syncHttpServer.BoundPort ?? SyncHttpServer.DefaultPort);

        // Rescan the music folder in the background while the UI is already showing
        _ = Task.Run(async () =>
        {
            var rescanLogger = AppLogging.CreateLogger("Flower.Rescan");
            // Covers the whole sequence below, not just the two iTunes syncs'
            // own brief individual scopes - the rescan itself is the longest
            // part (~9s against a large real library) and previously had no
            // busy-spinner coverage of its own at all, which is why the
            // spinner was so easy to miss at startup.
            using var busy = mainViewModel.BeginBusyScope("Refreshing Library");
            try
            {
                rescanLogger.LogInformation("Startup rescan starting for paths: {LibraryPaths}", string.Join(", ", appSettings.LibraryPaths));
                var stopwatch = Stopwatch.StartNew();
                var freshTracks = await importer.ImportAsync(appSettings.LibraryPaths);
                rescanLogger.LogInformation("Startup rescan found {TrackCount} tracks in {ElapsedMs}ms", freshTracks.Count, stopwatch.ElapsedMilliseconds);

                // Update the playlist first so navigation is consistent when TracksUpdated fires
                mainPlaylist.ReplaceAll(freshTracks);
                library.UpdateTracks(freshTracks);

                await libraryStore.SaveAsync(library.Tracks);
                rescanLogger.LogInformation("Library saved ({TrackCount} tracks)", library.Tracks.Count);

                // SyncITunesPlayCountAsync/SyncITunesDateAddedAsync each do their
                // own save (either may run again later via its own Settings
                // checkbox, independent of this startup rescan) and layer their
                // own more specific BusyMessage on top of this outer scope's.
                if (appSettings.SyncPlayCountFromITunes)
                    await mainViewModel.SyncITunesPlayCountAsync();
                if (appSettings.SyncDateAddedFromITunes)
                    await mainViewModel.SyncITunesDateAddedAsync();
            }
            catch (Exception ex)
            {
                // Without this, a failure here (e.g. a library path became
                // unreadable) would just be an unobserved task fault - logged
                // above via TaskScheduler.UnobservedTaskException, eventually,
                // but only once the GC finalizes the task; log it immediately
                // here instead.
                rescanLogger.LogError(ex, "Startup rescan failed");
            }
        });
    }

}
