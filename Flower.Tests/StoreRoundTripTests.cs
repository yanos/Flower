using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.ViewModels;

namespace Flower.Tests;

// LibraryStore/PlaylistStore/AppSettingsStore resolve their file path via
// AppDataDirectory, which checks PlatformDataDirectory.Current first and
// only falls back to HOME-derived OS folders if that's unset - see
// AppDataDirectory.Path. These tests pin PlatformDataDirectory.Current to an
// isolated temp directory for the lifetime of each test so they never read
// or write the real developer's library.json/playlists.json.
//
// HOME is *also* redirected, for ResolveLibraryXmlPath's Music-folder lookup
// (SpecialFolder.UserProfile-based, not AppDataDirectory) - but HOME alone
// used to be the only guard here, and that was insufficient on GitHub
// Actions' ubuntu runners: AppDataDirectory's non-macOS branch resolves via
// Environment.GetFolderPath(SpecialFolder.LocalApplicationData), which
// checks the XDG_DATA_HOME environment variable *before* falling back to
// $HOME/.local/share - and that runner image has XDG_DATA_HOME pinned to a
// fixed path regardless of HOME, so every test silently shared and polluted
// that one real directory instead of getting its own temp one.
//
// All such tests live in this one class because xUnit runs test methods
// within a class sequentially - spreading this across classes would risk
// two tests mutating these process-wide settings at the same time under
// parallel execution.
[Collection("PlatformDataDirectory")]
public class StoreRoundTripTests : IDisposable
{
    private readonly string? _originalHome;
    private readonly string  _tempHome;

    public StoreRoundTripTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        PlatformDataDirectory.Current = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task LibraryStore_round_trips_tracks_including_duration()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", Duration = TimeSpan.FromSeconds(125), Path = "/music/a.mp3" },
        };

        await new LibraryStore(NullLogger<LibraryStore>.Instance).SaveAsync(tracks);
        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(125), loaded[0].Duration);
    }

    [Fact]
    public async Task LibraryStore_Load_returns_empty_list_when_no_file_exists()
    {
        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();
        Assert.Empty(loaded);
    }

    // Save (synchronous) is the Window.Closing counterpart to SaveAsync - see
    // its doc comment - so a track that just finished (incrementing PlayCount
    // via a fire-and-forget SaveAsync) isn't lost if the app quits before that
    // write lands. Must round-trip identically to the async path.
    [Fact]
    public void LibraryStore_Save_round_trips_tracks_synchronously()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", PlayCount = 1, Duration = TimeSpan.FromSeconds(125), Path = "/music/a.mp3" },
        };

        new LibraryStore(NullLogger<LibraryStore>.Instance).Save(tracks);
        var loaded = new LibraryStore(NullLogger<LibraryStore>.Instance).Load();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(1, loaded[0].PlayCount);
    }

    // Minimal stand-in for GaplessAudioManager, just for raising EndReached below -
    // see PlaylistControlViewModelTests.FakeAudioManager for why that test
    // class never raises EndReached itself (needs a live Avalonia dispatcher).
    // This test avoids that by giving PlaylistControlViewModel an empty
    // current playlist, so GetNextTrack returns null and the handler never
    // reaches its Dispatcher.UIThread.Post call - this test lives here (not
    // there) because it does touch LibraryStore for real and needs this
    // class's HOME redirection.
    private sealed class FakeAudioManager : Flower.Manager.IAudioManager
    {
        public bool IsPlaying { get; set; }
        public int Volume { get; set; }
        public float Position { get; set; }
        public long Time { get; set; }
        public long Length { get; set; }
        public void Play(Track track) { }
        public void SetUpcoming(Track? next) { }
        public void Resume() { }
        public void Pause() { }
        public void Stop() { }
        public void ApplyEqualizer(Flower.Manager.Equalizer? equalizer) { }
        public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);
#pragma warning disable CS0067
        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
        public event EventHandler<Flower.Manager.TrackFailedEventArgs>? TrackFailed;
