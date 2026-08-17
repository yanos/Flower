using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

using CommunityToolkit.Mvvm.DependencyInjection;

using LibVLCSharp.Shared;

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
        var services = new ServiceCollection().AddLogging(builder => builder.AddSerilog());
        AppLogging.UseLoggerFactory(services.BuildServiceProvider().GetRequiredService<ILoggerFactory>());

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

    private Control? Bootstrap(Microsoft.Extensions.Logging.ILogger logger, IServiceCollection services)
    {
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

        var deviceKeyStore = new DeviceKeyStore(AppLogging.CreateTypedLogger<DeviceKeyStore>());
        var deviceIdentityStore = new DeviceIdentityStore(AppLogging.CreateTypedLogger<DeviceIdentityStore>());
        var deviceNicknameStore = new DeviceNicknameStore(AppLogging.CreateTypedLogger<DeviceNicknameStore>());
        var trustedPeerStore = new TrustedPeerStore(AppLogging.CreateTypedLogger<TrustedPeerStore>());
        var playlistSyncStateStore = new PlaylistSyncStateStore(AppLogging.CreateTypedLogger<PlaylistSyncStateStore>());
        var clientLogStore = new ClientLogStore();

        // Flower.Web/WASM has no P2P sync stack at all: .NET-for-WASM's crypto
        // backend has no asymmetric crypto support whatsoever (verified
        // directly - ECDsa.Create()/RSA.Create() both throw
        // PlatformNotSupportedException for every curve/key size; only
        // symmetric crypto and hashing work), so DeviceSigningKey - and every
        // one of these services below, which all depend on it transitively -
        // simply cannot be constructed there. MainViewModel takes all of
        // these as nullable, defaulted constructor parameters specifically to
        // accommodate this (see its own doc comment); every other platform
        // still gets the exact same non-null instances as before.
        DeviceIdentity? deviceIdentity = null;
        DeviceSigningKey? signingKey = null;
        NetworkDiscoveryService? networkDiscovery = null;
        SyncHttpServer? syncHttpServer = null;
        PlaylistSyncService? playlistSyncService = null;
        LibrarySyncService? librarySyncService = null;
        LibraryDownloadService? libraryDownloadService = null;
        PeerPairingService? peerPairingService = null;
        PeerUnpairNotifier? peerUnpairNotifier = null;
        PairedServerReachability? pairedServerReachability = null;
        PeerTrackResolver? peerTrackResolver = null;

        if (!OperatingSystem.IsBrowser())
        {
            // This device's cryptographic identity (see DeviceSigningKey/
            // SignatureVerifier) - loaded before DeviceIdentity, since Fingerprint
            // is now derived from the public key here rather than an independent
            // random value (see DeviceIdentityStore.Load).
            var (deviceKey, devicePublicKeyRaw) = deviceKeyStore.Load();
            signingKey = new DeviceSigningKey(deviceKey, devicePublicKeyRaw);

            // One shared, mutable identity object rather than separate fingerprint/
            // alias strings handed to each service - MainViewModel.DeviceAlias edits
            // it in place (and persists via DeviceIdentityStore) when the user renames
            // this device in Settings, and every service below reads .Alias live off
            // the same instance, so the new name takes effect immediately without
            // needing to reconstruct or restart anything.
            deviceIdentity = deviceIdentityStore.Load(signingKey.Fingerprint);

            // Needs deviceIdentity constructed first - it identifies us on every
            // /info poll now, not just gated sync requests (see
            // NetworkDiscoveryService.ResolveAliasAsync, DiscoveredDevice.TrustsUs).
            networkDiscovery = new NetworkDiscoveryService(deviceIdentity, AppLogging.CreateTypedLogger<NetworkDiscoveryService>());
            syncHttpServer = new SyncHttpServer(deviceIdentity, signingKey, appSettings, library, playlistStore, trustedPeerStore, clientLogStore, AppLogging.CreateTypedLogger<SyncHttpServer>());
            playlistSyncService = new PlaylistSyncService(library, deviceIdentity, signingKey, appSettings, playlistStore, playlistSyncStateStore, deviceNicknameStore, AppLogging.CreateTypedLogger<PlaylistSyncService>());
            librarySyncService = new LibrarySyncService(library, deviceIdentity, signingKey, appSettings, libraryStore, InMemoryLogStore.Instance, AppLogging.CreateTypedLogger<LibrarySyncService>());
            libraryDownloadService = new LibraryDownloadService(library, deviceIdentity, signingKey, appSettings, libraryStore, AppLogging.CreateTypedLogger<LibraryDownloadService>());
            peerPairingService = new PeerPairingService(deviceIdentity, signingKey, AppLogging.CreateTypedLogger<PeerPairingService>());
            peerUnpairNotifier = new PeerUnpairNotifier(networkDiscovery, deviceIdentity, signingKey, AppLogging.CreateTypedLogger<PeerUnpairNotifier>());
            pairedServerReachability = new PairedServerReachability(networkDiscovery, appSettings);
            peerTrackResolver = new PeerTrackResolver(pairedServerReachability);
        }

        // LibVLC is only needed for decode now (GaplessAudioManager's
        // TrackDecoders) - the render sink on every platform is MiniaudioSink,
        // a dedicated miniaudio playback device reading the shared ring
        // buffer directly, replacing LibVlcRawStreamSink's synthetic-stream-
        // through-LibVLC's-rawaud-demuxer approach after that proved to be
        // the source of a real playback bug (a decode-side seek could
        // freeze the render side solid for several seconds - see git
        // history). Android/iOS use their vendored native miniaudio builds
        // (native/miniaudio/android, native/miniaudio/ios) - see
        // LibVlcRawStreamSink's own remarks for why it's kept around
        // unreferenced rather than deleted yet.
        //
        // Neither LibVLC nor miniaudio ships a browser/WASM build (see
        // SYNC-PLAN.md's Flower.Web section) - VlcNativeSetup.Initialize()/
        // `new LibVLC()` would throw immediately there, so Flower.Web gets
        // WebAudioManager instead, driving a browser <audio> element via
        // [JSImport] rather than going through IAudioSink/GaplessCoordinator
        // at all (see its own remarks for why). Everything else in Bootstrap
        // below (DI, Views/ViewModels, the library/settings stores) is
        // unaffected - this is the only platform fork the audio pipeline
        // needs.
        IAudioManager audioManager;
        if (OperatingSystem.IsBrowser())
        {
            audioManager = new Manager.WebAudioManager();
        }
        else
        {
            VlcNativeSetup.Initialize();
            var libVLC = new LibVLC();
            var audioSink = PlatformAudioManager.Current ?? new MiniaudioSink(AppLogging.CreateTypedLogger<MiniaudioSink>());

            var gaplessAudioManager = new GaplessAudioManager(
                libVLC,
                audioSink,
                AppLogging.CreateTypedLogger<GaplessAudioManager>(),
                AppLogging.CreateTypedLogger<GaplessCoordinator>(),
                AppLogging.CreateTypedLogger<TrackDecoder>());

            // Re-apply the persisted EQ curve before the very first frame of
            // audio plays, rather than waiting for the user to open the
            // Equalizer window this session - see EqualizerViewModel.
            if (appSettings.EqualizerSettings is { Enabled: true } eqSettings)
                gaplessAudioManager.ApplyEqualizer(Manager.Equalizer.BuildFrom(eqSettings, GaplessFormat.SampleRate));

            audioManager = gaplessAudioManager;
        }

        // AddSingleton(instance) throws on a null instance, unlike a
        // constructor parameter default (see MainViewModel's own doc comment) -
        // these ten are only ever null on Flower.Web/WASM, where nothing else
        // in the container needs them resolvable at all, so they're simply
        // left unregistered there rather than registered-as-null.
        if (deviceIdentity != null)
            services.AddSingleton(deviceIdentity);
        if (signingKey != null)
            services.AddSingleton(signingKey);
        if (networkDiscovery != null)
            services.AddSingleton(networkDiscovery);
        if (pairedServerReachability != null)
            services.AddSingleton(pairedServerReachability);
        if (syncHttpServer != null)
            services.AddSingleton(syncHttpServer);
        if (playlistSyncService != null)
            services.AddSingleton(playlistSyncService);
        if (librarySyncService != null)
            services.AddSingleton(librarySyncService);
        if (libraryDownloadService != null)
            services.AddSingleton(libraryDownloadService);
        if (peerPairingService != null)
            services.AddSingleton(peerPairingService);
        if (peerUnpairNotifier != null)
            services.AddSingleton(peerUnpairNotifier);
        if (peerTrackResolver != null)
            services.AddSingleton(peerTrackResolver);

        Ioc.Default.ConfigureServices(
            services
                .AddSingleton<IAudioManager>(audioManager)
                .AddSingleton<PlaylistControlViewModel>()
                .AddSingleton(library)
                .AddSingleton(mainPlaylist)
                .AddSingleton(appSettings)
                .AddSingleton<ColumnManager>()
                .AddSingleton(importer)
                .AddSingleton(libraryStore)
                .AddSingleton(appSettingsStore)
                .AddSingleton(playlistStore)
                .AddSingleton(deviceKeyStore)
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
                .AddSingleton<EqualizerViewModel>()
                .AddSingleton<NowPlayingIntegrationService>()
                .BuildServiceProvider());

        var mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();

        Control? mainView = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
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
                var browserRoot = new Border
                {
                    Child = new MainView { DataContext = mainViewModel }
                };
                browserRoot[!Border.BackgroundProperty] =
                    new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
                singleView = browserRoot;
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
            // Non-null here by construction - both were built in the matching
            // !IsBrowser() branch above, just too far away for flow analysis
            // to see across.
            PlatformMulticastLock.Current?.Acquire();
            syncHttpServer!.Start();
            networkDiscovery!.Start(syncHttpServer.BoundPort ?? SyncHttpServer.DefaultPort);
        }

        // Rescan the music folder in the background while the UI is already
        // showing. Meaningless under WASM - there's no local music folder to
        // scan in a browser sandbox; Flower.Web's library instead comes from
        // Flower.Server via a SubsonicLibraryImporter (IMusicImporter), not yet
        // built (see SYNC-PLAN.md) - until then the browser head just starts
        // with an empty library rather than running this against nothing.
        if (!OperatingSystem.IsBrowser())
        {
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

        return mainView;
    }
}
