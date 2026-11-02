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

namespace Flower.Tests.Playback;

// Endless Play (docs/VIBE-PLAYLIST-PLAN.md, "Endless Play"): a queue whose
// last song starts asks the server for songs like the last few, and adds some
// to the end in time for the next song to follow it - unless the switch is
// off, repeat or shuffle means it never ends, or something else was lined up
// meanwhile.
[Collection("PlatformDataDirectory")]
public class EndlessPlayPlaybackTests : PinnedDataDirectory
{
    // The server: answers with the songs it was given, and remembers what it
    // was asked. Held until Answer when told to, so a test can act in between.
    private sealed class Server(IReadOnlyList<Track> answer) : IVibePlaylists
    {
        private TaskCompletionSource? _held;

        public List<(List<Track> Seeds, List<Track> Exclude)> Asked { get; } = [];

        public void Hold() => _held = new TaskCompletionSource();

        public void Answer() => _held!.SetResult();

        public Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default) =>
            Task.FromResult(new VibePreview([]));

        public Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Track>>([]);

        public async Task<IReadOnlyList<Track>> EndlessAsync(IReadOnlyList<Track> seeds, IEnumerable<Track> exclude, CancellationToken ct = default)
        {
            Asked.Add((seeds.ToList(), exclude.ToList()));
            if (_held != null)
                await _held.Task;
            return answer;
        }
    }

    private static List<Track> Songs(int count, int from = 0) =>
        Enumerable.Range(from, count).Select(n => new Track { Title = $"Song {n}", Artists = $"Artist {n}", Album = $"Album {n}", Path = $"/m/{n}.mp3" }).ToList();

    private readonly List<Track> _queue = Songs(8);
    private readonly List<Track> _found = Songs(30, from: 100);

    private PlaylistControlViewModel Make(Server server, out FakeAudioManager audio, AppSettings? settings = null, bool on = true)
    {
        audio = new FakeAudioManager();
        var library = _queue.Concat(_found).ToList();
        return new PlaylistControlViewModel(
            audio, new MainPlaylist(library), new Library(library), settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance), NullLogger<PlaylistControlViewModel>.Instance)
        {
            OnUiThread = work => work(),
            OffPlaybackThread = work => work(),
            VibePlaylists = server,
            EndlessPlays = () => on,
        };
    }

    private void PlayQueue(PlaylistControlViewModel vm, int from)
    {
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", _queue));
        vm.Play(_queue[from], from);
    }

    [Fact]
    public async Task The_last_song_starting_asks_for_songs_like_the_last_five_and_adds_ten()
    {
        var server = new Server(_found);
        var vm = Make(server, out var audio);

        PlayQueue(vm, 7);
        await vm.EndlessPass;

        var (seeds, exclude) = Assert.Single(server.Asked);
        Assert.Equal(_queue.Skip(3), seeds);
        Assert.Equal(_queue, exclude);
        Assert.Equal(_queue.Count + PlaylistControlViewModel.EndlessBatch, vm.CurrentPlaylist.Tracks.Count);
        Assert.All(vm.CurrentPlaylist.Tracks.Skip(_queue.Count), t => Assert.Contains(t, _found));
        // Armed behind the song playing, so it follows without a gap.
        Assert.Same(vm.CurrentPlaylist.Tracks[_queue.Count], audio.LastUpcoming);
    }

    [Fact]
    public void A_song_before_the_last_asks_nothing()
    {
        var server = new Server(_found);
        var vm = Make(server, out _);

        PlayQueue(vm, 6);

        Assert.Empty(server.Asked);
    }

    [Fact]
    public void Off_on_this_device_repeat_or_shuffle_asks_nothing()
    {
        var off = new Server(_found);
        PlayQueue(Make(off, out _, on: false), 7);

        var repeat = new Server(_found);
        PlayQueue(Make(repeat, out _, new AppSettings { IsRepeatEnabled = true }), 7);

        var shuffle = new Server(_found);
        PlayQueue(Make(shuffle, out _, new AppSettings { IsShuffleEnabled = true }), 7);

        Assert.Empty(off.Asked);
        Assert.Empty(repeat.Asked);
        Assert.Empty(shuffle.Asked);
    }

    [Fact]
    public async Task Songs_lined_up_while_it_asks_win_over_the_answer()
    {
        var server = new Server(_found);
        server.Hold();
        var vm = Make(server, out _);
        PlayQueue(vm, 7);

        var mine = Songs(2, from: 500);
        vm.AddToQueue(mine);
        server.Answer();
        await vm.EndlessPass;

        Assert.Equal(_queue.Concat(mine), vm.CurrentPlaylist.Tracks);
    }

    [Fact]
    public async Task A_queue_that_ran_out_while_it_asked_plays_on_when_the_answer_comes()
    {
        var server = new Server(_found);
        server.Hold();
        var vm = Make(server, out var audio);
        PlayQueue(vm, 7);

        audio.RaiseEndReached();
        audio.RaisePlayedOut();
        server.Answer();
        await vm.EndlessPass;

        Assert.Same(vm.CurrentPlaylist.Tracks[_queue.Count], vm.CurrentlyPlayingTrack);
        Assert.Contains(vm.CurrentlyPlayingTrack!, _found);
    }

    [Fact]
    public async Task Leaves_out_songs_already_in_the_queue_and_asks_again_from_the_new_end()
    {
        // The server answers with half the queue back and three new songs.
        var server = new Server(_queue.Take(4).Concat(_found.Take(3)).ToList());
        var vm = Make(server, out var audio);
        PlayQueue(vm, 7);
        await vm.EndlessPass;

        Assert.Equal(_queue.Concat(_found.Take(3)).OrderBy(t => t.Title).ToList(), vm.CurrentPlaylist.Tracks.OrderBy(t => t.Title).ToList());

        for (var i = 0; i < 3; i++)
            audio.RaiseEndReached();
        await vm.EndlessPass;

        Assert.Equal(2, server.Asked.Count);
        Assert.Equal(vm.CurrentPlaylist.Tracks.Skip(_queue.Count - 2).Take(5), server.Asked[1].Seeds);
    }
}
