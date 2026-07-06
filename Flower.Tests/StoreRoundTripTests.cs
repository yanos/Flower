using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        await new LibraryStore().SaveAsync(tracks);
        var loaded = await new LibraryStore().LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(125), loaded[0].Duration);
    }

    [Fact]
    public async Task LibraryStore_Load_returns_empty_list_when_no_file_exists()
    {
        var loaded = await new LibraryStore().LoadAsync();
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

        new LibraryStore().Save(tracks);
        var loaded = new LibraryStore().Load();

        Assert.Single(loaded);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(1, loaded[0].PlayCount);
    }

    // Minimal stand-in for VlcAudioManager, just for raising EndReached below -
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
        public void Resume() { }
        public void Pause() { }
        public void Stop() { }
        public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);
#pragma warning disable CS0067
        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
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
        var vm = new PlaylistControlViewModel(audio, emptyPlaylist, library, new AppSettings());

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
        var first = new ColumnManager(new AppSettings());
        var title = first.Columns.Single(c => c.Id == "Title");
        title.Width = 321;
        first.Flush();

        // A brand-new ColumnManager reading the just-persisted settings.json
        // simulates the next app launch.
        var second = new ColumnManager(new AppSettingsStore().Load());
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
    public void ITunesPlayCountImporter_does_not_match_a_track_with_a_different_duration()
    {
        var xmlPath = Path.Combine(_tempHome, "sample-library.xml");
        File.WriteAllText(xmlPath, SampleLibraryXml(17));

        // Same title/artist/album as the XML entry, but a different length -
        // a genuinely different recording (e.g. a remix or live version),
        // which SyncKey correctly treats as not the same track.
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
}
