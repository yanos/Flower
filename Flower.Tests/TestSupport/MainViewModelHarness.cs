using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Logging;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Tests.TestSupport;

// Builds a fully wired MainViewModel - and, on top of it, a
// MobileMainViewModel - out of real dependencies, for tests that need the
// actual thing rather than a stub.
//
// Every one of these dependencies is either a plain data holder or a service
// whose constructor only wires up event subscriptions: none of them start
// network listeners or timers on their own (NetworkDiscoveryService's polling
// needs an explicit Start(), which MainViewModel's constructor never calls),
// so building one touches neither
// the network nor the real filesystem beyond whatever PlatformDataDirectory
// is currently pinned to. Callers must pin it (see PinnedDataDirectory), or
// these will write into the developer's own settings.json.
//
// Extracted from MainViewModelSidebarNavigationTests - the only place this
// existed - once ScreenStackPanelSwipeTests needed the same thing.
public static class MainViewModelHarness
{
    public sealed class FakeMusicImporter : IMusicImporter
    {
        public Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null) =>
            Task.FromResult(new List<Track>());
    }

    // A real EC key pair, just not one anything here actually signs or
    // verifies with.
    public static DeviceSigningKey MakeSigningKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return new DeviceSigningKey(ecdsa, raw);
    }

    // The pieces a test may want to reach past MainViewModel for - it does not
    // expose its own collaborators.
    //
    // Disposable, and every caller must dispose it: PeerSyncCoordinator's
    // constructor starts a periodic _logPushTimer, so a MainViewModel that is
    // built and dropped leaves a DispatcherTimer ticking on the shared headless
    // dispatcher for the rest of the run - the leak written up in
    // AssemblySetup.cs, and the reason a leaked short-interval timer could
    // destabilize unrelated [AvaloniaFact]s. Disposal is also what takes back
    // MainViewModel's sixteen event subscriptions and PlaylistControl's/
    // CurrentlyPlaying's handlers on the shared FakeAudioManager, so one test's
    // ViewModels stop reacting to the next test's events.
    public sealed record Parts(
        MainViewModel Main,
        PlaylistControlViewModel PlaylistControl,
        CurrentlyPlayingControlViewModel CurrentlyPlaying,
        FakeAudioManager Audio,
        AppSettings AppSettings,
        Library Library,
        MainPlaylist MainPlaylist,
        NetworkDiscoveryService NetworkDiscovery,
        PairedServerReachability Reachability,
        FakeMdnsBackend MdnsBackend,
        // Non-null only when the caller asked for stubbed sync services - see
        // BuildParts' stubSyncServices.
        StubLibrarySyncService? StubLibrarySync = null,
        StubPlaylistSyncService? StubPlaylistSync = null) : IDisposable
    {
        // Disposes everything BuildParts constructed that owns a timer, a
        // socket or a subscription - MainViewModel.Dispose already covers its
        // own bag and PeerSyncCoordinator (hence the log-push timer), but the
        // two other ViewModels and the three services were built here and are
        // owned by nobody else. Every member is individually idempotent, so a
        // test that disposes one of them itself (ViewModelDisposalTests does)
        // can still dispose the Parts.
        public void Dispose()
        {
            Main.Dispose();
            PlaylistControl.Dispose();
            CurrentlyPlaying.Dispose();
            Reachability.Dispose();
            NetworkDiscovery.Dispose();
        }
    }

    // The mobile shell plus the desktop Parts underneath it, so a test that
    // only wants MobileMainViewModel still has something to dispose - the
    // MainViewModel it wraps is what owns the log-push timer.
    public sealed record MobileParts(MobileMainViewModel Mobile, Parts Parts) : IDisposable
    {
        public void Dispose()
        {
            Mobile.Dispose();
            Parts.Dispose();
        }
    }

    // Canned answers for the two sync entry points, so a test can drive
    // ForceSyncNow's reachable path (its result strings, the trust
    // confirmation it performs on success) without a peer to sync against -
    // see docs/ARCHITECTURE-REVIEW.md Tier 5.6.
    public sealed class StubLibrarySyncService : LibrarySyncService
    {
        public StubLibrarySyncService(Library library, DeviceIdentity identity, DeviceSigningKey key,
            AppSettings appSettings)
            : base(library, identity, key, appSettings, TestLogArchive.InTempDirectory(),
                   NullLogger<LibrarySyncService>.Instance,
                   NullLogger<RemoteLibraryImporter>.Instance) { }

        public LibrarySyncResult Result { get; set; } = new(Success: true, FetchedCount: 0, AddedCount: 0);
        public List<DiscoveredDevice> SyncedWith { get; } = new();

        public List<DiscoveredDevice> PushedLogsTo { get; } = new();
        public Queue<bool> LogPushResults { get; } = new();

        public override Task<LibrarySyncResult> SyncWithAsync(DiscoveredDevice device)
        {
            SyncedWith.Add(device);
            return Task.FromResult(Result);
        }

        // Recorded separately from SyncedWith: the point of several tests is
        // that the periodic log push happens *without* a catalog pull.
        public override Task<bool> PushLogsOnlyAsync(DiscoveredDevice device)
        {
            PushedLogsTo.Add(device);
            return Task.FromResult(LogPushResults.TryDequeue(out var result) ? result : true);
        }
    }

    public sealed class StubPlaylistSyncService : PlaylistSyncService
    {
        public StubPlaylistSyncService(Library library, DeviceIdentity identity, DeviceSigningKey key,
            AppSettings appSettings, PlaylistSyncStateStore stateStore, DeviceNicknameStore nicknames)
            : base(library, identity, key, appSettings, stateStore, nicknames,
                   NullLogger<PlaylistSyncService>.Instance) { }

        public List<(DiscoveredDevice Device, bool ForceInitiator)> SyncedWith { get; } = new();

        public override Task SyncWithAsync(DiscoveredDevice device, bool forceInitiator = false)
        {
            SyncedWith.Add((device, forceInitiator));
            return Task.CompletedTask;
        }
    }

    // Deliberately returns Parts rather than a bare MainViewModel: the caller
    // has to hold something disposable, or the log-push timer leaks (see Parts).
    public static Parts Build(Library library, MainPlaylist mainPlaylist, AppSettings? appSettings = null) =>
        BuildParts(library, mainPlaylist, appSettings);

    // appSettings is injectable so a test can construct a MainViewModel that
    // already has, say, a saved PairedServerFingerprint - several things
    // (BuildSidebarItems' pinned paired-server placeholder, the restored last
    // view) only ever happen once, in the constructor, off whatever settings
    // exist at that moment.
    // stubSyncServices swaps the two sync entry points for canned ones (see
    // StubLibrarySyncService); discoveryHttpClient answers the /info handshake,
    // so a test can make a peer genuinely discovered - which is what
    // PairedServerReachability.PairedServerDevice, and therefore ForceSyncNow's
    // reachable path, depends on.
    public static Parts BuildParts(
        Library library,
        MainPlaylist mainPlaylist,
        AppSettings? appSettings = null,
        bool stubSyncServices = false,
        HttpClient? discoveryHttpClient = null)
    {
        appSettings ??= new AppSettings();
        var audio = new FakeAudioManager();
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var playlistControl = new PlaylistControlViewModel(
            audio, mainPlaylist, library, appSettings, appSettingsStore,
            NullLogger<PlaylistControlViewModel>.Instance);
        var currentlyPlaying = new CurrentlyPlayingControlViewModel(
            playlistControl, audio, library, new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance), NullLogger<CurrentlyPlayingControlViewModel>.Instance);

        var deviceIdentity = new DeviceIdentity { Fingerprint = "test-device", Alias = "Test Device" };
        var signingKey = MakeSigningKey();

        var mdnsBackend = new FakeMdnsBackend();
        var networkDiscovery = new NetworkDiscoveryService(
            deviceIdentity, NullLogger<NetworkDiscoveryService>.Instance, mdnsBackend, discoveryHttpClient);
        var reachability = new PairedServerReachability(
            networkDiscovery, appSettings, appSettingsStore, NullLogger<PairedServerReachability>.Instance);
        var syncStateStore = new PlaylistSyncStateStore(NullLogger<PlaylistSyncStateStore>.Instance);
        var deviceNicknameStore = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        var stubPlaylistSync = stubSyncServices
            ? new StubPlaylistSyncService(library, deviceIdentity, signingKey, appSettings, syncStateStore, deviceNicknameStore)
            : null;
        var stubLibrarySync = stubSyncServices
            ? new StubLibrarySyncService(library, deviceIdentity, signingKey, appSettings)
            : null;
        var playlistSyncService = (PlaylistSyncService?)stubPlaylistSync ?? new PlaylistSyncService(
            library, deviceIdentity, signingKey, appSettings, syncStateStore, deviceNicknameStore,
            NullLogger<PlaylistSyncService>.Instance);
        var librarySyncService = (LibrarySyncService?)stubLibrarySync ?? new LibrarySyncService(
            library, deviceIdentity, signingKey, appSettings, TestLogArchive.InTempDirectory(),
            NullLogger<LibrarySyncService>.Instance, NullLogger<RemoteLibraryImporter>.Instance);
        var libraryDownloadService = new LibraryDownloadService(
            library, deviceIdentity, signingKey, appSettings,
            NullLogger<LibraryDownloadService>.Instance);
        var peerPairingService = new PeerPairingService(
            deviceIdentity, signingKey, NullLogger<PeerPairingService>.Instance);
        var peerTrackResolver = new PeerTrackResolver(reachability);
        var trustedPeerStore = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var deviceIdentityStore = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance);

        var busy = new BusyState();
        var main = new MainViewModel(
            playlistControl, library, appSettings, new FakeMusicImporter(), mainPlaylist,
            appSettingsStore, deviceIdentityStore, deviceNicknameStore, trustedPeerStore,
            busy,
            new ITunesImportCoordinator(library, busy, NullLogger<ITunesImportCoordinator>.Instance),
            new AnimationClock(),
            new VolumeControlViewModel(audio),
            new OutputDeviceControlViewModel(audio),
            currentlyPlaying,
            new EqualizerViewModel(audio, appSettings, appSettingsStore),
            new SidebarRenameService(deviceNicknameStore, NullLogger<SidebarRenameService>.Instance),
            NullLogger<MainViewModel>.Instance,
            networkDiscovery, reachability, playlistSyncService, librarySyncService, libraryDownloadService,
            peerPairingService, peerTrackResolver, deviceIdentity, signingKey);

        return new Parts(main, playlistControl, currentlyPlaying, audio, appSettings, library, mainPlaylist,
            networkDiscovery, reachability, mdnsBackend, stubLibrarySync, stubPlaylistSync);
    }

    public static MobileParts BuildMobile(Library library, MainPlaylist mainPlaylist)
    {
        var parts = BuildParts(library, mainPlaylist);
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance);
        return new MobileParts(mobile, parts);
    }
}
