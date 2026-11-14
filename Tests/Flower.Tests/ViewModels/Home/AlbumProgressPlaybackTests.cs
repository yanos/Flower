using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
// through HomeViewModel's resume. Playback callbacks run inline; dispatcher
// delivery and the shelf-hold timer have separate UI integration tests.
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
        vm.OnUiThread = work => work();
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

    [Fact]
    public void Playing_an_album_and_pausing_it_leaves_it_on_the_shelf_at_the_pause()
    {
        var album = Album("Blue", 5);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        PlayAndHear(vm, audio, album[0], 0);
        PlayAndHear(vm, audio, album[1], 1);
        audio.RaisePaused();

        var entry = Assert.Single(vm.AlbumProgress.Entries);
        Assert.Equal(album[1].Id, entry.TrackId);
        Assert.Equal(TimeSpan.FromMilliseconds(HeardAtMs), entry.Position);
    }

    // Sampling an album: a few seconds of each song, skipping on.
    [Fact]
    public void Skipping_through_an_album_puts_nothing_on_the_shelf()
    {
        var album = Album("Blue", 5);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        for (var i = 0; i < album.Count; i++)
        {
            vm.Play(album[i], i);
            Hear(audio, untilMs: 20_000);
        }

        Assert.Empty(vm.AlbumProgress.Entries);
    }

    // A tag that overstates the length leaves a song short of counting until
    // it ends, where it is counted after all - and still counts as the
    // second song heard, though by then the next one is starting.
    [Fact]
    public void A_song_counted_only_at_its_end_is_still_heard()
    {
        var album = Album("Blue", 4);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        PlayAndHear(vm, audio, album[0], 0);
        vm.Play(album[1], 1);
        Hear(audio, untilMs: 180_000);
        Assert.Empty(vm.AlbumProgress.Entries);

        audio.RaiseEndReached();

        Assert.Equal(album[2].Id, Assert.Single(vm.AlbumProgress.Entries).TrackId);
    }

    [Fact]
    public void The_last_song_ending_takes_the_album_off_the_shelf()
    {
        var album = Album("Blue", 3);
        var vm = Playback(album, out var audio, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));

        PlayAndHear(vm, audio, album[1], 1);
        PlayAndHear(vm, audio, album[2], 2);
        Assert.Single(vm.AlbumProgress.Entries);

        audio.RaiseEndReached();

        Assert.Empty(vm.AlbumProgress.Entries);
    }

    [Fact]
    public void Continue_playing_queues_the_album_and_starts_at_the_saved_place()
    {
        var blue = Album("Blue", 4);
        var red = Album("Red", 2);
        var vm = Playback(blue.Concat(red).ToList(), out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);

        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", blue));
        PlayAndHear(vm, audio, blue[0], 0);
        PlayAndHear(vm, audio, blue[1], 1);
        audio.Time = 60_000;
        audio.RaisePositionChanged();
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", red));
        PlayAndHear(vm, audio, red[0], 0);

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
    [Fact]
    public void Continue_playing_the_album_already_loaded_just_resumes_it()
    {
        var album = Album("Blue", 4);
        var vm = Playback(album, out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));
        PlayAndHear(vm, audio, album[0], 0);
        PlayAndHear(vm, audio, album[1], 1);
        audio.IsPlaying = false;

        home.Rebuild();
        home.ResumeCommand.Execute(Assert.Single(home.ContinuePlaying));

        Assert.Equal(1, audio.ResumeCount);
        Assert.Equal(album[1].Id, audio.LastPlayed?.Id);
    }

    // The album playing shows a pause on its cover, and pressing it pauses.
    [Fact]
    public void Continue_playing_the_album_that_is_playing_pauses_it()
    {
        var album = Album("Blue", 4);
        var vm = Playback(album, out var audio, out var library);
        using var home = new HomeViewModel(library, vm.AlbumProgress, vm);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", album));
        PlayAndHear(vm, audio, album[0], 0);
        PlayAndHear(vm, audio, album[1], 1);
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
}
