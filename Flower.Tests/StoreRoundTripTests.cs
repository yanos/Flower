using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
}
