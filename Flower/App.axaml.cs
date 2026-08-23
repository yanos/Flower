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
            .AddSingleton(sp =>
            {
                var library = new Library(
                    sp.GetRequiredService<LibraryStore>().Load(),
                    sp.GetRequiredService<ILogger<Library>>(),
                    sp.GetRequiredService<TrackRepository>(),
                    sp.GetRequiredService<PlaylistRepository>());

                // The app's answer to "is this placeholder's origin still
                // someone we can ask" - the same rule PeerTrackResolver applies
                // before dialing a peer at all, so a rescan cannot decide to
                // keep a row that playback would then refuse to resolve. Read
                // through the settings object on every call rather than
                // captured, because unpairing changes the answer while the
                // library is alive. See Library.IsOriginPaired.
                var appSettings = sp.GetRequiredService<AppSettings>();
                library.IsOriginPaired = fingerprint =>
                    SyncRolePolicy.MayRequestFrom(appSettings.PairedServerFingerprint, fingerprint);

                return library;
            })
            .AddSingleton(sp => new MainPlaylist(sp.GetRequiredService<Library>().Tracks))

            // The platform hook wins when a head has installed one (Android's
            // MediaStore importer); otherwise the shared filesystem scanner.
            // The browser branch below replaces this outright - there are no
            // folders to scan in a sandbox, and its library is a server's.
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
                sp.GetService<ICoverArtUrlResolver>(),
                sp.GetService<IPeerCredentials>(),
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
        {
            RegisterBrowserServices(services);
            return;
        }

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
            .AddSingleton<PeerTrackResolver>()
            // How this device proves who it is to a peer, for every signed call
            // into one (see IPeerCredentials). On this branch because it needs
            // the signing key, which the browser head has no way to produce.
            .AddSingleton<IPeerCredentials, SignedDeviceCredentials>()
            // Registered here, on the peer-stack branch, because that is what it
            // resolves against - a paired peer found over mDNS, addressed with
            // this device's signing key. PlaylistControlViewModel takes it as an
            // optional dependency (see IStreamUrlResolver) precisely so the
            // browser head, which has neither, still constructs.
            .AddSingleton<IStreamUrlResolver, PeerStreamUrlResolver>()
            // Album art resolves against the same peer, by the same rule, and
            // so belongs on the same branch - see ICoverArtUrlResolver.
            .AddSingleton<ICoverArtUrlResolver, PeerCoverArtUrlResolver>();
    }

    // Everything the browser head has instead of the peer-to-peer stack above.
    //
    // A tab is not a device: it cannot sign (no asymmetric crypto under WASM),
    // cannot discover (no mDNS from a sandbox), and has no folders to scan. What
    // it has is one server - the origin it was served from - and one credential,
    // the session token that server minted and put in the page URL. Those two
    // facts are the whole of this method: a credential built from the token, a
    // library pulled from the origin, and stream URLs minted against it.
    //
    // Registered last, so these win over the shared registrations above for the
    // services they replace (IMusicImporter in particular).
    private static void RegisterBrowserServices(IServiceCollection services)
    {
        // Read here rather than where it is used because reading it consumes it
        // - see BrowserSession.
        var session = BrowserSession.FromPageUrl();
        services.AddSingleton(session);

        if (session.Token == null)
        {
            // A tab opened by hand rather than through the desktop client's
            // "Server Settings..." button. It has no authority over the server,
            // so it gets none of what follows and shows an empty library - which
            // is honest, and is what it showed before any of this existed. Said
            // explicitly rather than by omission: the registration this replaces
            // is the filesystem scanner, which would go looking for a music
            // folder inside a browser sandbox.
            services.AddSingleton<Importer.IMusicImporter>(_ => new Importer.EmptyLibraryImporter());
            return;
        }

        var origin = BrowserLocation.Origin;

        services
            // One shared client, which under WASM is a thin wrapper over the
            // browser's own fetch stack rather than anything holding sockets -
            // so the usual reasons not to register an HttpClient as a singleton
            // do not apply here, and there is nothing to dispose.
            .AddSingleton(_ => new HttpClient())

            // The browser's entire authentication story - see
            // AdminSessionCredentials. Registered under both its own type and
            // the interface so the settings overlay can reach the token itself.
            .AddSingleton(new AdminSessionCredentials(session.Token))
            .AddSingleton<IPeerCredentials>(sp => sp.GetRequiredService<AdminSessionCredentials>())

            // The library: the origin server's catalog, as placeholders. This is
            // the registration that makes "local files" versus "a self-hosted
            // server" a choice of IMusicImporter rather than a second code path,
            // which is what that abstraction was introduced for.
            .AddSingleton<Importer.IMusicImporter>(sp => new Importer.OriginLibraryImporter(
                sp.GetRequiredService<HttpClient>(),
                origin.ToString(),
                sp.GetRequiredService<IPeerCredentials>(),
                sp.GetRequiredService<ILogger<Importer.RemoteLibraryImporter>>(),
                sp.GetRequiredService<ILogger<Importer.OriginLibraryImporter>>()))

            // Playback: a ticket per track, because an <audio> element cannot
            // carry credentials of its own - see StreamTicketUrlResolver.
            .AddSingleton<IStreamUrlResolver>(sp => new StreamTicketUrlResolver(
                sp.GetRequiredService<HttpClient>(),
                origin,
                sp.GetRequiredService<IPeerCredentials>(),
                sp.GetRequiredService<ILogger<StreamTicketUrlResolver>>()))

            // Album art: no ticket needed, because AlbumArtLoader fetches it
            // with its own HttpClient and can send the session header that
            // already reaches GET /library - see OriginCoverArtUrlResolver.
            .AddSingleton<ICoverArtUrlResolver>(_ => new OriginCoverArtUrlResolver(origin))

            // The origin's playlists, mirrored read-only into this tab - see
            // OriginPlaylistImporter for why a tab is not a party to the
            // peer-to-peer playlist merge the desktop runs.
            .AddSingleton<Importer.IPlaylistImporter>(sp => new Importer.OriginPlaylistImporter(
                sp.GetRequiredService<HttpClient>(),
                origin.ToString(),
                sp.GetRequiredService<IPeerCredentials>(),
                sp.GetRequiredService<ILogger<Importer.OriginPlaylistImporter>>()))

            // ...and back the other way, which is the one thing a browser tab
            // could not do until now - see IPlaylistWriter. Still not the
            // peer-to-peer merge: a tab's playlists are the server's, so an
            // edit here is an edit there.
            .AddSingleton<Importer.IPlaylistWriter>(sp => new Importer.OriginPlaylistWriter(
                sp.GetRequiredService<HttpClient>(),
                origin.ToString(),
                sp.GetRequiredService<IPeerCredentials>(),
                sp.GetRequiredService<ILogger<Importer.OriginPlaylistWriter>>()))

            // And what the tab *plays*, which until now changed here and
            // stayed here - lost at the next refresh, since a tab's filesystem
            // is an in-memory one. Same premise again: a play of the server's
            // track is the server's play. See IPlayReporter.
            .AddSingleton<Importer.IPlayReporter>(sp => new Importer.OriginPlayReporter(
                sp.GetRequiredService<HttpClient>(),
                origin.ToString(),
                sp.GetRequiredService<IPeerCredentials>(),
                sp.GetRequiredService<ILogger<Importer.OriginPlayReporter>>()));
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

            // Every address the paired server has told us about, registered as
            // a peer so the ordinary poll loop starts probing them. This is
            // what a client off its home network has instead of discovery -
            // mDNS is link-local, so away from home there is nothing to find
            // and this is the only route back to the server. See
            // PairedServerReachability and docs/REMOTE-ACCESS-PLAN.md.
            _ = provider.GetRequiredService<PairedServerReachability>().RestoreRememberedAsync();
        }

        // Refresh the library in the background while the UI is already showing.
        //
        // Whatever IMusicImporter is registered: folders on disk for a desktop
        // or phone, the origin server's catalog for a browser tab (see
        // RegisterBrowserServices). This used to be skipped outright under WASM,
        // back when the only importer was a filesystem scanner and there was
        // nothing for the browser to scan - the sole remaining fork is the two
        // iTunes syncs below, which are about *this* machine's music library and
        // mean nothing for a catalog pulled off a server.
        var importer = provider.GetRequiredService<Importer.IMusicImporter>();
        var isLocalImporter = importer.ScansLocalFiles;
        var mainPlaylist = provider.GetRequiredService<MainPlaylist>();

        // Only the browser registers one - see IPlaylistWriter. PlaylistsChanged
        // rather than PlaylistsUpdated, because this needs to fire for exactly
        // the case that one deliberately skips: a local edit. It is already the
        // event that means "the on-disk copy is stale", and for a head whose
        // playlists live on a server, the server's copy is the on-disk copy.
        var playlistWriter = provider.GetService<Importer.IPlaylistWriter>();
        if (playlistWriter != null)
            library.PlaylistsChanged += (_, _) => playlistWriter.Schedule(library.Playlists);

        // Likewise the browser only - see IPlayReporter. TrackStatsChanged is
        // already the "one track's counters moved" signal both halves of a
        // play raise (the played-at stamp when it starts, the count bump when
        // it ends naturally), and it carries which half it was, so this needs
        // no second subscription and no knowledge of the playback pipeline.
        var playReporter = provider.GetService<Importer.IPlayReporter>();
        if (playReporter != null)
            library.TrackStatsChanged += (_, e) => playReporter.Report(e.Track, e.Change);

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
                // A remote catalog has no paths, and logging this device's
                // (which still default to ~/Music even where nothing can read
                // it) would misdescribe what is about to happen.
                if (isLocalImporter)
                {
                    rescanLogger.LogInformation("Startup rescan starting for paths: {LibraryPaths}", string.Join(", ", appSettings.LibraryPaths));
                }
                else
                {
                    rescanLogger.LogInformation("Startup library refresh starting from the remote catalog");
                }

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

                // Only the browser registers one - see IPlaylistImporter. After
                // UpdateTracks rather than before, because a playlist on the
                // wire names its tracks by description and can only be resolved
                // against a library that has already arrived.
                //
                // ReplacePlaylists, not ResetPlaylists. Reset is for replaying
                // the on-disk set into a Library before any window exists, and
                // deliberately announces nothing; this runs on a background task
                // with the UI already up, so nothing would rebuild the sidebar
                // and the playlists would be fetched and then invisible. Replace
                // also no-ops when the set came back identical, which is the
                // normal case on every rescan after the first.
                if (provider.GetService<Importer.IPlaylistImporter>() is { } playlistImporter)
                {
                    var remotePlaylists = await playlistImporter.ImportAsync(library.Tracks);
                    // Before the install, not after: ReplacePlaylists raises
                    // PlaylistsChanged exactly as a user's own edit does, and
                    // the writer below has no other way to tell the two apart -
                    // without this every rescan would push the server's own
                    // playlists straight back at it.
                    playlistWriter?.NoteOriginState(remotePlaylists);
                    library.ReplacePlaylists(remotePlaylists);
                    rescanLogger.LogInformation("Playlists refreshed from the remote catalog ({PlaylistCount})", remotePlaylists.Count);
                }

                // SyncITunesPlayCountAsync/SyncITunesDateAddedAsync each do their
                // own save (either may run again later via its own Settings
                // checkbox, independent of this startup rescan) and layer their
                // own more specific BusyMessage on top of this outer scope's.
                // Both gated on the master IntegrateWithITunes switch first -
                // with it off, Flower ignores Music.app entirely, whatever
                // these two remember individually. Asked of
                // ITunesIntegration rather than spelled out here, because
                // the server gates its own imports on the same rule.
                if (isLocalImporter && Flower.Importer.ITunesIntegration.ShouldSyncPlayCount(appSettings))
                    await mainViewModel.SyncITunesPlayCountAsync();
                if (isLocalImporter && Flower.Importer.ITunesIntegration.ShouldSyncDateAdded(appSettings))
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

        return mainView;
    }

    // The browser half of the desktop client's "Server Settings..." button.
    //
    // That button mints a short-lived admin session against the server and opens
    // this page at #admin=<token>&page=settings. The page= half is what decides
    // whether the overlay opens: the token alone no longer implies it, now that
    // an ordinary jukebox tab carries one as its library credential. (See
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
            // Off the container, not off the URL: reading the fragment consumes
            // it, and RegisterBrowserServices got there first because the same
            // token is now the credential for the library and for playback too
            // (see BrowserSession).
            var session = Ioc.Default.GetRequiredService<BrowserSession>();
            if (session.Token == null || !string.Equals(session.Page, "settings", StringComparison.OrdinalIgnoreCase))
                return;

            var client = ServerAdminClient.ForSession(
                Ioc.Default.GetRequiredService<HttpClient>(), BrowserLocation.Origin, session.Token);
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
