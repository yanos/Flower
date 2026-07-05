using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flower.Controls;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Tests;

// LibraryStore/PlaylistStore resolve their file path from the OS user-data
// folder (via HOME on macOS/Linux). These tests redirect HOME to an isolated
// temp directory for the lifetime of each test so they never read or write
// the real developer's library.json/playlists.json. All such tests live in
// this one class because xUnit runs test methods within a class sequentially
// — spreading this across classes would risk two tests mutating the
// process-wide HOME variable at the same time under parallel execution.
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
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
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
    public async Task ColumnVisibilityStore_round_trips_column_states()
    {
        var states = new List<ColumnState>
        {
            new() { Id = "Title", IsVisible = true, Width = 197.5, Order = 0 },
            new() { Id = "Artist", IsVisible = false, Width = 150, Order = 1 },
        };

        await new ColumnVisibilityStore().SaveColumnStatesAsync(states);
        var loaded = new ColumnVisibilityStore().LoadColumnStates();

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
        var first = new ColumnManager(new ColumnVisibilityStore());
        var title = first.Columns.Single(c => c.Id == "Title");
        title.Width = 321;
        first.Flush();

        // A brand-new ColumnManager reading the same (temp-HOME) store simulates
        // the next app launch.
        var second = new ColumnManager(new ColumnVisibilityStore());
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
}
