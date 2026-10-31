using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// Continue Playing driven the way the app drives it: through
// PlaylistControlViewModel's own starts, pauses and ends, and picked back up
// through HomeViewModel's resume.
[Collection("PlatformDataDirectory")]
public class AlbumProgressPlaybackTests : PinnedDataDirectory
{
    private static List<Track> Album(string name, int songs) =>
        Enumerable.Range(1, songs).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = "Artist",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
        }).ToList();

    private static PlaylistControlViewModel Playback(List<Track> tracks, out FakeAudioManager audio, out Library library)
    {
        audio = new FakeAudioManager();
        library = new Library(tracks);
        var vm = new PlaylistControlViewModel(
            audio,
            new MainPlaylist(tracks),
            library,
            new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        vm.OffPlaybackThread = work => work();
        return vm;
    }

    [AvaloniaFact]
    public void Playing_an_album_and_pausing_it_leaves_it_on_the_shelf_at_the_pause()
    {
        var album = Album("Blue", 5);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        vm.Play(album[0], 0);
        vm.Play(album[1], 1);
        audio.Time = 125_000;
        audio.RaisePositionChanged();
        audio.RaisePaused();

        var entry = Assert.Single(vm.AlbumProgress.Entries);
        Assert.Equal(album[1].Id, entry.TrackId);
        Assert.Equal(TimeSpan.FromSeconds(125), entry.Position);
    }

    [AvaloniaFact]
    public void The_last_song_ending_takes_the_album_off_the_shelf()
    {
        var album = Album("Blue", 3);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        vm.Play(album[1], 1);
        vm.Play(album[2], 2);
        Assert.Single(vm.AlbumProgress.Entries);

        audio.RaiseEndReached();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.AlbumProgress.Entries);
    }

    [AvaloniaFact]
    public void Continue_playing_queues_the_album_and_starts_at_the_saved_place()
    {
        var blue = Album("Blue", 4);
        var red = Album("Red", 2);
        var vm = Playback(blue.Concat(red).ToList(), out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);

        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", blue));
        vm.Play(blue[0], 0);
        vm.Play(blue[1], 1);
        audio.Time = 60_000;
        audio.RaisePositionChanged();
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", red));
        vm.Play(red[0], 0);

        home.Rebuild();
        var item = Assert.Single(home.ContinuePlaying);

        home.ResumeCommand.Execute(item);
        audio.Length = 240_000;
        audio.RaisePlaying();

        Assert.Equal(blue[1].Id, audio.LastPlayed?.Id);
        Assert.Equal(blue.Select(t => t.Id), vm.CurrentPlaylist.Tracks.Select(t => t.Id));
        Assert.Equal(1, vm.QueueIndex);
        Assert.Equal(0.25f, audio.Position, 3);
    }

    // Tapping the album that is already in the deck carries on rather than
    // rewinding it to the last place written down.
    [AvaloniaFact]
    public void Continue_playing_the_album_already_loaded_just_resumes_it()
    {
        var album = Album("Blue", 4);
        var vm = Playback(album, out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));
        vm.Play(album[0], 0);
        vm.Play(album[1], 1);
        audio.IsPlaying = false;

        home.Rebuild();
        home.ResumeCommand.Execute(Assert.Single(home.ContinuePlaying));

        Assert.Equal(1, audio.ResumeCount);
        Assert.Equal(album[1].Id, audio.LastPlayed?.Id);
    }

    // The album playing shows a pause on its cover, and pressing it pauses.
    [AvaloniaFact]
    public void Continue_playing_the_album_that_is_playing_pauses_it()
    {
        var album = Album("Blue", 4);
        var vm = Playback(album, out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));
        vm.Play(album[0], 0);
        vm.Play(album[1], 1);
        audio.IsPlaying = true;
        audio.RaisePlaying();

        home.Rebuild();
        var item = Assert.Single(home.ContinuePlaying);
        Assert.True(item.IsPlaying);

        home.ResumeCommand.Execute(item);
        Assert.Equal(1, audio.PauseCount);

        audio.IsPlaying = false;
        audio.RaisePaused();
        Assert.False(item.IsPlaying);
    }
    // A clock whose one-shot timers go off when the test says so.
    private sealed class ManualTimers : TimeProvider
    {
        private readonly List<TimerCallback> _pending = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _pending.Add(callback);
            return new Timer(() => _pending.Remove(callback));
        }

        public void FireAll()
        {
            foreach (var callback in _pending.ToList())
                callback(null);
            _pending.Clear();
        }

        private sealed class Timer(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => dispose();
            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // Picked up from the shelf, an album stays under the pointer that
    // clicked it for a while, then goes to the front where playing put it.
    [AvaloniaFact]
    public void A_resumed_album_keeps_its_place_on_the_shelf_until_the_hold_runs_out()
    {
        var blue = Album("Blue", 4);
        var red = Album("Red", 4);
        var vm = Playback(blue.Concat(red).ToList(), out _, out var library);
        var timers = new ManualTimers();
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm, clock: timers);

        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", blue));
        vm.Play(blue[0], 0);
        vm.Play(blue[1], 1);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", red));
        vm.Play(red[0], 0);
        vm.Play(red[1], 1);

        home.Rebuild();
        Assert.Equal(new[] { "Red", "Blue" }, home.ContinuePlaying.Select(c => c.Tile.Name));

        home.ResumeCommand.Execute(home.ContinuePlaying[1]);
        // Playing it put it at the front of the tracker straight away.
        Assert.Equal(AlbumProgressTracker.AlbumIdOf(blue[0]), vm.AlbumProgress.Entries[0].AlbumId);

        home.Rebuild();
        Assert.Equal(new[] { "Red", "Blue" }, home.ContinuePlaying.Select(c => c.Tile.Name));

        timers.FireAll();
        Dispatcher.UIThread.RunJobs();
        home.Rebuild();
        Assert.Equal(new[] { "Blue", "Red" }, home.ContinuePlaying.Select(c => c.Tile.Name));
    }
}
