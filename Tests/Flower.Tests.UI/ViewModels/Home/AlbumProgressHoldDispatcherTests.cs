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
public class AlbumProgressHoldDispatcherTests : PinnedDataDirectory
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

    // The listen meter's clock, which only moves when the test plays.
    private TimeSpan _now = TimeSpan.FromHours(1);

    private PlaylistControlViewModel Playback(List<Track> tracks, out FakeAudioManager audio, out Library library)
    {
        audio = new FakeAudioManager();
        library = new Library(tracks);
        var vm = new PlaylistControlViewModel(
            audio,
            new MainPlaylist(tracks),
            library,
            new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            listenMeter: new ListenMeter(() => _now));
        vm.OffPlaybackThread = work => work();
        return Own(vm);
    }

    // How far into one of these four-minute songs it has been heard enough to
    // count as a play.
    private static readonly long HeardAtMs =
        (long)Math.Ceiling(TimeSpan.FromMinutes(4).TotalMilliseconds * ListenMeter.CountedFraction);

    // Plays the song just started from its top to untilMs, reported the way
    // the position timer reports it - clock and position moving together.
    // By default, until it has been heard.
    private void Hear(FakeAudioManager audio, long? untilMs = null)
    {
        var until = untilMs ?? HeardAtMs;
        audio.Time = 0;
        while (audio.Time < until)
        {
            var step = Math.Min(250, until - audio.Time);
            _now += TimeSpan.FromMilliseconds(step);
            audio.Time += step;
            audio.RaisePositionChanged();
        }
    }

    private void PlayAndHear(PlaylistControlViewModel vm, FakeAudioManager audio, Track track, int index)
    {
        vm.Play(track, index);
        Hear(audio);
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
        var vm = Playback(blue.Concat(red).ToList(), out var audio, out var library);
        var timers = new ManualTimers();
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm, clock: timers);

        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", blue));
        PlayAndHear(vm, audio, blue[0], 0);
        PlayAndHear(vm, audio, blue[1], 1);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", red));
        PlayAndHear(vm, audio, red[0], 0);
        PlayAndHear(vm, audio, red[1], 1);

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
