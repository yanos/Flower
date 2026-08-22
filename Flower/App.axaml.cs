using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using LibVLCSharp.Shared;

using Flower.Controls;
using Flower.Logging;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
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
        // to whatever AppLogging.UseLoggerFactory below has configured *the
        // first time that class is touched*, so these two calls need to be the
        // very first thing that happens.
        var logPath = AppLogging.Initialize();

        // DI container setup starts here, near the top of startup - logging is
        // the first thing registered on it (via the standard
        // Microsoft.Extensions.Logging AddLogging(...)/AddSerilog() pipeline,
        // wrapping the Log.Logger Initialize() just configured) rather than
        // AppLogging building its own separate factory. `services` keeps
        // accumulating registrations as Bootstrap constructs each piece of the
        // app below; only the final Ioc.Default.ConfigureServices(...) call
        // actually builds and hands off the finished container, since
        // CommunityToolkit.Mvvm's Ioc.Default can only be configured once.
        // One factory, registered as the container's ILoggerFactory rather
        // than built from a throwaway provider of its own - the previous
        // BuildServiceProvider() here produced a second, never-disposed
        // container that existed solely to fetch this (ARCHITECTURE-REVIEW 2.3).
        // Registering the instance after AddLogging makes it win resolution, so
        // every injected ILogger<T> comes from the very same factory the
        // non-DI, static-field loggers in Flower.Core use.
        var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
        AppLogging.UseLoggerFactory(loggerFactory);

        var services = new ServiceCollection()
            .AddLogging(builder => builder.AddSerilog())
            .AddSingleton(loggerFactory);

        var logger = AppLogging.CreateLogger<App>();

        // Anything that throws without a handler further up would otherwise
        // just vanish (a console nobody's watching, or on some platforms
        // nothing at all) - log it before the process potentially dies so a
        // bug report has something to go on.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
            // The process is about to die - flush now, or Serilog's buffered
            // file write never reaches disk and this line is lost right along
            // with the crash it was recording.
            if (e.IsTerminating)
                AppLogging.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // A *native* crash (e.g. inside libvlc) bypasses UnhandledException
        // above entirely - it never becomes a managed exception. Pick up
        // whatever the OS/runtime already recorded about the previous run's
        // crash, if any, and fold it into this run's log instead. See
        // CrashReportScanner's own comment for how each platform's evidence
        // gets there.
        CrashReportScanner.ScanAndLog(logger);
        if (PlatformCrashInfo.PendingAndroidExitReasons is { Count: > 0 } androidExitReasons)
        {
            foreach (var reason in androidExitReasons)
                logger.LogCritical("Crash found in Android's process exit history: {Reason}", reason);
            PlatformCrashInfo.PendingAndroidExitReasons = null;
        }

        logger.LogInformation("Flower starting. Log file: {LogPath}", logPath);

        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            // Avalonia 12's Android host builds the AppBuilder (and calls this
            // method) from Application.OnCreate, which Android guarantees
            // completes before any Activity is created - so MainActivity hasn't
            // had a chance yet to wire Importer.PlatformMusicImporter.Current
            // (the one platform hook Bootstrap reads below that genuinely needs
            // a live Activity, for ActivityCompat.RequestPermissions' result
            // callback - see AndroidMediaStoreImporter). MainViewFactory is the
            // sanctioned way to defer content creation until an Activity
            // actually exists: it's only invoked from AvaloniaMainActivity.
            // InitializeAvaloniaView, i.e. after MainActivity.OnCreate has
            // already run.
            activityLifetime.MainViewFactory = () => Bootstrap(logger, services)!;
        }
        else
        {
            Bootstrap(logger, services);
        }
    }

    // Registration only - nothing here constructs a service; the container
    // does that when it is resolved.
    internal static void RegisterServices(IServiceCollection services)
    {
        services
            // The database itself, shared by every repository-backed store.
            // Registered as a singleton so one FlowerDb governs the process's
            // connection settings (see FlowerDb.Open's pragmas) - and, for a
            // test pointing at an in-memory database, so its keep-alive
            // connection outlives the individual repository calls.
            //
            // The one-time JSON import happens in the factory, before this
            // instance is handed to anything - the Library registration below
            // reads through LibraryStore the moment it is resolved, so there
            // is no later point at which it would still be safe to do. (The
            // schema itself is FlowerDb's own responsibility - see its
            // constructor.)
            .AddSingleton(sp =>
            {
                var db = FlowerDb.OpenDefault();
                JsonLibraryImport.RunIfNeeded(db, sp.GetRequiredService<ILogger<FlowerDb>>());
                return db;
            })

            // Persistence. Every store takes an ILogger<T> and the FlowerDb
            // above, so the container builds them outright.
            .AddSingleton<TrackRepository>()
            .AddSingleton<PlaylistRepository>()
            .AddSingleton<LibraryStore>()
            .AddSingleton<AppSettingsStore>()
            .AddSingleton<PlaylistStore>()
            .AddSingleton<DeviceKeyStore>()
            .AddSingleton<DeviceIdentityStore>()
            .AddSingleton<DeviceNicknameStore>()
            .AddSingleton<SidebarRenameService>()
            .AddSingleton<TrustedPeerStore>()
            .AddSingleton<PlaylistSyncStateStore>()
            .AddSingleton<ClientLogStore>()
            .AddSingleton(InMemoryLogStore.Instance)

            // Loaded-from-disk state. AppSettings and the cached library are
            // values a store produces, not services, so they need a factory -
            // resolving either one is what reads the file.
            .AddSingleton(sp => sp.GetRequiredService<AppSettingsStore>().Load())
            // The repositories are handed in as Library's ITrackStore and
            // IPlaylistStore, so a play count, a star or a playlist edit is
            // written the moment it is applied -
            // exactly how Flower.Server registers the same type. It used to be
            // the caller's job here (PlaylistControlViewModel bumping the count
            // and then remembering to call LibraryStore.SaveStats), which is
            // the same structural problem Library.PlaylistsChanged exists to
            // solve for playlists: a new mutation path could simply forget.
            .AddSingleton(sp => new Library(
                sp.GetRequiredService<LibraryStore>().Load(),
                sp.GetRequiredService<ILogger<Library>>(),
                sp.GetRequiredService<TrackRepository>(),
                sp.GetRequiredService<PlaylistRepository>()))
            .AddSingleton(sp => new MainPlaylist(sp.GetRequiredService<Library>().Tracks))

            // The platform hook wins when a head has installed one (Android's
            // MediaStore importer); otherwise the shared filesystem scanner.
            .AddSingleton(sp => Importer.PlatformMusicImporter.Current
                                ?? new Importer.Importer(sp.GetRequiredService<ILogger<Importer.Importer>>()))

            .AddSingleton<ColumnManager>()
            // Constructed by hand rather than by type: its two peer dependencies
            // are genuinely optional (see its own constructor remarks) and are
            // simply not registered on Flower.Web/WASM, and a nullable *type* is
            // not something the container treats as optional - only a defaulted
            // parameter is, which the trailing ILogger rules out here. Asking for
            // them with GetService is what actually makes them optional; the
            // type-based registration threw CannotResolveService and took the
            // whole browser app down before its first frame.
            .AddSingleton(sp => new AlbumArtLoader(
                sp.GetService<PeerTrackResolver>(),
                sp.GetService<DeviceIdentity>(),
                sp.GetRequiredService<ILogger<AlbumArtLoader>>()))

            // One 60Hz timer for every animation in the app - see
            // AnimationClock. Injected into MainViewModel (which threads it
            // down to the track rows); the Controls that also animate reach it
            // through AnimationClock.Current, set from this instance below.
            .AddSingleton<AnimationClock>()

            // The status bar's busy indicator, shared between MainViewModel
            // (which surfaces it as IsBusy/BusyMessage) and the collaborators
            // split out of it that run long operations - see BusyState.
            .AddSingleton<BusyState>()
            .AddSingleton<ITunesImportCoordinator>()

            // ViewModels are singletons because they hold app-lifetime state,
            // not because they cannot be let go of any more: every one of them
            // below now pairs each "+=" with its "-=" through a SubscriptionBag
            // and disposes it (ARCHITECTURE-REVIEW 2.3/4.2), so the container
            // disposing this provider really does detach them.
            .AddSingleton<PlaylistControlViewModel>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<VolumeControlViewModel>()
            .AddSingleton<CurrentlyPlayingControlViewModel>()
            .AddSingleton<MobileMainViewModel>()
            .AddSingleton<LogViewModel>()
            .AddSingleton<EqualizerViewModel>()
            .AddSingleton<NowPlayingIntegrationService>();

        RegisterAudio(services);

        // Flower.Web/WASM has no P2P sync stack at all: .NET-for-WASM's crypto
        // backend has no asymmetric crypto support whatsoever (verified
        // directly - ECDsa.Create()/RSA.Create() both throw
        // PlatformNotSupportedException for every curve/key size), so
        // DeviceSigningKey - and every service below, which all depend on it
        // transitively - simply cannot be constructed there. They are left
        // unregistered rather than registered-as-null; MainViewModel takes all
        // of them as nullable, defaulted constructor parameters specifically to
        // accommodate that (see its own doc comment).
        if (OperatingSystem.IsBrowser())
            return;

        services
            // This device's cryptographic identity (see DeviceSigningKey/
            // SignatureVerifier). DeviceIdentity is derived from it, since
            // Fingerprint is the public key's hash rather than an independent
            // random value (see DeviceIdentityStore.Load).
            .AddSingleton(sp =>
            {
                var (deviceKey, devicePublicKeyRaw) = sp.GetRequiredService<DeviceKeyStore>().Load();
                return new DeviceSigningKey(deviceKey, devicePublicKeyRaw);
            })
            // One shared, mutable identity object rather than separate
            // fingerprint/alias strings handed to each service - MainViewModel.
            // DeviceAlias edits it in place (and persists via
            // DeviceIdentityStore) when the user renames this device in
            // Settings, and every service below reads .Alias live off the same
            // instance, so the new name takes effect immediately without
            // needing to reconstruct or restart anything.
            .AddSingleton(sp => sp.GetRequiredService<DeviceIdentityStore>()
                .Load(sp.GetRequiredService<DeviceSigningKey>().Fingerprint))

            .AddSingleton<NetworkDiscoveryService>()
            .AddSingleton<SyncHttpServer>()
            .AddSingleton<PlaylistSyncService>()
            .AddSingleton<LibrarySyncService>()
            .AddSingleton<LibraryDownloadService>()
            .AddSingleton<PeerPairingService>()
            .AddSingleton<PeerUnpairNotifier>()
            .AddSingleton<PairedServerReachability>()
            .AddSingleton<PeerTrackResolver>();
    }

    // The one platform fork the audio pipeline needs.
    //
    // LibVLC is only needed for decode now (GaplessAudioManager's
    // TrackDecoders) - the render sink on every platform is MiniaudioSink, a
    // dedicated miniaudio playback device reading the shared ring buffer
    // directly, replacing LibVlcRawStreamSink's synthetic-stream-through-
    // LibVLC's-rawaud-demuxer approach after that proved to be the source of a
    // real playback bug (a decode-side seek could freeze the render side solid
    // for several seconds - see git history). Android/iOS use their vendored
    // native miniaudio builds (native/miniaudio/android, native/miniaudio/ios)
    // - see LibVlcRawStreamSink's own remarks for why it is kept around
    // unreferenced rather than deleted yet.
    //
    // Neither LibVLC nor miniaudio ships a browser/WASM build (see
    // SYNC-PLAN.md's Flower.Web section) - VlcNativeSetup.Initialize()/
    // `new LibVLC()` would throw immediately there, so Flower.Web gets
    // WebAudioManager instead, driving a browser <audio> element via [JSImport]
    // rather than going through IAudioSink/GaplessCoordinator at all (see its
    // own remarks for why).
    internal static void RegisterAudio(IServiceCollection services)
    {
        if (OperatingSystem.IsBrowser())
        {
            services.AddSingleton<IAudioManager>(_ => new Manager.WebAudioManager());
            return;
        }

        services
            .AddSingleton(_ =>
            {
                VlcNativeSetup.Initialize();
                return new LibVLC();
            })
            .AddSingleton(sp => PlatformAudioManager.Current
                                ?? new MiniaudioSink(sp.GetRequiredService<ILogger<MiniaudioSink>>()))
            .AddSingleton<IAudioManager>(sp => new GaplessAudioManager(
                sp.GetRequiredService<LibVLC>(),
                sp.GetRequiredService<IAudioSink>(),
                sp.GetRequiredService<ILogger<GaplessAudioManager>>(),
                sp.GetRequiredService<ILogger<GaplessCoordinator>>(),
                sp.GetRequiredService<ILogger<TrackDecoder>>()));
    }

    // The composition root. Every service is registered by *type* (or by a
    // factory when it needs something the container cannot produce on its own -
    // a platform hook, a loaded-from-disk value), and the container does the
    // constructing. Adding a dependency to an existing service is therefore an
    // edit to that service's constructor and nothing else; this method only
    // changes when a genuinely new service appears. It used to `new` all ~30 of
    // them by hand in dependency order and register them as instances, which
    // meant constructor injection was bypassed everywhere and every new
    // dependency was also an edit here (docs/ARCHITECTURE-REVIEW.md 2.3).
    private Control? Bootstrap(Microsoft.Extensions.Logging.ILogger logger, IServiceCollection services)
    {
        RegisterServices(services);

        // CommunityToolkit.Mvvm's Ioc.Default can only be configured once, so
        // this is the single hand-off point from registration to resolution.
        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var appSettings = provider.GetRequiredService<AppSettings>();
        // Before any window is created, so the very first frame already
        // renders in the saved variant instead of flashing OS-default then
        // switching.
        AppTheme.Apply(appSettings.ThemePreference);

        var library = provider.GetRequiredService<Library>();
        var playlistStore = provider.GetRequiredService<PlaylistStore>();

        // ResetPlaylists, not a loop of AddPlaylist: replaying the on-disk set
        // back into Library is not a change, and every mutation path persists
        // itself now (see Library's IPlaylistStore), so adding them one at a
        // time would write the whole set back out once per playlist.
        //
        // This subscription used to live here - the one place playlists were
        // written, covering the ~six mutation sites that each used to save by
        // hand. It moved into Library for the same reason the track writes
        // did: a rule the composition root enforces is still a rule something
        // else has to be wired up to, and Flower.Server had to wire up its own
        // equivalent separately. Library.RaisePlaylistsChanged is now the
        // single place a playlist set reaches disk, for both hosts.
        library.ResetPlaylists(playlistStore.Load(library.Tracks));

        // Resolving IAudioManager is what actually opens LibVLC/miniaudio (see
        // AddAudio), so it happens here rather than lazily under the first
        // ViewModel that happens to want it.
        var audioManager = provider.GetRequiredService<IAudioManager>();
        // Re-apply the persisted EQ curve before the very first frame of audio
        // plays, rather than waiting for the user to open the Equalizer window
        // this session - see EqualizerViewModel.
        if (audioManager is GaplessAudioManager gapless && appSettings.EqualizerSettings is { Enabled: true } eqSettings)
            gapless.ApplyEqualizer(Manager.Equalizer.BuildFrom(eqSettings, GaplessFormat.SampleRate));

        // AlbumArtLoader is reached from `init`-only ViewModels built by static
        // builders (TrackListBuilder, AlbumGridBuilder), which have no
        // container and no constructor to inject through - so its dependencies
        // are handed to one instance here instead of it reaching into
        // Ioc.Default from inside a static method, which is what made it
        // untestable. Threading the instance the rest of the way down to
        // TrackRowViewModel/AlbumTileViewModel belongs with their decomposition
        // (docs/ARCHITECTURE-REVIEW.md 4.2).
        AlbumArtLoader.Current = provider.GetRequiredService<AlbumArtLoader>();

        // Same seam, same reason: MainView, ScreenStackPanel and
        // RubberBandScroll animate from code-behind, where there is no
        // constructor to inject through. Everything with a constructor gets
        // the instance handed to it instead (MainViewModel ->
        // LibraryBrowserViewModel -> TrackRowViewModel).
        AnimationClock.Current = provider.GetRequiredService<AnimationClock>();

        var mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();

        Control? mainView = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(
                appSettings,
                provider.GetRequiredService<ColumnManager>(),
                provider.GetRequiredService<AppSettingsStore>())
            {
                DataContext = mainViewModel
            };
            desktop.MainWindow = window;
            mainView = window;

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
            // Avalonia.Browser reports the same single-view lifetime iOS and
            // Android do, but a browser is a desktop-shaped surface, so it gets
            // the full MainView rather than the touch-first MobileMainView.
            //
            // Both are UserControls with no Background of their own - on
            // desktop MainWindow supplies the window background, and there is
            // no Window in a single-view lifetime. Without one the top level
            // composites onto the transparent page and every DynamicResource
            // themed brush underneath has nothing to sit on. Hosting the view
            // in a themed Panel gives it the surface MainWindow otherwise would.
            Control singleView;
            if (OperatingSystem.IsBrowser())
            {
                var browserMainView = new MainView { DataContext = mainViewModel };
                var browserRoot = new Border { Child = browserMainView };
                browserRoot[!Border.BackgroundProperty] =
                    new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
                singleView = browserRoot;

                OpenServerSettingsFromUrl(browserMainView, logger);
            }
            else
            {
                singleView = new MobileMainView
                {
                    DataContext = Ioc.Default.GetRequiredService<MobileMainViewModel>()
                };
            }

            singleViewPlatform.MainView = singleView;
            mainView = singleView;
        }

        // Constructed eagerly (nothing else references it) so its
        // subscriptions to PlaylistControlViewModel/IAudioManager start
        // immediately rather than lazily on first use - see
        // NowPlayingIntegrationService.cs / docs/MEDIA-KEYS-PLAN.md Phase 2.
        Ioc.Default.GetRequiredService<NowPlayingIntegrationService>();

        base.OnFrameworkInitializationCompleted();

        // See SYNC-PLAN.md: mDNS discovery + the start of the real sync protocol.
        // SyncHttpServer starts first so networkDiscovery can advertise whichever
        // port it actually bound (see SyncHttpServer.Start for why that can differ
        // from SyncHttpServer.DefaultPort).
        //
        // Neither exists under WASM: SyncHttpServer binds a raw HttpListener
        // socket and NetworkDiscoveryService sends/receives mDNS multicast UDP,
        // both flatly unavailable inside a browser sandbox. Flower.Web instead
        // talks to Flower.Server's REST API directly (see SYNC-PLAN.md) - LAN
        // peer-to-peer sync is a desktop/mobile-only feature.
        if (!OperatingSystem.IsBrowser())
        {
            // Registered only on the matching !IsBrowser() branch of
            // RegisterServices, so resolving them is safe exactly here.
            var syncHttpServer = provider.GetRequiredService<SyncHttpServer>();
            var networkDiscovery = provider.GetRequiredService<NetworkDiscoveryService>();

            PlatformMulticastLock.Current?.Acquire();
            syncHttpServer.Start();
            networkDiscovery.Start(syncHttpServer.BoundPort ?? SyncHttpServer.DefaultPort);
        }

        // Rescan the music folder in the background while the UI is already
        // showing. Meaningless under WASM - there's no local music folder to
        // scan in a browser sandbox; Flower.Web's library instead comes from
        // Flower.Server via a SubsonicLibraryImporter (IMusicImporter), not yet
        // built (see SYNC-PLAN.md) - until then the browser head just starts
        // with an empty library rather than running this against nothing.
        if (!OperatingSystem.IsBrowser())
        {
            var importer = provider.GetRequiredService<Importer.IMusicImporter>();
            var mainPlaylist = provider.GetRequiredService<MainPlaylist>();

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
                    // Persisted by UpdateTracks itself - see Library's
                    // ITrackStore. Flower.Server's rescan is the same two
                    // lines for the same reason.
                    library.UpdateTracks(freshTracks);
                    rescanLogger.LogInformation("Library saved ({TrackCount} tracks)", library.Tracks.Count);

                    // SyncITunesPlayCountAsync/SyncITunesDateAddedAsync each do their
                    // own save (either may run again later via its own Settings
                    // checkbox, independent of this startup rescan) and layer their
                    // own more specific BusyMessage on top of this outer scope's.
                    // Both gated on the master IntegrateWithITunes switch first
                    // (see AppSettings) - with it off, Flower ignores Music.app
                    // entirely, whatever these two remember individually.
                    if (appSettings.IntegrateWithITunes)
                    {
                        if (appSettings.SyncPlayCountFromITunes)
                            await mainViewModel.SyncITunesPlayCountAsync();
                        if (appSettings.SyncDateAddedFromITunes)
                            await mainViewModel.SyncITunesDateAddedAsync();
                    }
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

        return mainView;
    }

    // The browser half of the desktop client's "Server Settings..." button.
    //
    // That button mints a short-lived admin session against the server and opens
    // this page at #admin=<token>&page=settings (see
    // MainViewModel.OpenSelectedServerSettingsAsync and Flower.Server's
    // AdminSessionService). The token is the browser's whole authority here - it
    // cannot sign anything, because .NET-for-WebAssembly has no asymmetric crypto
    // at all, which is the same reason DeviceSigningKey is not registered on this
    // platform (see RegisterServices above).
    //
    // The settings shown are then the *server's*, not this app's: a
    // RemoteServerSettingsBackend over the origin the page was served from, which
    // is the server itself.
    private static void OpenServerSettingsFromUrl(MainView mainView, Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            var fragment = BrowserLocation.TakeFragment();
            if (!fragment.TryGetValue("admin", out var token) || string.IsNullOrWhiteSpace(token))
                return;

            var client = ServerAdminClient.ForSession(new HttpClient(), BrowserLocation.Origin, token);
            var settings = new SettingsViewModel(new RemoteServerSettingsBackend(client));

            // Posted rather than called inline: the view is not attached to a
            // visual tree yet at this point in OnFrameworkInitializationCompleted,
            // and SettingsPanel's own load path expects to be.
            Dispatcher.UIThread.Post(() => mainView.ShowSettingsOverlay(settings, mainViewModel: null));
        }
        catch (Exception ex)
        {
            // A malformed fragment, or a browser that would not give us one, must
            // not stop the app from starting - the user can still open Settings
            // themselves, they just will not be administering the server.
            logger.LogWarning(ex, "Could not read the admin session from the page URL");
        }
    }
}
