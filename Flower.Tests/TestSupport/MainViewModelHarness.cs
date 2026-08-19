using System;
using System.Collections.Generic;
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
// and SyncHttpServer's listener both need an explicit Start(), which
// MainViewModel's constructor never calls), so building one touches neither
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
    public sealed record Parts(
        MainViewModel Main,
        PlaylistControlViewModel PlaylistControl,
        CurrentlyPlayingControlViewModel CurrentlyPlaying,
        FakeAudioManager Audio,
        AppSettings AppSettings,
        Library Library,
        MainPlaylist MainPlaylist);

    public static MainViewModel Build(Library library, MainPlaylist mainPlaylist, AppSettings? appSettings = null) =>
        BuildParts(library, mainPlaylist, appSettings).Main;

    // appSettings is injectable so a test can construct a MainViewModel that
    // already has, say, a saved PairedServerFingerprint - several things
    // (BuildSidebarItems' pinned paired-server placeholder, the restored last
    // view) only ever happen once, in the constructor, off whatever settings
    // exist at that moment.
    public static Parts BuildParts(Library library, MainPlaylist mainPlaylist, AppSettings? appSettings = null)
    {
        appSettings ??= new AppSettings();
        var audio = new FakeAudioManager();
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var playlistControl = new PlaylistControlViewModel(
            audio, mainPlaylist, library, appSettings, libraryStore, appSettingsStore,
            NullLogger<PlaylistControlViewModel>.Instance);
        var currentlyPlaying = new CurrentlyPlayingControlViewModel(
            playlistControl, audio, library, NullLogger<CurrentlyPlayingControlViewModel>.Instance);

        var deviceIdentity = new DeviceIdentity { Fingerprint = "test-device", Alias = "Test Device" };
        var signingKey = MakeSigningKey();

        var networkDiscovery = new NetworkDiscoveryService(
            deviceIdentity, NullLogger<NetworkDiscoveryService>.Instance, new FakeMdnsBackend());
        var reachability = new PairedServerReachability(networkDiscovery, appSettings);
        var syncStateStore = new PlaylistSyncStateStore(NullLogger<PlaylistSyncStateStore>.Instance);
        var deviceNicknameStore = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        var playlistSyncService = new PlaylistSyncService(
            library, deviceIdentity, signingKey, appSettings, syncStateStore, deviceNicknameStore,
            NullLogger<PlaylistSyncService>.Instance);
        var librarySyncService = new LibrarySyncService(
            library, deviceIdentity, signingKey, appSettings, libraryStore, InMemoryLogStore.Instance,
            NullLogger<LibrarySyncService>.Instance);
        var libraryDownloadService = new LibraryDownloadService(
            library, deviceIdentity, signingKey, appSettings, libraryStore,
            NullLogger<LibraryDownloadService>.Instance);
        var peerPairingService = new PeerPairingService(
            deviceIdentity, signingKey, NullLogger<PeerPairingService>.Instance);
        var peerTrackResolver = new PeerTrackResolver(reachability);
        var trustedPeerStore = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var syncHttpServer = new SyncHttpServer(
            deviceIdentity, signingKey, appSettings, library, trustedPeerStore, new ClientLogStore(),
            NullLogger<SyncHttpServer>.Instance);
        var deviceIdentityStore = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance);

        var busy = new BusyState();
        var main = new MainViewModel(
            playlistControl, library, appSettings, new FakeMusicImporter(), mainPlaylist,
            libraryStore, appSettingsStore, deviceIdentityStore, deviceNicknameStore,
            busy,
            new ITunesImportCoordinator(library, libraryStore, busy, NullLogger<ITunesImportCoordinator>.Instance),
            new AnimationClock(),
            NullLogger<MainViewModel>.Instance,
            networkDiscovery, reachability, playlistSyncService, librarySyncService, libraryDownloadService,
            peerPairingService, peerTrackResolver, syncHttpServer, deviceIdentity, signingKey);

        return new Parts(main, playlistControl, currentlyPlaying, audio, appSettings, library, mainPlaylist);
    }

    public static MobileMainViewModel BuildMobile(Library library, MainPlaylist mainPlaylist)
    {
        var parts = BuildParts(library, mainPlaylist);
        return new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying);
    }
}
