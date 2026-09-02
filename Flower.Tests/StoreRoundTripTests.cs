using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Controls;
using Flower.Tests.TestSupport;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
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
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task LibraryStore_round_trips_tracks_including_duration()
    {
        var tracks = new List<Track>
        {
            new Track { Title = "A", Artists = "X", Duration = TimeSpan.FromSeconds(125), Path = "/music/a.mp3" },
        };

        Repo().ReplaceAll(tracks);
        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(125), loaded[0].Duration);
    }

    // IsLocallyDownloaded decides whether the next rescan is allowed to drop a
    // track (see Library.UpdateTracks), so it is only any use if it survives a
    // restart - and the tracks it matters most for are the mobile ones in
    // app-private storage, on the platform most likely to be killed between
    // launches. The row mapper reads by ordinal, which makes an added column a
    // silent-corruption risk rather than a compile error, so pin both values.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LibraryStore_round_trips_whether_a_track_was_downloaded_here(bool downloaded)
    {
        Repo().ReplaceAll(new List<Track>
        {
            new Track { Title = "A", Path = "/private/downloads/a.mp3", IsLocallyDownloaded = downloaded },
        });

        var loaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();

        Assert.Equal(downloaded, Assert.Single(loaded).IsLocallyDownloaded);
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

        Repo().ReplaceAll(tracks);
        var loaded = new LibraryStore(NullLogger<LibraryStore>.Instance).Load();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(1, loaded[0].PlayCount);
    }

    // Minimal stand-in for GaplessAudioManager, just for raising EndReached below -
    // see PlaylistControlViewModelTests.FakeAudioManager for why that test
    // class never raises EndReached itself (needs a live Avalonia dispatcher).
    // This test avoids that by giving PlaylistControlViewModel an empty
    // current playlist, so there is no next track and the handler never
    // reaches its Dispatcher.UIThread.Post call - this test lives here (not
    // there) because it does touch LibraryStore for real and needs this
    // class's HOME redirection.
    private sealed class FakeAudioManager : Flower.Manager.IAudioManager
    {
        public bool IsPlaying { get; set; }
        public int Volume { get; set; }
        public int VolumeOffset { get; set; }
        public float Position { get; set; }
        public long Time { get; set; }
        public long Length { get; set; }
        public void Play(Track track) { }
        public void SetUpcoming(Track? next) { }
        public void Resume() { }
        public void Pause() { }
        public void Stop() { }
        public void ApplyEqualizer(Flower.Manager.Equalizer? equalizer) { }
        public System.Collections.Generic.IReadOnlyList<Flower.Manager.AudioOutputDevice> GetOutputDevices() => [];
        public string? OutputDeviceId => null;
        public void SetOutputDevice(string? deviceId) { }
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
            audio, emptyPlaylist, library, new AppSettings(),
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

        PlaylistRepo().Save(new List<Playlist> { playlist });

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

        PlaylistRepo().Save(new List<Playlist> { playlist });

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

        PlaylistRepo().Save(new List<Playlist> { playlist });
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

        PlaylistRepo().Save(new List<Playlist> { playlist });
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
    // identically to "not trusted" (see PeerSignatureAuth.VerifyTrustedPeer),
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
    public void DeviceIdentityStore_Load_seeds_a_default_alias_on_first_run()
    {
        var identity = new DeviceIdentityStore(NullLogger<DeviceIdentityStore>.Instance).Load("fp-derived");

        Assert.False(string.IsNullOrEmpty(identity.Alias));
        Assert.Equal("fp-derived", identity.Fingerprint);
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
    public void DeviceKeyStore_writes_the_private_key_readable_only_by_its_owner()
    {
        // POSIX modes only - on Windows the file relies on the per-user
        // profile directory's ACL instead, and SetUnixFileMode throws.
        if (OperatingSystem.IsWindows())
            return;

        // The key sits in plaintext JSON (no OS keychain - see DeviceKeyStore's
        // own remarks), so 0600 is what keeps another local user on a shared
        // machine from lifting this device's identity outright.
        new DeviceKeyStore(NullLogger<DeviceKeyStore>.Instance).Load().Key.Dispose();

        var mode = File.GetUnixFileMode(DeviceKeyStore.StorePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
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
    public async Task AppSettingsStore_round_trips_the_paired_server()
    {
        var settings = new AppSettings
        {
            PairedServerFingerprint  = "abc123",
            PairedServerAlias        = "Living Room Mac",
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

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

        PlaylistRepo().Save(new[] { new Playlist("Mix", new List<Track> { local, placeholder }) });
        var loaded = store.Load(new List<Track> { local, placeholder });

        var tracks = Assert.Single(loaded).Tracks;
        Assert.Equal(2, tracks.Count);
        Assert.Same(placeholder, tracks[1]);
    }

    // Track.Id is the only thing a playlist entry stores, so an entry naming a
    // track the library no longer has is simply dropped - there's no path
    // fallback to rescue it with.
    [Fact]
    public async Task PlaylistStore_drops_entries_whose_track_is_gone_from_the_library()
    {
        var kept = new Track { Title = "A", Path = "/music/a.mp3" };
        var gone = new Track { Title = "B", Path = "/music/b.mp3" };
        var store = new PlaylistStore(NullLogger<PlaylistStore>.Instance);

        PlaylistRepo().Save(new[] { new Playlist("Mix", new List<Track> { kept, gone }) });
        var loaded = store.Load(new List<Track> { kept });

        var playlist = Assert.Single(loaded);
        Assert.Same(kept, Assert.Single(playlist.Tracks));
    }

    // ── Tier 4.1: one-time import of the pre-SQLite JSON stores ──────────────

    [Fact]
    public void The_JSON_library_and_playlists_are_imported_once_and_renamed_aside()
    {
        var track = new Track
        {
            Title = "Imported", Artists = "X", Path = "/music/a.mp3",
            PlayCount = 12, ImportedPlayCount = 3,
            DateAdded = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        WriteLegacyJson([track], [("Old Mix", [track])]);

        var db = new FlowerDb(FlowerDb.DefaultPath);
        JsonLibraryImport.RunIfNeeded(db, NullLogger.Instance);

        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var imported = Assert.Single(libraryStore.Load());
        Assert.Equal("Imported", imported.Title);
        // The stats that exist nowhere else are the whole reason this is an
        // import rather than a rescan.
        Assert.Equal(12, imported.PlayCount);
        Assert.Equal(3, imported.ImportedPlayCount);
        Assert.Equal(track.DateAdded, imported.DateAdded);

        var playlist = Assert.Single(new PlaylistStore(NullLogger<PlaylistStore>.Instance, db).Load(libraryStore.Load()));
        Assert.Equal("Old Mix", playlist.Name);
        Assert.Equal("Imported", Assert.Single(playlist.Tracks).Title);

        // Renamed aside, not deleted - and its absence is what stops a second
        // import from running.
        Assert.False(File.Exists(Path.Combine(_tempHome, "library.json")));
        Assert.True(File.Exists(Path.Combine(_tempHome, "library.json" + JsonLibraryImport.ImportedSuffix)));
        Assert.False(File.Exists(Path.Combine(_tempHome, "playlists.json")));
    }

    [Fact]
    public async Task A_second_import_does_not_run_and_cannot_resurrect_deleted_tracks()
    {
        var track = new Track { Title = "Old", Path = "/music/a.mp3" };
        WriteLegacyJson([track], []);

        var db = new FlowerDb(FlowerDb.DefaultPath);
        JsonLibraryImport.RunIfNeeded(db, NullLogger.Instance);

        // The user then deletes that track and adds another.
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        Repo(db).ReplaceAll([new Track { Title = "New", Path = "/music/b.mp3" }]);

        JsonLibraryImport.RunIfNeeded(db, NullLogger.Instance);

        Assert.Equal("New", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public void An_unreadable_JSON_library_is_not_imported_as_an_empty_one()
    {
        // AtomicJsonFile.Read catches a bad parse, quarantines the file and
        // returns null rather than throwing - so an import that treated null
        // as "no tracks" would write an empty library, rename the source aside
        // as though it had worked, and leave the next rescan to reset every
        // play count to zero. The file must end up quarantined by the JSON
        // layer, never marked imported.
        var libraryJson = Path.Combine(_tempHome, "library.json");
        File.WriteAllText(libraryJson, "{ not json");

        var db = new FlowerDb(FlowerDb.DefaultPath);
        JsonLibraryImport.RunIfNeeded(db, NullLogger.Instance);

        Assert.False(File.Exists(libraryJson + JsonLibraryImport.ImportedSuffix));
        Assert.True(File.Exists(AtomicJsonFile.CorruptPath(libraryJson)));
        Assert.Empty(new LibraryStore(NullLogger<LibraryStore>.Instance, db).Load());
    }

    [Fact]
    public void Importing_with_no_JSON_present_is_a_no_op()
    {
        var db = new FlowerDb(FlowerDb.DefaultPath);
        JsonLibraryImport.RunIfNeeded(db, NullLogger.Instance);

        Assert.Empty(new LibraryStore(NullLogger<LibraryStore>.Instance, db).Load());
    }

    // Writes the two legacy files in exactly the shape the JSON stores used to
    // produce, so the import is exercised against the real format rather than
    // against a hand-built approximation of it.
    private void WriteLegacyJson(List<Track> tracks, List<(string Name, List<Track> Tracks)> playlists)
    {
        AtomicJsonFile.Write(
            Path.Combine(_tempHome, "library.json"),
            tracks,
            FlowerJsonContext.Default.TrackEnumerable);

        var records = playlists
            .Select(p => new JsonLibraryImport.PlaylistRecord(
                p.Name,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                p.Tracks.Select(t => new JsonLibraryImport.PlaylistTrackRecord(t.Id)).ToList()))
            .ToList();

        AtomicJsonFile.Write(
            Path.Combine(_tempHome, "playlists.json"),
            records,
            FlowerJsonContext.Default.PlaylistRecordList);
    }

    // ── Crash-safety (AtomicJsonFile) ────────────────────────────────────────
    //
    // Before AtomicJsonFile, every store wrote straight over its live file, so
    // a crash mid-write truncated it - and every Load() catches a bad parse and
    // returns "empty", silently discarding the user's state. These pin the
    // recovery.
    //
    // Driven through AppSettingsStore rather than LibraryStore: the library
    // moved to SQLite in Tier 4.1 and no longer goes through AtomicJsonFile at
    // all, but settings.json, device.json, trusted-peers.json and the rest
    // still do, so the machinery still needs covering - just not via a store
    // that stopped using it.

    [Fact]
    public async Task AppSettingsStore_recovers_from_the_backup_when_the_live_file_is_truncated()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        // Two saves: the first creates the file, the second rotates it into .bak.
        await store.SaveAsync(settings);
        await store.SaveAsync(settings);

        Truncate(AppSettingsStore.StorePath);

        var loaded = store.Load();

        Assert.Equal("Album", loaded.SortColumn);
        Assert.False(loaded.SortAscending);
    }

    // The recovered contents go back to the live path, so a user who quits
    // before the next save still keeps them.
    [Fact]
    public async Task AppSettingsStore_writes_the_recovered_contents_back_to_the_live_file()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Year" };

        await store.SaveAsync(settings);
        await store.SaveAsync(settings);
        Truncate(AppSettingsStore.StorePath);

        store.Load();

        // A second, independent store instance reads the live file with no
        // recovery step of its own.
        var reloaded = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance).Load();
        Assert.Equal("Year", reloaded.SortColumn);
    }

    // With no backup to fall back on the data really is gone, but the bad file
    // is preserved for a bug report rather than being silently overwritten by
    // the next save.
    [Fact]
    public async Task AppSettingsStore_quarantines_an_unreadable_file_when_there_is_no_backup()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        await store.SaveAsync(new AppSettings { SortColumn = "Album" });
        Truncate(AppSettingsStore.StorePath);

        var loaded = store.Load();

        Assert.Null(loaded.SortColumn);
        Assert.True(File.Exists(AtomicJsonFile.CorruptPath(AppSettingsStore.StorePath)));
    }

    [Fact]
    public async Task AtomicJsonFile_leaves_no_temp_file_behind_after_a_successful_write()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        await store.SaveAsync(new AppSettings { SortColumn = "Album" });

        Assert.False(File.Exists(AppSettingsStore.StorePath + ".tmp"));
        Assert.True(File.Exists(AppSettingsStore.StorePath));
    }

    // The SQLite counterpart of the quarantine case above: a database file
    // that isn't one must degrade to "empty library" rather than take the
    // process down. This runs from the DI factory on the startup path, before
    // there is any UI to report an error through, so an escaping exception is
    // a crash on launch with no way back - which is what it did until FlowerDb
    // learned to quarantine.
    [Fact]
    public void LibraryStore_Load_returns_empty_when_the_database_file_is_corrupt()
    {
        File.WriteAllText(FlowerDb.DefaultPath, "this is not a database");

        Assert.Empty(new LibraryStore(NullLogger<LibraryStore>.Instance).Load());
        Assert.True(File.Exists(FlowerDb.CorruptPath(FlowerDb.DefaultPath)));
    }

    // ...and the replacement database is a working one, not a second casualty.
    [Fact]
    public async Task A_quarantined_database_is_replaced_by_a_usable_one()
    {
        File.WriteAllText(FlowerDb.DefaultPath, "this is not a database");
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);

        Repo().ReplaceAll([new Track { Title = "After", Path = "/music/a.mp3" }]);

        Assert.Equal("After", Assert.Single(store.Load()).Title);
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

    // The test above only proves one store instance serializes its own saves.
    // The lock that does that is per-instance while the file is per-path, so
    // two stores over the same path - two DI containers in one process, or any
    // store built through its parameterless constructor rather than resolved -
    // serialized against nothing and collided on the shared fixed .tmp name.
    // That is what made CompositionRootTests fail every second or third run
    // with "the process cannot access settings.json.tmp because it is being
    // used by another process"; the lock now lives in AtomicJsonFile, keyed on
    // the path.
    [Fact]
    public async Task Concurrent_saves_from_separate_store_instances_do_not_collide()
    {
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new AppSettingsStore(NullLogger<AppSettingsStore>.Instance))
            .ToList();
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        // Sync Save and async SaveAsync interleaved deliberately - they are
        // different code paths into the same file, and MainWindow.Closing fires
        // the synchronous one exactly while debounced async saves are in flight.
        await Task.WhenAll(stores.SelectMany(store => new[]
        {
            store.SaveAsync(settings),
            Task.Run(() => store.Save(settings)),
        }));

        Assert.False(File.Exists(AppSettingsStore.StorePath + ".tmp"));
        var reloaded = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance).Load();
        Assert.Equal("Album", reloaded.SortColumn);
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

    // ── Tier 4.1: SQLite-backed library ───────────────────────────────────────

    // Seeds the database directly. Writing the library is Library's own job
    // now (see its ITrackStore) - LibraryStore only reads - so a test that
    // just needs rows on disk goes to the repository rather than through a
    // mutation it is not actually exercising.
    private static TrackRepository Repo() => new(FlowerDb.OpenDefault());

    private static TrackRepository Repo(FlowerDb db) => new(db);

    private static PlaylistRepository PlaylistRepo() => new(FlowerDb.OpenDefault());

    private static PlaylistRepository PlaylistRepo(FlowerDb db) => new(db);

    [Fact]
    public void A_play_count_bump_is_on_disk_before_it_returns()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo().ReplaceAll([track]);

        // Through Library, not through a store method: the write is Library's
        // own now (see its ITrackStore), so a caller cannot bump a count and
        // forget to persist it. Playing one song fires two of these -
        // RecordPlayed when it starts, IncrementPlayCount when it ends - and
        // each has to land on its own: this used to be debounced by 3s, and
        // anything that killed the process inside that window (a crash, or a
        // phone backgrounding the app, which had no flush hook at all)
        // silently dropped the increment.
        var library = new Library([track], NullLogger<Library>.Instance, new TrackRepository(FlowerDb.OpenDefault()));

        library.IncrementPlayCount(track);
        Assert.Equal(1, Assert.Single(store.Load()).PlayCount);

        library.IncrementPlayCount(track);
        Assert.Equal(2, Assert.Single(store.Load()).PlayCount);

        library.RecordPlayed(track);
        Assert.NotNull(Assert.Single(store.Load()).LastPlayedAt);
    }

    [Fact]
    public void A_play_count_bump_writes_one_row_and_leaves_the_rest_of_the_library_alone()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var played = new Track { Title = "A", Path = "/music/a.mp3" };
        var untouched = new Track { Title = "B", Path = "/music/b.mp3", PlayCount = 7 };
        Repo().ReplaceAll([played, untouched]);

        var library = new Library([played, untouched], NullLogger<Library>.Instance, new TrackRepository(FlowerDb.OpenDefault()));
        library.IncrementPlayCount(played);

        var loaded = store.Load();
        Assert.Equal(1, loaded.Single(t => t.Path == "/music/a.mp3").PlayCount);
        Assert.Equal(7, loaded.Single(t => t.Path == "/music/b.mp3").PlayCount);
    }

    [Fact]
    public async Task Every_persisted_Track_field_round_trips_through_SQLite()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track
        {
            Title = "T", Subtitle = "S", Artists = "A", AlbumArtists = "AA", IsCompilation = true,
            Album = "Al", AlbumSort = "AlS", Year = "1999",
            TitleSort = "TS", ArtistsSort = "AS", ComposersSort = "CS",
            RememberPlaybackPosition = true, ResumePosition = TimeSpan.FromSeconds(90.5),
            IgnoreWhenShuffling = true, VolumeAdjustment = -35,
            TrackNumber = 3, TrackCount = 12, DiscNumber = 2, DiscCount = 2,
            Composers = "C", Conductor = "Cond", RemixedBy = "R",
            Genre = "G", BeatsPerMinute = 128, InitialKey = "Am", Grouping = "Gr",
            Publisher = "P", ISRC = "ISRC1",
            Comment = "Cm", Description = "D", Copyright = "Co", Lyrics = "L",
            Duration = TimeSpan.FromSeconds(123.456), Bitrate = 320, SampleRate = 44100,
            Channels = 2, BitsPerSample = 16, Codec = "flac", EncoderProfile = "LAME 3.100, VBR (V0)",
            Path = "/music/a.flac",
            OriginDeviceFingerprint = "fp", OriginTrackId = "otid",
            OriginFileExtension = "flac", OriginAlbumArtHash = "hash",
            PlayCount = 4, ImportedPlayCount = 7,
            LastPlayedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            DateAdded = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        track.RemotePlayCounts["peer-a"] = 11;
        track.RemotePlayCounts["peer-b"] = 22;

        Repo().ReplaceAll([track]);
        var reloaded = Assert.Single(store.Load());

        Assert.Equal(track.Id, reloaded.Id);
        Assert.Equal("T", reloaded.Title);
        Assert.Equal("S", reloaded.Subtitle);
        Assert.Equal("A", reloaded.Artists);
        Assert.Equal("AA", reloaded.AlbumArtists);
        Assert.True(reloaded.IsCompilation);
        Assert.Equal("Al", reloaded.Album);
        Assert.Equal("AlS", reloaded.AlbumSort);
        Assert.Equal("TS", reloaded.TitleSort);
        Assert.Equal("AS", reloaded.ArtistsSort);
        Assert.Equal("CS", reloaded.ComposersSort);
        Assert.True(reloaded.RememberPlaybackPosition);
        Assert.Equal(TimeSpan.FromSeconds(90.5), reloaded.ResumePosition);
        Assert.True(reloaded.IgnoreWhenShuffling);
        Assert.Equal(-35, reloaded.VolumeAdjustment);
        Assert.Equal("LAME 3.100, VBR (V0)", reloaded.EncoderProfile);
        Assert.Equal("1999", reloaded.Year);
        Assert.Equal(3u, reloaded.TrackNumber);
        Assert.Equal(12u, reloaded.TrackCount);
        Assert.Equal(2u, reloaded.DiscNumber);
        Assert.Equal(2u, reloaded.DiscCount);
        Assert.Equal("C", reloaded.Composers);
        Assert.Equal("Cond", reloaded.Conductor);
        Assert.Equal("R", reloaded.RemixedBy);
        Assert.Equal("G", reloaded.Genre);
        Assert.Equal(128u, reloaded.BeatsPerMinute);
        Assert.Equal("Am", reloaded.InitialKey);
        Assert.Equal("Gr", reloaded.Grouping);
        Assert.Equal("P", reloaded.Publisher);
        Assert.Equal("ISRC1", reloaded.ISRC);
        Assert.Equal("Cm", reloaded.Comment);
        Assert.Equal("D", reloaded.Description);
        Assert.Equal("Co", reloaded.Copyright);
        Assert.Equal("L", reloaded.Lyrics);
        Assert.Equal(track.Duration, reloaded.Duration);
        Assert.Equal(320, reloaded.Bitrate);
        Assert.Equal(44100, reloaded.SampleRate);
        Assert.Equal(2, reloaded.Channels);
        Assert.Equal(16, reloaded.BitsPerSample);
        Assert.Equal("flac", reloaded.Codec);
        Assert.Equal("/music/a.flac", reloaded.Path);
        Assert.Equal("fp", reloaded.OriginDeviceFingerprint);
        Assert.Equal("otid", reloaded.OriginTrackId);
        Assert.Equal("flac", reloaded.OriginFileExtension);
        Assert.Equal("hash", reloaded.OriginAlbumArtHash);
        Assert.Equal(4, reloaded.PlayCount);
        Assert.Equal(7, reloaded.ImportedPlayCount);
        Assert.Equal(track.LastPlayedAt, reloaded.LastPlayedAt);
        Assert.Equal(track.DateAdded, reloaded.DateAdded);
        Assert.Equal(11, reloaded.RemotePlayCounts["peer-a"]);
        Assert.Equal(22, reloaded.RemotePlayCounts["peer-b"]);
    }

    [Fact]
    public async Task Saving_the_library_removes_rows_for_tracks_no_longer_present()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var kept = new Track { Title = "Kept", Path = "/music/kept.mp3" };
        var dropped = new Track { Title = "Dropped", Path = "/music/dropped.mp3" };

        Repo().ReplaceAll([kept, dropped]);
        Assert.Equal(2, store.Load().Count);

        // A rescan after the file was deleted on disk.
        Repo().ReplaceAll([kept]);

        Assert.Equal("Kept", Assert.Single(store.Load()).Title);
    }

    [Fact]
    public async Task A_removed_track_takes_its_remote_play_counts_with_it()
    {
        var store = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var track = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        track.RemotePlayCounts["peer"] = 5;
        Repo().ReplaceAll([track]);

        Repo().ReplaceAll([]);
        // Re-adding a track that reuses the id must not inherit the old rows -
        // the child table cascades on delete (see Schema.V1).
        var reused = new Track { Id = track.Id, Title = "Gone", Path = "/music/gone.mp3" };
        Repo().ReplaceAll([reused]);

        Assert.Empty(Assert.Single(store.Load()).RemotePlayCounts);
    }

    [Fact]
    public async Task Playlists_round_trip_with_their_order_and_resolve_against_the_library()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var a = new Track { Title = "A", Path = "/music/a.mp3" };
        var b = new Track { Title = "B", Path = "/music/b.mp3" };
        var c = new Track { Title = "C", Path = "/music/c.mp3" };
        Repo(db).ReplaceAll([a, b, c]);

        var playlist = new Playlist("Mix", [c, a, b]);
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal(playlist.Id, reloaded.Id);
        Assert.Equal("Mix", reloaded.Name);
        Assert.Equal(["C", "A", "B"], reloaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_playlist_entry_whose_track_left_the_library_is_dropped_on_load()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var kept = new Track { Title = "Kept", Path = "/music/kept.mp3" };
        var gone = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        Repo(db).ReplaceAll([kept, gone]);
        PlaylistRepo(db).Save([new Playlist("Mix", [kept, gone])]);

        // The file was deleted and a rescan dropped it from the library, but
        // the playlist row still references it.
        Repo(db).ReplaceAll([kept]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal("Kept", Assert.Single(reloaded.Tracks).Title);
    }

    [Fact]
    public async Task Deleting_a_playlist_removes_its_membership_rows()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);

        var kept = new Playlist("Kept", [track]);
        var deleted = new Playlist("Deleted", [track]);
        PlaylistRepo(db).Save([kept, deleted]);
        PlaylistRepo(db).Save([kept]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal("Kept", reloaded.Name);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playlist_tracks;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task A_smart_playlists_rules_round_trip_alongside_its_materialized_contents()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var jazz = new Track { Title = "Blue in Green", Genre = "Jazz", Path = "/music/blue.mp3" };
        Repo(db).ReplaceAll([jazz]);

        var playlist = new Playlist("Recent Jazz", [jazz])
        {
            Rules = new SmartPlaylistRules(
                MatchMode.All,
                [
                    new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")),
                    new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)),
                ],
                new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed)),
        };
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));

        Assert.True(reloaded.IsSmart);
        Assert.Equal(playlist.Rules!.Conditions, reloaded.Rules!.Conditions);
        Assert.Equal(playlist.Rules.Limit, reloaded.Rules.Limit);
        // The materialized rows are still what a load produces, so the sidebar
        // has contents before anything re-evaluates - see Schema.V6.
        Assert.Equal(["Blue in Green"], reloaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task An_ordinary_playlist_stores_no_rules_at_all()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);
        PlaylistRepo(db).Save([new Playlist("Mix", [track])]);

        Assert.False(Assert.Single(playlistStore.Load(libraryStore.Load())).IsSmart);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playlists WHERE rules IS NOT NULL;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    // "Convert to ordinary playlist": drop the rules, keep whatever was last
    // materialized. Unlike created_at, rules is in the upsert's DO UPDATE for
    // exactly this - a clear that never reached the row would leave the
    // playlist recomputing itself forever.
    [Fact]
    public async Task Clearing_the_rules_converts_a_smart_playlist_back_to_an_ordinary_one()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Genre = "Jazz", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);

        var playlist = new Playlist("Jazz", [track])
        {
            Rules = SmartPlaylistRules.MatchAll(
                new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz"))),
        };
        PlaylistRepo(db).Save([playlist]);

        playlist.Rules = null;
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.False(reloaded.IsSmart);
        Assert.Equal(["A"], reloaded.Tracks.Select(t => t.Title));
    }

    // A rules blob can arrive from a peer or a newer build. Failing the whole
    // playlist load over one is worse than losing the rules: the playlist keeps
    // the tracks it last materialized and behaves as an ordinary one.
    [Fact]
    public async Task A_playlist_whose_rules_blob_cannot_be_read_still_loads()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);
        PlaylistRepo(db).Save([new Playlist("Mix", [track])]);

        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE playlists SET rules = 'not json at all';";
            command.ExecuteNonQuery();
        }

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.False(reloaded.IsSmart);
        Assert.Equal(["A"], reloaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void A_version_five_database_gains_the_rules_column_without_losing_its_playlists()
    {
        var path = Path.Combine(PlatformDataDirectory.Current!, "pre-rules.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText =
                Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4 + Schema.V5
                + "INSERT INTO playlists (id, name, updated_at) VALUES ('abc', 'Kept', 0);"
                + "PRAGMA user_version = 5;";
            setup.ExecuteNonQuery();
        }

        var db = new FlowerDb(path);
        using var migrated = db.Open();

        using (var columns = migrated.CreateCommand())
        {
            columns.CommandText = "SELECT name FROM pragma_table_info('playlists') WHERE name = 'rules';";
            Assert.Equal("rules", columns.ExecuteScalar());
        }

        // The point of a step rather than a fold into V1: playlists are exactly
        // what a delete-and-rescan cannot reproduce.
        using (var kept = migrated.CreateCommand())
        {
            kept.CommandText = "SELECT name FROM playlists;";
            Assert.Equal("Kept", kept.ExecuteScalar());
        }

        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(migrated));
    }

    [Fact]
    public void The_schema_is_created_at_the_latest_version_and_migrating_again_is_a_no_op()
    {
        var path = Path.Combine(PlatformDataDirectory.Current!, "versioned.db");

        using (var connection = new FlowerDb(path).Open())
            Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));

        // Re-opening an existing database must not re-run a script - every one
        // of them starts with CREATE TABLE and would throw.
        using (var connection = new FlowerDb(path).Open())
            Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }

    [Fact]
    public void A_version_four_database_without_encoder_profile_is_upgraded_without_losing_its_version()
    {
        var path = Path.Combine(PlatformDataDirectory.Current!, "pre-encoder-profile.db");

        // Reproduce the short-lived version-4 schema: the database was marked
        // current before EncoderProfile was added to the in-progress V4
        // script. A new migration, rather than editing V4 again, is the only
        // way to repair that already-stamped database.
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4 + "PRAGMA user_version = 4;";
            setup.ExecuteNonQuery();
        }

        var db = new FlowerDb(path);
        using var migrated = db.Open();
        using var columns = migrated.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('tracks') WHERE name = 'encoder_profile';";

        Assert.Equal("encoder_profile", columns.ExecuteScalar());
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(migrated));
    }
}
