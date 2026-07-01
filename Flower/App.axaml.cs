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
using Flower.ViewModels;
using Flower.Views;

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

        // Load cached library synchronously so the UI shows immediately with data
        var cachedTracks = libraryStore.Load();
        var library = new Library(cachedTracks);
        var mainPlaylist = new MainPlaylist(library.Tracks);

        foreach (var playlist in new PlaylistStore().Load(library.Tracks))
            library.AddPlaylist(playlist);

        Ioc.Default.ConfigureServices(
            new ServiceCollection()
                .AddSingleton<IAudioManager>(new VlcAudioManager())
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton<ColumnVisibilityStore>()
                .AddSingleton<ColumnManager>()
                .AddSingleton<Importer.Importer>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<VolumeControlViewModel>()
                .AddSingleton<CurrentlyPlayingControlViewModel>()
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
            singleViewPlatform.MainView = new MainView
            {
                DataContext = Ioc.Default.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();

        // Rescan the music folder in the background while the UI is already showing
        _ = Task.Run(async () =>
        {
            var importer = Ioc.Default.GetRequiredService<Importer.Importer>();
            var freshTracks = importer.Import();

            // Update the playlist first so navigation is consistent when TracksUpdated fires
            mainPlaylist.ReplaceAll(freshTracks);
            library.UpdateTracks(freshTracks);

            await libraryStore.SaveAsync(freshTracks);
        });
    }

}
