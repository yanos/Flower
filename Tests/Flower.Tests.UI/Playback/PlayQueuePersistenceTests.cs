using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Playback;

// The queue outliving the app - a phone kills a paused app whenever it likes,
// and every one of those launches used to come back to nothing queued - and
// outliving the output going away under it, which used to drop the song
// playing and with it the whole Queue screen.
[Collection("PlatformDataDirectory")]
public class PlayQueuePersistenceTests : PinnedDataDirectory
{
    private static Track T(string title) => new() { Title = title, Album = title, Path = $"/music/{title}.mp3" };

    private static PlaylistControlViewModel Control(List<Track> library, out FakeAudioManager audio)
    {
        audio = new FakeAudioManager();
        return new PlaylistControlViewModel(
            audio, new MainPlaylist(library), new Library(library), new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            queueStore: new PlayQueueStore(NullLogger<PlayQueueStore>.Instance));
    }

    private static string[] Titles(PlaylistControlViewModel vm) => vm.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    [Fact]
    public void A_relaunch_comes_back_to_the_queue_parked_where_it_was_left()
    {
        var library = new List<Track> { T("A"), T("B"), T("C"), T("D") };
        var before = Control(library, out var audio);
        var queue = new List<Track> { library[2], library[0], library[1] };
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", queue));
        before.Play(queue[1], 1);
        audio.Time = 42_000;
        audio.RaisePositionChanged();
        before.SavePlaybackState();

        var after = Control(library, out var relaunched);
        after.RestoreQueue();

        Assert.Equal(new[] { "C", "A", "B" }, Titles(after));
        Assert.Equal(1, after.QueueIndex);
        Assert.Equal("A", after.CurrentlyPlayingTrack?.Title);
        Assert.Null(relaunched.LastPlayed);

        // Play starts the parked song from where it was, rather than resuming
        // a decoder this launch never had.
        relaunched.Length = 100_000;
        after.PlayOrPause();
        relaunched.RaisePlaying();

        Assert.Equal("A", relaunched.LastPlayed?.Title);
        Assert.Equal(0, relaunched.ResumeCount);
        Assert.Equal(0.42f, relaunched.Position, 3);
    }

    [Fact]
    public void Songs_gone_from_the_library_are_left_out_and_the_place_follows_the_song()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var before = Control([a, b, c], out _);
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track> { a, b, c }));
        before.Play(c, 2);
        before.SavePlaybackState();

        var after = Control([b, c], out _);
        after.RestoreQueue();

        Assert.Equal(new[] { "B", "C" }, Titles(after));
        Assert.Equal(1, after.QueueIndex);
    }

    [AvaloniaFact]
    public void The_output_stopping_under_a_song_keeps_the_song_and_the_queue()
    {
        var library = new List<Track> { T("A"), T("B"), T("C") };
        var vm = Control(library, out var audio);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", library));
        vm.Play(library[1], 1);

        audio.RaiseStopped();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(library[1], vm.CurrentlyPlayingTrack);
        Assert.Equal(1, vm.QueueIndex);

        vm.PlayOrPause();

        Assert.Same(library[1], audio.LastPlayed);
        Assert.Equal(0, audio.ResumeCount);
    }

    [Fact]
    public void Clearing_the_queue_keeps_only_the_song_playing()
    {
        var library = new List<Track> { T("A"), T("B"), T("C") };
        var vm = Control(library, out var audio);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", library));
        vm.Play(library[1], 1);

        vm.ClearQueue();

        Assert.Equal(new[] { "B" }, Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        Assert.False(vm.CanClearQueue);
        Assert.Null(audio.LastUpcoming);
    }
}
