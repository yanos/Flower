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

// A Vibe Playlist playing as its endless queue (docs/VIBE-PLAYLIST-PLAN.md,
// "The endless queue"): always ten songs ahead, shuffle and repeat not
// applying, ended by playing anything else, and early skips written down.
[Collection("PlatformDataDirectory")]
public class VibeQueuePlaybackTests : PinnedDataDirectory
{
    private sealed class Skips : IVibeSkipStore
    {
        public List<(Guid Playlist, Guid Track)> Recorded { get; } = [];

        public IReadOnlySet<Guid> Load(Guid playlistId) => Recorded.Where(r => r.Playlist == playlistId).Select(r => r.Track).ToHashSet();

        public void Record(Guid playlistId, Guid trackId) => Recorded.Add((playlistId, trackId));
    }

    private sealed class Extensions(IReadOnlyList<Track> more) : IVibePlaylists
    {
        public int Asked { get; private set; }

        public Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default) =>
            Task.FromResult(new VibePreview([]));

        public Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult(more);
        }
    }

    private static List<Track> Songs(int count, int from = 0) =>
        Enumerable.Range(from, count).Select(n => new Track { Title = $"Song {n}", Artists = $"Artist {n}", Album = $"Album {n}", Path = $"/m/{n}.mp3" }).ToList();

    private readonly Skips _skips = new();

    private PlaylistControlViewModel Make(List<Track> library, out FakeAudioManager audio, AppSettings? settings = null)
    {
        audio = new FakeAudioManager();
        var vm = new PlaylistControlViewModel(
            audio, new MainPlaylist(library), new Library(library), settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance), NullLogger<PlaylistControlViewModel>.Instance)
        {
            OnUiThread = work => work(),
            OffPlaybackThread = work => work(),
            VibeSkips = _skips,
        };
        return vm;
    }

    private static Playlist Vibe(List<Track> pool) => new("Rainy", pool) { VibePrompt = "music for a rainy day" };

    private static int Ahead(PlaylistControlViewModel vm) => vm.CurrentPlaylist.Tracks.Count - 1 - vm.QueueIndex;

    [Fact]
    public void Playing_a_vibe_playlist_queues_ten_songs_after_the_one_picked()
    {
        var pool = Songs(30);
        var vm = Make(pool, out var audio);

        vm.PlayVibe(Vibe(pool), pool[4]);

        Assert.Same(pool[4], vm.CurrentlyPlayingTrack);
        Assert.Equal(VibeQueueSource.Ahead, Ahead(vm));
        Assert.Equal(11, vm.CurrentPlaylist.Tracks.Distinct().Count());
        Assert.NotNull(vm.VibeSource);
    }

    [Fact]
    public void The_queue_tops_itself_up_as_it_moves_on()
    {
        var pool = Songs(30);
        var vm = Make(pool, out var audio);
        vm.PlayVibe(Vibe(pool));

        for (var i = 0; i < 5; i++)
            audio.RaiseEndReached();

        Assert.Equal(VibeQueueSource.Ahead, Ahead(vm));
        Assert.Equal(16, vm.CurrentPlaylist.Tracks.Count);
    }

    [Fact]
    public void Shuffle_and_repeat_do_not_apply_to_it()
    {
        var pool = Songs(30);
        var vm = Make(pool, out var audio, new AppSettings { IsShuffleEnabled = true, IsRepeatEnabled = true });
        vm.PlayVibe(Vibe(pool));
        var queued = vm.CurrentPlaylist.Tracks.ToList();

        audio.RaiseEndReached();

        Assert.Same(queued[1], vm.CurrentlyPlayingTrack);
        Assert.False(vm.Shuffles);
    }

    [Fact]
    public void Playing_anything_else_ends_it()
    {
        var pool = Songs(30);
        var vm = Make(pool, out _);
        vm.PlayVibe(Vibe(pool));

        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", Songs(3, from: 100)));

        Assert.Null(vm.VibeSource);
    }

    [Fact]
    public void A_song_skipped_in_its_first_seconds_is_written_down_against_the_playlist()
    {
        var pool = Songs(30);
        var vm = Make(pool, out var audio);
        var playlist = Vibe(pool);
        vm.PlayVibe(playlist, pool[0]);

        audio.Time = 5_000;
        vm.Next();
        var second = vm.CurrentlyPlayingTrack!;
        audio.Time = 95_000;
        vm.Next();

        Assert.Equal([(playlist.Id, pool[0].Id)], _skips.Recorded);
        Assert.NotEqual(pool[0], second);
    }

    [Fact]
    public void Once_most_of_the_pool_has_played_it_asks_for_more_and_draws_from_them()
    {
        var pool = Songs(12);
        var more = Songs(20, from: 100);
        var extensions = new Extensions(more);
        var vm = Make(pool.Concat(more).ToList(), out var audio);
        vm.VibePlaylists = extensions;

        vm.PlayVibe(Vibe(pool));
        for (var i = 0; i < 5; i++)
            audio.RaiseEndReached();

        Assert.Equal(1, extensions.Asked);
        Assert.Equal(32, vm.VibeSource!.Pool.Count);
        Assert.Contains(vm.CurrentPlaylist.Tracks, t => more.Contains(t));
    }
}
