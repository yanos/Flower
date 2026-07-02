using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views;
using Flower.Views.Mobile;

using Microsoft.Extensions.DependencyInjection;


namespace Flower;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
        var playlistSyncService = new PlaylistSyncService(library, ownFingerprint);

        Ioc.Default.ConfigureServices(
            new ServiceCollection()
                .AddSingleton<IAudioManager>(new VlcAudioManager())
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton(appSettings)
                .AddSingleton<ColumnVisibilityStore>()
                .AddSingleton<ColumnManager>()
                .AddSingleton(importer)
                .AddSingleton(networkDiscovery)
                .AddSingleton(playlistSyncService)
                .AddSingleton<MainViewModel>()
                .AddSingleton<VolumeControlViewModel>()
                .AddSingleton<CurrentlyPlayingControlViewModel>()
                .AddSingleton<MobileMainViewModel>()
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
            var freshTracks = await importer.ImportAsync(appSettings.LibraryPaths);

            // Update the playlist first so navigation is consistent when TracksUpdated fires
            mainPlaylist.ReplaceAll(freshTracks);
            library.UpdateTracks(freshTracks);

            await libraryStore.SaveAsync(freshTracks);
        });
    }

}
