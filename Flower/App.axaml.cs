using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Importer;
using Flower.Manager;
using Flower.Models;
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
        // Line below is needed to remove Avalonia data validation.
        // Without this line you will get duplicate validations from both Avalonia and CT
        BindingPlugins.DataValidators.RemoveAt(0);

        var importer = new Importer.Importer();
        var tracks = importer.Import();
        var library = new Library(tracks);

        // Register all the services needed for the application to run
        //var collection = new ServiceCollection();
        //collection.AddCommonServices();

        //collection.AddSingleton<IAudioManager>(new VlcAudioManager());
        

        // Creates a ServiceProvider containing services from the provided IServiceCollection
        //var services = collection.BuildServiceProvider();

        var mainPlaylist = new MainPlaylist(library.Tracks);

        Ioc.Default.ConfigureServices(
            new ServiceCollection()
                .AddSingleton<IAudioManager>(new VlcAudioManager())
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(importer)
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton<MainViewModel>()
                .AddSingleton<VolumeControlViewModel>()
                .AddSingleton<CurrentlyPlayingControlViewModel>()
                .BuildServiceProvider());

        //var vm = services.GetRequiredService<MainViewModel>();

        //var mainViewModel = new MainViewModel(library);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Ioc.Default.GetRequiredService<MainViewModel>()
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

        
    }
}