#pragma warning restore CS0067
    }

    // Regression test for the reported bug: play a track, it counts, but a
    // restart reverts it to 0. Root cause - every launch kicks off a
    // background rescan (App.axaml.cs) that replaces Library.Tracks with
    // brand-new Track instances for every file, even unchanged ones. If that
    // rescan lands while a track is still playing (plenty of time if e.g. the
    // user alt-tabs to Music.app and back), CurrentlyPlayingTrack is left
    // pointing at the old, now-discarded instance. Incrementing PlayCount on
    // that orphaned object used to be silently lost, since it's no longer in
    // Library.Tracks and never gets saved.
    [Fact]
    public void EndReached_increments_PlayCount_on_the_current_library_track_even_if_a_rescan_replaced_it_mid_playback()
    {
        var oldTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { oldTrack });
        var emptyPlaylist = new MainPlaylist(new List<Track>());
        var audio = new FakeAudioManager();
        var vm = new PlaylistControlViewModel(
            audio, emptyPlaylist, library, new AppSettings(), new LibraryStore(NullLogger<LibraryStore>.Instance),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance), NullLogger<PlaylistControlViewModel>.Instance);

        vm.Play(oldTrack);

        // Simulate a rescan landing while oldTrack is still "playing": a
        // brand-new Track instance for the same file replaces it in the library.
        var newTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { newTrack });

        audio.RaiseEndReached();

        Assert.Equal(1, newTrack.PlayCount);
        Assert.Equal(0, oldTrack.PlayCount);
    }

    [Fact]
    public async Task PlaylistStore_round_trips_playlist_by_resolving_track_paths_against_the_library()
    {
        var trackA = new Track { Title = "A", Path = "/music/a.mp3" };
        var trackB = new Track { Title = "B", Path = "/music/b.mp3" };
        var playlist = new Playlist("Favorites", new List<Track> { trackA, trackB });

        await new PlaylistStore().SaveAsync(new List<Playlist> { playlist });

        var loaded = new PlaylistStore().Load(new List<Track> { trackA, trackB });

        Assert.Single(loaded);
        Assert.Equal("Favorites", loaded[0].Name);
        Assert.Equal(new[] { "A", "B" }, loaded[0].Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task PlaylistStore_Load_skips_tracks_no_longer_present_in_the_library()
    {
        var trackA    = new Track { Title = "A",    Path = "/music/a.mp3" };
        var trackGone = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        var playlist  = new Playlist("Favorites", new List<Track> { trackA, trackGone });

        await new PlaylistStore().SaveAsync(new List<Playlist> { playlist });

        // Simulate "Gone" having been removed from the library since the playlist was saved.
        var loaded = new PlaylistStore().Load(new List<Track> { trackA });

        Assert.Single(loaded);
        var only = Assert.Single(loaded[0].Tracks);
        Assert.Equal("A", only.Title);
    }

    [Fact]
    public void PlaylistStore_Load_returns_empty_list_when_no_file_exists()
    {
        var loaded = new PlaylistStore().Load(new List<Track>());
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task PlaylistStore_round_trips_Id_and_UpdatedAt()
    {
        var playlist = new Playlist("Favorites", new List<Track>());
        var originalId = playlist.Id;
        var originalUpdatedAt = playlist.UpdatedAt;

        await new PlaylistStore().SaveAsync(new List<Playlist> { playlist });
        var loaded = new PlaylistStore().Load(new List<Track>());

        var only = Assert.Single(loaded);
        Assert.Equal(originalId, only.Id);
        Assert.Equal(originalUpdatedAt, only.UpdatedAt);
    }

    [Fact]
    public async Task PlaylistStore_renaming_does_not_change_Id()
    {
        var playlist = new Playlist("Old Name", new List<Track>());
        var id = playlist.Id;
        playlist.Name = "New Name";

        await new PlaylistStore().SaveAsync(new List<Playlist> { playlist });
        var loaded = new PlaylistStore().Load(new List<Track>());

        var only = Assert.Single(loaded);
        Assert.Equal(id, only.Id);
        Assert.Equal("New Name", only.Name);
    }

    [Fact]
    public async Task TrustedPeerStore_Approve_then_IsTrusted_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.False(store.IsTrusted("fp-1"));

        await store.ApproveAsync("fp-1", "Yanos's iPhone", "pubkey-1");

        Assert.True(store.IsTrusted("fp-1"));
        var peer = Assert.Single(store.Load());
        Assert.Equal("fp-1", peer.Fingerprint);
        Assert.Equal("Yanos's iPhone", peer.Alias);
        Assert.Equal("pubkey-1", peer.PublicKey);
    }

    [Fact]
    public async Task TrustedPeerStore_GetPublicKey_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.Null(store.GetPublicKey("fp-1"));

        await store.ApproveAsync("fp-1", "Desktop", "pubkey-1");

        Assert.Equal("pubkey-1", store.GetPublicKey("fp-1"));
    }

    // Simulates a trusted-peers.json written before PublicKey existed - such
    // an entry must still deserialize (rather than throwing and silently
    // trusting nobody), but has no usable key: GetPublicKey treats that
    // identically to "not trusted" (see SyncHttpServer's verification path),
    // which is what forces the one-time re-pairing this signing scheme's
    // migration relies on.
    [Fact]
    public void TrustedPeerStore_GetPublicKey_returns_null_for_a_legacy_entry_with_no_key()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TrustedPeerStore.StorePath)!);
        File.WriteAllText(TrustedPeerStore.StorePath,
            """[{"Fingerprint":"fp-legacy","Alias":"Old Desktop","ApprovedAt":"2024-01-01T00:00:00+00:00"}]""");

        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);

        Assert.True(store.IsTrusted("fp-legacy"));
        Assert.Null(store.GetPublicKey("fp-legacy"));
    }

    [Fact]
    public async Task TrustedPeerStore_Revoke_removes_a_previously_approved_peer()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "Desktop", "pubkey-1");
        await store.ApproveAsync("fp-2", "iPad", "pubkey-2");

        await store.RevokeAsync("fp-1");

        Assert.False(store.IsTrusted("fp-1"));
        Assert.True(store.IsTrusted("fp-2"));
    }

    [Fact]
    public async Task TrustedPeerStore_Approve_replaces_rather_than_duplicates_an_existing_fingerprint()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.ApproveAsync("fp-1", "Old Alias", "pubkey-1");

        await store.ApproveAsync("fp-1", "New Alias", "pubkey-1-rotated");

        var peer = Assert.Single(store.Load());
        Assert.Equal("New Alias", peer.Alias);
        Assert.Equal("pubkey-1-rotated", peer.PublicKey);
    }

    [Fact]
    public void TrustedPeerStore_IsTrusted_is_false_when_no_file_exists()
    {
        Assert.False(new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance).IsTrusted("anything"));
    }

    [Fact]
    public async Task TrustedPeerStore_Deny_then_LoadDenied_round_trips()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        Assert.Empty(store.LoadDenied());

        await store.DenyAsync("fp-1", "Suspicious Device");

        var denied = Assert.Single(store.LoadDenied());
        Assert.Equal("fp-1", denied.Fingerprint);
        Assert.Equal("Suspicious Device", denied.Alias);
    }

    [Fact]
    public async Task TrustedPeerStore_ForgetDenial_removes_the_entry()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.DenyAsync("fp-1", "Denied Device");

        await store.ForgetDenialAsync("fp-1");

        Assert.Empty(store.LoadDenied());
    }

    [Fact]
    public async Task TrustedPeerStore_Approve_clears_a_matching_denial()
    {
        var store = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
        await store.DenyAsync("fp-1", "Reconsidered Device");

        await store.ApproveAsync("fp-1", "Reconsidered Device", "pubkey-1");

        Assert.Empty(store.LoadDenied());
        Assert.True(store.IsTrusted("fp-1"));
    }

    [Fact]
    public void DeviceIdentityStore_Load_backfills_a_default_alias_for_a_pre_existing_identity_missing_one()
    {
        // Simulates device.json written before Alias existed - Fingerprint only.
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceIdentityStore.StorePath)!);
        File.WriteAllText(DeviceIdentityStore.StorePath, """{"Fingerprint":"fp-legacy"}""");

        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.False(string.IsNullOrEmpty(identity.Alias));
    }

    // Fingerprint is now derived from the device's signing keypair (see
    // DeviceKeyStore/SignedRequestCanonicalizer.ComputeFingerprint), not an
    // independent value stored in device.json - Load() must overwrite a
    // stale/legacy fingerprint with whatever the caller says is currently
    // derived, not preserve the old one.
    [Fact]
    public void DeviceIdentityStore_Load_overwrites_a_stale_fingerprint_with_the_derived_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceIdentityStore.StorePath)!);
        File.WriteAllText(DeviceIdentityStore.StorePath, """{"Fingerprint":"fp-legacy","Alias":"Desktop"}""");

        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.Equal("fp-derived", identity.Fingerprint);
        Assert.Equal("Desktop", identity.Alias);

        var reloaded = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");
        Assert.Equal("fp-derived", reloaded.Fingerprint);
    }

    [Fact]
    public async Task DeviceIdentityStore_SaveAsync_round_trips_a_renamed_alias()
    {
        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");
        identity.Alias = "Yanos's iPhone";

        await new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).SaveAsync(identity);
        var reloaded = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.Equal("Yanos's iPhone", reloaded.Alias);
        Assert.Equal(identity.Fingerprint, reloaded.Fingerprint);
    }

    [Fact]
    public void DeviceKeyStore_Load_generates_once_and_persists_across_reloads()
    {
        var (key1, publicKeyRaw1) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load();
        var (key2, publicKeyRaw2) = new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load();

        Assert.Equal(Convert.ToBase64String(publicKeyRaw1), Convert.ToBase64String(publicKeyRaw2));
        Assert.Equal(
            key1.ExportParameters(false).Q.X,
            key2.ExportParameters(false).Q.X);
        key1.Dispose();
        key2.Dispose();
    }

    [Fact]
    public void DeviceNicknameStore_Get_returns_null_when_no_nickname_is_set()
    {
        Assert.Null(new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance).Get("fp-1"));
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_then_Get_round_trips_a_nickname()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "Yanos's iPhone");

        Assert.Equal("Yanos's iPhone", store.Get("fp-1"));
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_replaces_rather_than_duplicates_an_existing_fingerprint()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "Old Name");

        await store.SetAsync("fp-1", "New Name");

        Assert.Equal("New Name", store.Get("fp-1"));
        Assert.Single(store.Load());
    }

    [Fact]
    public async Task DeviceNicknameStore_SetAsync_with_an_empty_nickname_clears_the_override()
    {
        var store = new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance);
        await store.SetAsync("fp-1", "A Name");

        await store.SetAsync("fp-1", "");

        Assert.Null(store.Get("fp-1"));
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_window_geometry()
    {
        var settings = new AppSettings
        {
            WindowWidth       = 1024,
            WindowHeight      = 768,
            WindowX           = 50,
            WindowY           = 60,
            WindowIsMaximized = true,
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal(1024, loaded.WindowWidth);
        Assert.Equal(768,  loaded.WindowHeight);
        Assert.Equal(50,   loaded.WindowX);
        Assert.Equal(60,   loaded.WindowY);
        Assert.True(loaded.WindowIsMaximized);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_repeat_and_shuffle_toggles()
    {
        var settings = new AppSettings { IsRepeatEnabled = true, IsShuffleEnabled = true };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.True(loaded.IsRepeatEnabled);
        Assert.True(loaded.IsShuffleEnabled);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_last_view_state()
    {
        var settings = new AppSettings
        {
            LastSidebarKind   = "Playlist",
            LastPlaylistName  = "Favorites",
            LastScrollOffsetY = 1234.5,
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal("Playlist",  loaded.LastSidebarKind);
        Assert.Equal("Favorites", loaded.LastPlaylistName);
        Assert.Equal(1234.5,      loaded.LastScrollOffsetY);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_server_role_and_paired_server()
    {
        var settings = new AppSettings
        {
            IsServer                 = true,
            PairedServerFingerprint  = "abc123",
            PairedServerAlias        = "Living Room Mac",
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.True(loaded.IsServer);
        Assert.Equal("abc123",          loaded.PairedServerFingerprint);
        Assert.Equal("Living Room Mac", loaded.PairedServerAlias);
    }

    [Fact]
    public void AppSettingsStore_Save_is_synchronous_and_round_trips_window_geometry()
    {
        var settings = new AppSettings { WindowWidth = 900, WindowHeight = 600 };

        new AppSettingsStore().Save(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal(900, loaded.WindowWidth);
        Assert.Equal(600, loaded.WindowHeight);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_column_states()
    {
        var settings = new AppSettings
        {
            ColumnStates = new List<ColumnState>
            {
                new() { Id = "Title", IsVisible = true, Width = 197.5, Order = 0 },
                new() { Id = "Artist", IsVisible = false, Width = 150, Order = 1 },
            },
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load().ColumnStates;

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        var title = loaded.Single(s => s.Id == "Title");
        Assert.Equal(197.5, title.Width);
        Assert.True(title.IsVisible);
        var artist = loaded.Single(s => s.Id == "Artist");
        Assert.False(artist.IsVisible);
    }

    [Fact]
    public void ColumnManager_Flush_synchronously_persists_widths_for_the_next_launch()
    {
        // Simulates a resize followed immediately by quitting: Flush() must land
        // on disk without waiting for the normal 500ms debounce (see
        // ColumnManager.ScheduleSave), which the process might not survive long
        // enough to complete.
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var first = new ColumnManager(new AppSettings(), appSettingsStore);
        var title = first.Columns.Single(c => c.Id == "Title");
        title.Width = 321;
        first.Flush();

        // A brand-new ColumnManager reading the just-persisted settings.json
        // simulates the next app launch.
        var second = new ColumnManager(appSettingsStore.Load(), appSettingsStore);
        Assert.Equal(321, second.Columns.Single(c => c.Id == "Title").Width);
    }

    [Fact]
    public void PlaylistStore_Load_assigns_a_fresh_Id_to_pre_sync_records_missing_one()
    {
        // Simulates playlists.json written before Playlist.Id existed: the JSON
        // has no "id" property at all, which PlaylistRecord's default parameter
        // deserializes as Guid.Empty - Load must not persist that sentinel.
        var trackA = new Track { Title = "A", Path = "/music/a.mp3" };
        Directory.CreateDirectory(Path.GetDirectoryName(PlaylistStore.StorePath)!);
        File.WriteAllText(PlaylistStore.StorePath, """[{"Name":"Legacy","TrackPaths":["/music/a.mp3"]}]""");

        var loaded = new PlaylistStore().Load(new List<Track> { trackA });

        var only = Assert.Single(loaded);
        Assert.NotEqual(Guid.Empty, only.Id);
    }

    // These test ApplyFromXmlFile directly, against an explicit synthetic XML
    // path, rather than the full Apply(tracks) entry point - Apply() always
    // tries a *live* AppleScript export from Music.app first (see
    // ITunesPlayCountImporter's class comment) and wins on any machine that
    // actually has Music.app installed, including the one this was developed
    // on, which would make these tests see real library data instead of the
    // synthetic fixture below.

    [Fact]
    public void ITunesPlayCountImporter_applies_play_count_from_a_library_export()
    {
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        File.WriteAllText(xmlPath, SampleLibraryXml(17));

        // Deliberately a completely different path than anything in the XML -
        // matching is by Track.SyncKey (title/artist/album/duration), not
        // path, precisely because a real classic-iTunes export's paths don't
        // survive Apple's later iTunes-to-Music.app migration (confirmed
        // against a real library: the export still pointed at
        // ~/Music/iTunes/iTunes Music/..., while the actual files had long
        // since moved to ~/Music/Music/Media.localized/...).
        var track = new Track
        {
            Title = "Test Song", Artists = "Test Artist", Album = "Test Album",
            Duration = TimeSpan.FromSeconds(200),
            Path = "/completely/different/path/song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_falls_back_to_title_artist_album_when_duration_disagrees_but_is_unambiguous()
    {
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        File.WriteAllText(xmlPath, SampleLibraryXml(17));

        // Same title/artist/album as the XML entry, but a very different
        // length - confirmed against a real VBR-encoded MP3 where TagLib's
        // parsed duration and Music.app's own recorded Total Time disagreed
        // by ~10 minutes (a known old-iTunes VBR-header mis-parse), not a
        // rounding-boundary fraction of a second. There's only one candidate
        // in the XML at this title/artist/album, so Track.BuildLooseKey's
        // fallback (see its own doc comment) still matches it.
        var track = new Track
        {
            Title = "Test Song", Artists = "Test Artist", Album = "Test Album",
            Duration = TimeSpan.FromSeconds(45),
            Path = "/music/song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_does_not_guess_between_two_entries_with_different_durations()
    {
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        // Two distinct XML entries share the same title/artist/album but have
        // different durations from each other (and from the local track below)
        // - genuinely ambiguous (could be, say, a studio cut and a live version
        // sharing sloppy tags), so neither the exact key nor the loose-key
        // fallback should guess which one the local track corresponds to.
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    <key>1001</key>
                    <dict>
                        <key>Name</key><string>Test Song</string>
                        <key>Artist</key><string>Test Artist</string>
                        <key>Album</key><string>Test Album</string>
                        <key>Total Time</key><integer>200000</integer>
                        <key>Play Count</key><integer>17</integer>
                    </dict>
                    <key>1002</key>
                    <dict>
                        <key>Name</key><string>Test Song</string>
                        <key>Artist</key><string>Test Artist</string>
                        <key>Album</key><string>Test Album</string>
                        <key>Total Time</key><integer>300000</integer>
                        <key>Play Count</key><integer>9</integer>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);

        var track = new Track
        {
            Title = "Test Song", Artists = "Test Artist", Album = "Test Album",
            Duration = TimeSpan.FromSeconds(45),
            Path = "/music/song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(0, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_matches_by_path_when_metadata_disagrees()
    {
        // Confirmed against a real track whose Artist tag had been edited to
        // add a native-language name ("Takashi Kokubo (小久保隆)") after
        // Music.app last indexed it, leaving Music.app's own record at plain
        // "Takashi Kokubo" - metadata-based matching (exact or loose) can
        // never bridge a genuine content difference like this, but Location
        // still points at the exact same file, so path match (tried first)
        // does.
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    <key>1001</key>
                    <dict>
                        <key>Name</key><string>Song</string>
                        <key>Artist</key><string>Old Artist Name</string>
                        <key>Album</key><string>Album</string>
                        <key>Total Time</key><integer>75023</integer>
                        <key>Play Count</key><integer>17</integer>
                        <key>Location</key><string>file:///Users/test/Music/Music/Media.localized/Music/Artist/Album/01%20Song.mp3</string>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);

        var track = new Track
        {
            Title = "Song", Artists = "New Artist Name (Native Name)", Album = "Album", Duration = TimeSpan.FromSeconds(75.031),
            Path = "/Users/test/Music/Music/Media.localized/Music/Artist/Album/01 Song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_matches_by_path_despite_different_unicode_normalization()
    {
        // Confirmed against a real file whose name contains "é": iTunes'
        // Location URL had it as the decomposed form ("e" + a combining
        // acute accent, U+0301 - written here as "é") while the local
        // Track.Path used the precomposed single-codepoint form ("é") -
        // visually identical, but byte-for-byte different, so the path match
        // silently found nothing until both sides were normalized the same way.
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    <key>1001</key>
                    <dict>
                        <key>Name</key><string>Song</string>
                        <key>Artist</key><string>Artist</string>
                        <key>Album</key><string>Album</string>
                        <key>Total Time</key><integer>75023</integer>
                        <key>Play Count</key><integer>17</integer>
                        <key>Location</key><string>file:///Users/test/Music/Music/Media.localized/Music/Artist/Album/01%20De%CC%81ja.mp3</string>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);

        var track = new Track
        {
            Title = "Song", Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(75.031),
            Path = "/Users/test/Music/Music/Media.localized/Music/Artist/Album/01 Déja.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_leaves_ImportedPlayCount_alone_for_a_nonexistent_file()
    {
        var track = new Track { Title = "Test Song", Path = "/music/song.mp3", ImportedPlayCount = 3 };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, Path.Combine(_tempHome, "does-not-exist.xml"));

        Assert.Equal(3, track.ImportedPlayCount);
    }

    // ResolveLibraryXmlPath is the fallback used when Apply()'s live export
    // isn't available - tested directly here (rather than through Apply)
    // since it's a pure, deterministic function of what's on disk.

    [Fact]
    public void ResolveLibraryXmlPath_prefers_the_Music_app_export_when_both_exist()
    {
        if (!OperatingSystem.IsMacOS())
            return; // the resolver itself is macOS-only - nothing to exercise elsewhere

        // A machine that migrated from classic iTunes to Music.app can easily
        // have both files sitting on disk - the Music.app one is the actively
        // maintained one and must win, not whichever happens to be checked first.
        var musicLibraryDir = Path.Combine(_tempHome, "Music", "Music");
        Directory.CreateDirectory(musicLibraryDir);
        var musicAppPath = Path.Combine(musicLibraryDir, "Music Library.xml");
        File.WriteAllText(musicAppPath, SampleLibraryXml(99));

        var iTunesDir = Path.Combine(_tempHome, "Music", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        File.WriteAllText(Path.Combine(iTunesDir, "iTunes Music Library.xml"), SampleLibraryXml(4));

        Assert.Equal(musicAppPath, ITunesPlayCountImporter.ResolveLibraryXmlPath());
    }

    [Fact]
    public void ResolveLibraryXmlPath_finds_a_classic_iTunes_export_when_thats_all_that_exists()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // Regression coverage for the actual bug: a real classic-iTunes export
        // lives at "~/Music/iTunes/iTunes Music Library.xml" - a different
        // folder ("iTunes", not "Music") AND filename ("iTunes Music Library.xml",
        // not "iTunes Library.xml") than originally guessed, so this silently
        // found nothing on a real machine that actually had one.
        var iTunesDir = Path.Combine(_tempHome, "Music", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        var iTunesPath = Path.Combine(iTunesDir, "iTunes Music Library.xml");
        File.WriteAllText(iTunesPath, SampleLibraryXml(4));

        Assert.Equal(iTunesPath, ITunesPlayCountImporter.ResolveLibraryXmlPath());
    }

    // ── Playlist membership persistence ──────────────────────────────────────

    // PlaylistStore used to persist entries as `t.Path` and filter out anything
    // with a null Path - so adding a synced-but-not-yet-downloaded track (see
    // SYNC-PLAN.md Phase 3) to a playlist worked until the save, then the entry
    // was gone on next launch with nothing logged.
    [Fact]
    public async Task PlaylistStore_round_trips_a_not_yet_downloaded_placeholder_track()
    {
        var placeholder = new Track { Title = "Synced", Path = null, OriginDeviceFingerprint = "peer-fp" };
        var local       = new Track { Title = "Local",  Path = "/music/local.mp3" };
        var store       = new PlaylistStore(NullLogger<PlaylistStore>.Instance);

        await store.SaveAsync(new[] { new Playlist("Mix", new List<Track> { local, placeholder }) });
        var loaded = store.Load(new List<Track> { local, placeholder });

        var tracks = Assert.Single(loaded).Tracks;
        Assert.Equal(2, tracks.Count);
        Assert.Same(placeholder, tracks[1]);
    }

    // The pre-Track.Id shape: a playlists.json with TrackPaths and no Tracks
    // must still resolve, because the first launch after upgrading has a
    // library.json with no ids either (they're minted on load).
    [Fact]
    public void PlaylistStore_still_loads_a_legacy_path_only_playlists_file()
    {
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Directory.CreateDirectory(Path.GetDirectoryName(PlaylistStore.StorePath)!);
        File.WriteAllText(PlaylistStore.StorePath,
            """[{"Name":"Legacy Mix","TrackPaths":["/music/a.mp3"]}]""");

        var loaded = new PlaylistStore(NullLogger<PlaylistStore>.Instance).Load(new List<Track> { track });

        var playlist = Assert.Single(loaded);
        Assert.Equal("Legacy Mix", playlist.Name);
        Assert.Same(track, Assert.Single(playlist.Tracks));
    }

    // ── Crash-safety (AtomicJsonFile) ────────────────────────────────────────
    //
    // Before AtomicJsonFile, every store wrote straight over its live file, so
    // a crash mid-write truncated it - and every Load() catches a bad parse and
    // returns "empty". For library.json that isn't a degraded read, it's total
    // loss: the startup rescan then rewrites the file with DateAdded defaulted
    // to now and every PlayCount back to 0, and play counts/DateAdded/
    // LastPlayedAt/RemotePlayCounts exist nowhere else. These pin the recovery.

    [Fact]
    public async Task LibraryStore_recovers_play_counts_from_the_backup_when_the_live_file_is_truncated()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", PlayCount = 7, Path = "/music/a.mp3" },
        };

        // Two saves: the first creates the file, the second rotates it into .bak.
        await store.SaveAsync(tracks);
        await store.SaveAsync(tracks);

        Truncate(LibraryStore.StorePath);

        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal(7, loaded[0].PlayCount);
    }

    // The recovered contents go back to the live path, so a user who quits
    // before the next save still keeps them.
    [Fact]
    public async Task LibraryStore_writes_the_recovered_contents_back_to_the_live_file()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var tracks = new List<Track> { new Track { Title = "A", PlayCount = 3, Path = "/music/a.mp3" } };

        await store.SaveAsync(tracks);
        await store.SaveAsync(tracks);
        Truncate(LibraryStore.StorePath);

        store.Load();

        // A second, independent store instance reads the live file with no
        // recovery step of its own.
        var reloaded = new LibraryStore(NullLogger<LibraryStore>.Instance).Load();
        Assert.Single(reloaded);
        Assert.Equal(3, reloaded[0].PlayCount);
    }

    // With no backup to fall back on the data really is gone, but the bad file
    // is preserved for a bug report rather than being silently overwritten by
    // the next save.
    [Fact]
    public async Task LibraryStore_quarantines_an_unreadable_file_when_there_is_no_backup()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        await store.SaveAsync(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });
        Truncate(LibraryStore.StorePath);

        var loaded = store.Load();

        Assert.Empty(loaded);
        Assert.True(File.Exists(AtomicJsonFile.CorruptPath(LibraryStore.StorePath)));
    }

    [Fact]
    public async Task AtomicJsonFile_leaves_no_temp_file_behind_after_a_successful_write()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        await store.SaveAsync(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        Assert.False(File.Exists(LibraryStore.StorePath + ".tmp"));
        Assert.True(File.Exists(LibraryStore.StorePath));
    }

    // AppSettingsStore was the one store with no write lock, while
    // ColumnManager's debounced save fires on every pixel of a column-resize
    // drag - overlapping writes were routine, and on Windows they threw.
    [Fact]
    public async Task AppSettingsStore_survives_many_concurrent_saves()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => store.SaveAsync(settings)));

        var reloaded = store.Load();
        Assert.Equal("Album", reloaded.SortColumn);
        Assert.False(reloaded.SortAscending);
    }

    // Half a file is the realistic crash shape: valid JSON prefix, no closing
    // bracket. An empty file would also fail to parse, but wouldn't prove the
    // recovery path handles partial content.
    private static void Truncate(string path)
    {
        var contents = File.ReadAllText(path);
        File.WriteAllText(path, contents[..(contents.Length / 2)]);
    }

    // 200 seconds (200000 ms) - matches Track.BuildSyncKey's (int)Duration.TotalSeconds
    // truncation on the Flower side for a Duration of exactly 200 seconds.
    private static string SampleLibraryXml(int playCount) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Tracks</key>
            <dict>
                <key>1001</key>
                <dict>
                    <key>Name</key><string>Test Song</string>
                    <key>Artist</key><string>Test Artist</string>
                    <key>Album</key><string>Test Album</string>
                    <key>Total Time</key><integer>200000</integer>
                    <key>Play Count</key><integer>{playCount}</integer>
                    <key>Location</key><string>file:///Users/someone/Music/iTunes/iTunes%20Music/Test%20Artist/song.mp3</string>
                </dict>
            </dict>
        </dict>
        </plist>
        """;

    // ── Tier 1.1: coalesced library saves ─────────────────────────────────────

    [Fact]
    public async Task ScheduleSave_coalesces_a_burst_into_a_single_write()
    {
        var store  = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var tracks = new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } };

        // Playing one song fires two of these (RecordPlayed on Play,
        // IncrementPlayCount on EndReached). Neither should hit the disk on its
        // own - that write-per-event, over the whole library, is what Tier 1.1
        // set out to remove.
        store.ScheduleSave(tracks);
        store.ScheduleSave(tracks);

        Assert.False(File.Exists(LibraryStore.StorePath));

        store.Flush();

        Assert.True(File.Exists(LibraryStore.StorePath));
        var reloaded = store.Load();
        Assert.Equal("A", Assert.Single(reloaded).Title);
    }

    [Fact]
    public void Flush_with_nothing_pending_writes_nothing()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);

        store.Flush();

        Assert.False(File.Exists(LibraryStore.StorePath));
    }

    [Fact]
    public async Task Save_supersedes_a_pending_ScheduleSave_rather_than_being_overwritten_by_it()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);

        store.ScheduleSave(new List<Track> { new Track { Title = "Stale" } });
        // The app-exit path (MainWindow's Closing handler): an explicit
        // synchronous save while a debounced one is still queued. The queued one
        // must not fire afterwards and put the older snapshot back.
        store.Save(new List<Track> { new Track { Title = "Current" } });

        await Task.Delay(200);

        Assert.Equal("Current", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public async Task Library_json_is_written_without_indentation_or_null_properties()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        await store.SaveAsync(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        var json = await File.ReadAllTextAsync(LibraryStore.StorePath);

        // Tier 1.1's cheap size win - roughly 60% of the old file was
        // indentation and null-valued properties spelled out in full.
        Assert.DoesNotContain("\n  ", json);
        Assert.DoesNotContain("null", json);
        // Still round-trips, which is the only thing that actually matters.
        Assert.Equal("A", Assert.Single(store.Load()).Title);
    }
}
