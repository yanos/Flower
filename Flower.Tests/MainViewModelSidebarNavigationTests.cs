using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// Regression coverage for "switching sidebar views flashes the previous
// view's tracks before the new ones appear" - a plain sidebar click
// (Songs/Albums/Artists/a playlist) used to go through the same 250ms
// ScheduleFilter debounce as typing into the search box, so the old view's
// Rows stayed on screen for that whole delay after the new view was already
// visible. MainViewModel.ApplySubItemSelection's immediate:true path (used
// only by OnSidebarSelectionChanged) bypasses that debounce the same way
// RebuildRowsImmediatelyAsync already does for mobile's drill-in navigation.
//
// Isolated under PlatformDataDirectory the same way PlaylistPlaybackIntegrationTests/
// LibraryDownloadServiceTests are - MainViewModel's constructor wires up
// LibraryStore/AppSettingsStore, which resolve their on-disk path from it.
[Collection("PlatformDataDirectory")]
public class MainViewModelSidebarNavigationTests : IDisposable
{
    private readonly string _tempHome;

    public MainViewModelSidebarNavigationTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track T(string title) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private sealed class FakeMusicImporter : IMusicImporter
    {
        public Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null) =>
            Task.FromResult(new List<Track>());
    }

    // Mirrors LibraryDownloadServiceTests.MakeSigningKey - a real EC key pair,
    // just not one anything here actually signs/verifies with.
    private static DeviceSigningKey MakeSigningKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return new DeviceSigningKey(ecdsa, raw);
    }

    // Every one of these dependencies is either a plain data holder or a
    // service whose constructor only wires up event subscriptions - none of
    // them start network listeners/timers on their own (NetworkDiscoveryService's
    // polling and SyncHttpServer's listener both need an explicit Start(),
    // which MainViewModel's constructor never calls), so building a full
    // MainViewModel here doesn't touch the network or the real filesystem
    // beyond the temp PlatformDataDirectory above.
    private static MainViewModel MakeViewModel(Library library, MainPlaylist mainPlaylist)
    {
        var appSettings = new AppSettings();
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var playlistControl = new PlaylistControlViewModel(
            new FakeAudioManager(), mainPlaylist, library, appSettings, libraryStore, appSettingsStore,
            NullLogger<PlaylistControlViewModel>.Instance);

        var deviceIdentity = new DeviceIdentity { Fingerprint = "test-device", Alias = "Test Device" };
        var signingKey = MakeSigningKey();

        var networkDiscovery = new NetworkDiscoveryService(deviceIdentity, NullLogger<NetworkDiscoveryService>.Instance, new FakeMdnsBackend());
        var reachability = new PairedServerReachability(networkDiscovery, appSettings);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance);
        var syncStateStore = new PlaylistSyncStateStore(NullLogger<PlaylistSyncStateStore>.Instance);
        var deviceNicknameStore = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        var playlistSyncService = new PlaylistSyncService(
            library, deviceIdentity, signingKey, appSettings, playlistStore, syncStateStore, deviceNicknameStore,
            NullLogger<PlaylistSyncService>.Instance);
        var librarySyncService = new LibrarySyncService(
            library, deviceIdentity, signingKey, appSettings, libraryStore, InMemoryLogStore.Instance,
            NullLogger<LibrarySyncService>.Instance);
        var libraryDownloadService = new LibraryDownloadService(
            library, deviceIdentity, signingKey, appSettings, libraryStore, NullLogger<LibraryDownloadService>.Instance);
        var peerPairingService = new PeerPairingService(deviceIdentity, signingKey, NullLogger<PeerPairingService>.Instance);
        var peerTrackResolver = new PeerTrackResolver(reachability);
        var trustedPeerStore = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        var syncHttpServer = new SyncHttpServer(
            deviceIdentity, signingKey, appSettings, library, playlistStore, trustedPeerStore, new ClientLogStore(),
            NullLogger<SyncHttpServer>.Instance);
        var deviceIdentityStore = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance);

        return new MainViewModel(
            playlistControl, library, appSettings, new FakeMusicImporter(), mainPlaylist,
            libraryStore, appSettingsStore, playlistStore, deviceIdentityStore, deviceNicknameStore,
            NullLogger<MainViewModel>.Instance,
            networkDiscovery, reachability, playlistSyncService, librarySyncService, libraryDownloadService,
            peerPairingService, peerTrackResolver, syncHttpServer, deviceIdentity, signingKey);
    }

    [AvaloniaFact]
    public async Task Switching_sidebar_view_updates_Rows_well_under_the_search_debounce()
    {
        var trackA = T("A");
        var trackB = T("B");
        var trackC = T("C");
        var library = new Library(new List<Track> { trackA, trackB, trackC });
        library.Playlists.Add(new Playlist("Just A", new List<Track> { trackA }));
        var mainPlaylist = new MainPlaylist(library.Tracks);

        var vm = MakeViewModel(library, mainPlaylist);

        // Land on the playlist first and let its own rebuild fully settle
        // before timing the switch under test.
        var playlistItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Playlist);
        vm.SelectedSidebarItem = playlistItem;
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Single(vm.Rows);

        // Switching to Songs used to go through ScheduleFilter's 250ms
        // debounce like a search-box keystroke, so Rows would still show the
        // playlist's one track this soon after switching. Awaiting well
        // under that (50ms) proves the sidebar-switch path no longer waits
        // for it.
        var songsItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Songs);
        vm.SelectedSidebarItem = songsItem;
        await Task.Delay(50);

        Assert.Equal(3, vm.Rows.Count);
    }
}
