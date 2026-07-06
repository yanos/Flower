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

        var libraryStore = new LibraryStore();
        var appSettings = new AppSettingsStore().Load();
        var importer = Importer.PlatformMusicImporter.Current ?? new Importer.Importer();

        // Load cached library synchronously so the UI shows immediately with data
        var cachedTracks = libraryStore.Load();
        var library = new Library(cachedTracks);
        var mainPlaylist = new MainPlaylist(library.Tracks);

        foreach (var playlist in new PlaylistStore().Load(library.Tracks))
            library.AddPlaylist(playlist);

        var networkDiscovery = new NetworkDiscoveryService();

        // SyncHttpServer loads/generates this device's identity itself (see
        // DeviceIdentityStore) - read it once here too so PlaylistSyncService can
        // use the same fingerprint for initiator election (see its ordinal-compare
        // comment) without a second dependency between the two services.
        var ownFingerprint = new DeviceIdentityStore().Load().Fingerprint;
        var syncHttpServer = new SyncHttpServer(networkDiscovery.OwnInstanceName, library);
        var playlistSyncService = new PlaylistSyncService(library, ownFingerprint, networkDiscovery.OwnInstanceName);
        var librarySyncService = new LibrarySyncService(library, ownFingerprint, networkDiscovery.OwnInstanceName);

        Ioc.Default.ConfigureServices(
            new ServiceCollection()
                .AddSingleton<IAudioManager>(new VlcAudioManager())
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton(appSettings)
                .AddSingleton<ColumnManager>()
                .AddSingleton(importer)
                .AddSingleton(networkDiscovery)
                .AddSingleton(syncHttpServer)
                .AddSingleton(playlistSyncService)
                .AddSingleton(librarySyncService)
                .AddSingleton<MainViewModel>()
                .AddSingleton<VolumeControlViewModel>()
                .AddSingleton<CurrentlyPlayingControlViewModel>()
                .AddSingleton<MobileMainViewModel>()
                .AddLogging(builder => builder.AddSerilog())
                .BuildServiceProvider());

        var mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
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

                // SyncITunesPlayCountAsync does its own save (it may run again
                // later via the Settings checkbox, independent of this startup
                // rescan) and drives the status bar spinner via BeginBusy.
                if (appSettings.SyncPlayCountFromITunes)
                    await mainViewModel.SyncITunesPlayCountAsync();
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
