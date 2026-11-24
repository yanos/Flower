using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Playback;

// What the queue does about songs removed on purpose in the states the UI
// suite's QueueFollowsItsSourceTests does not reach: parked, across a
// relaunch, and arriving from a paired server.
[Collection("PlatformDataDirectory")]
public class QueueAcrossRemovalsTests : PinnedDataDirectory
{
    private static Track T(string title) => new() { Title = title, Album = title, Path = $"/music/{title}.mp3" };

    private static Track Placeholder(string title) => new()
    {
        Title = title,
        Album = title,
        OriginDeviceFingerprint = "server",
        OriginTrackId = title,
    };

    // A placeholder cannot start here with no stream resolver, so the song
    // playing is a downloaded copy of the server's.
    private static Track Downloaded(string title)
    {
        var track = T(title);
        track.OriginDeviceFingerprint = "server";
        track.OriginTrackId = title;
        track.IsLocallyDownloaded = true;
        return track;
    }

    private PlaylistControlViewModel Control(Library library, out FakeAudioManager audio)
    {
        audio = new FakeAudioManager();
        var vm = Own(new PlaylistControlViewModel(
            audio, new MainPlaylist(library.Tracks.ToList()), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            queueStore: new PlayQueueStore(NullLogger<PlayQueueStore>.Instance)));
        vm.OnUiThread = work => work();
        vm.OffPlaybackThread = work => work();
        return vm;
    }

    private static string[] Titles(PlaylistControlViewModel vm) => vm.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    [Fact]
    public void A_parked_song_removed_moves_the_place_to_the_song_after_it_without_playing()
    {
        var songs = new List<Track> { T("A"), T("B"), T("C") };
        var library = new Library(songs);
        var before = Control(library, out _);
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs));
        before.Play(songs[1], 1);
        before.SavePlaybackState();
        before.Dispose();

        var after = Control(library, out var audio);
        after.RestoreQueue();
        library.RemoveTracks([songs[1]], []);

        Assert.Equal(new[] { "A", "C" }, Titles(after));
        Assert.Equal("C", after.CurrentlyPlayingTrack?.Title);
        Assert.Null(audio.LastPlayed);
    }

    [Fact]
    public void Quitting_during_a_song_that_left_comes_back_to_the_song_after_it()
    {
        var songs = new List<Track> { T("A"), T("B"), T("C") };
        var library = new Library(songs);
        var before = Control(library, out _);
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs));
        before.Play(songs[1], 1);
        library.RemoveTracks([songs[1]], []);
        before.SavePlaybackState();
        before.Dispose();

        var after = Control(library, out _);
        after.RestoreQueue();

        Assert.Equal(new[] { "A", "C" }, Titles(after));
        Assert.Equal("C", after.CurrentlyPlayingTrack?.Title);
    }

    [Fact]
    public void A_queue_still_follows_its_playlist_after_a_relaunch()
    {
        var songs = new List<Track> { T("A"), T("B"), T("C"), T("D") };
        var library = new Library(songs);
        var playlist = new Playlist("Mix", new List<Track>(songs));
        library.AddPlaylist(playlist);
        var before = Control(library, out _);
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs), playlist, followsOrder: true);
        before.Play(songs[0], 0);
        before.SavePlaybackState();
        before.Dispose();

        var after = Control(library, out _);
        after.RestoreQueue();
        playlist.MoveEntries([3], 1);

        Assert.Equal(new[] { "A", "D", "B", "C" }, Titles(after));
    }

    [Fact]
    public void A_parked_song_removed_from_its_playlist_moves_the_place_on_without_playing()
    {
        var songs = new List<Track> { T("A"), T("B"), T("C") };
        var library = new Library(songs);
        var playlist = new Playlist("Mix", new List<Track>(songs));
        library.AddPlaylist(playlist);
        var before = Control(library, out _);
        before.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs), playlist, followsOrder: true);
        before.Play(songs[1], 1);
        before.SavePlaybackState();
        before.Dispose();

        var after = Control(library, out var audio);
        after.RestoreQueue();
        playlist.RemoveEntriesAt([1]);

        Assert.Equal(new[] { "A", "C" }, Titles(after));
        Assert.Equal("C", after.CurrentlyPlayingTrack?.Title);
        Assert.Null(audio.LastPlayed);
    }

    [Fact]
    public void A_song_the_server_removed_leaves_the_queue()
    {
        var songs = new List<Track> { Downloaded("A"), Placeholder("B"), Placeholder("C") };
        var library = new Library(songs);
        var vm = Control(library, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs));
        vm.Play(songs[0], 0);
        Assert.NotNull(vm.CurrentlyPlayingTrack);

        library.MergeSyncedTracks("server", [Placeholder("A"), Placeholder("C")], removedAtSource: new HashSet<string> { "B" });

        Assert.Equal(new[] { "A", "C" }, Titles(vm));
    }

    // A server whose music folder failed to mount lists nothing, which says
    // nothing about whether the songs are gone.
    [Fact]
    public void A_song_the_server_merely_stopped_listing_stays_queued()
    {
        var songs = new List<Track> { Downloaded("A"), Placeholder("B") };
        var library = new Library(songs);
        var vm = Control(library, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs));
        vm.Play(songs[0], 0);
        Assert.NotNull(vm.CurrentlyPlayingTrack);

        library.MergeSyncedTracks("server", [Placeholder("A")]);

        Assert.Equal(new[] { "A", "B" }, Titles(vm));
    }

    [Fact]
    public void A_vibe_queue_never_draws_a_removed_song_back_in()
    {
        var songs = Enumerable.Range(0, 30)
            .Select(n => new Track { Title = $"Song {n}", Artists = $"Artist {n}", Album = $"Album {n}", Path = $"/m/{n}.mp3" })
            .ToList();
        var library = new Library(songs);
        var vm = Control(library, out var audio);
        vm.PlayVibe(new Playlist("Rainy", new List<Track>(songs)) { VibePrompt = "music for a rainy day" }, songs[0]);
        var notQueuedYet = songs.First(s => !vm.CurrentPlaylist.Tracks.Contains(s));

        library.RemoveTracks([notQueuedYet], []);

        for (var i = 0; i < 60; i++)
        {
            vm.Next();
            Assert.NotEqual(notQueuedYet.Id, audio.LastPlayed?.Id);
            Assert.DoesNotContain(vm.CurrentPlaylist.Tracks, t => t.Id == notQueuedYet.Id);
        }
    }

    [Fact]
    public void Unpairing_takes_the_servers_songs_out_of_the_queue()
    {
        var own = T("Mine");
        var streamed = Placeholder("Theirs");
        var songs = new List<Track> { own, streamed };
        var library = new Library(songs);
        var vm = Control(library, out _);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", songs));
        vm.Play(own, 0);

        library.RemoveTracksFromOrigin("server");

        Assert.Equal(new[] { "Mine" }, Titles(vm));
    }
}
