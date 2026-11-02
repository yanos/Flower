using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.SemanticAudio;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// What a Vibe Playlist holds on the host that ranks (docs/VIBE-PLAYLIST-PLAN.md,
// "Search"): the head of its sentence's ranking, cut at 200 songs or a margin
// below the best, installed without moving UpdatedAt.
public class VibePlaylistPoolsTests
{
    private readonly List<Track> _songs = Enumerable.Range(0, 300).Select(n => new Track { Title = $"Song {n}", Path = $"/m/{n}.mp3" }).ToList();
    private readonly Library _library;
    private readonly List<SemanticAudioQuery> _asked = [];
    private SemanticAudioStatus _status = new(SemanticAudioState.UpToDate, null, SemanticAudioProgress.None, 300, null);
    private bool _ready = true;

    // Song n scores 1 - n/10000: a gentle slope, so only the size cap bites.
    private Func<int, float> _score = n => 1f - n / 10000f;

    public VibePlaylistPoolsTests() => _library = new Library(_songs);

    private VibePlaylistPools Pools() => new(
        _library,
        (query, _) =>
        {
            if (!_ready)
                throw new SemanticAudioNotReadyException(SemanticAudioState.Downloading);
            _asked.Add(query);
            return _songs.Select((t, n) => new SemanticAudioMatch(t.Id, _score(n))).ToList();
        },
        () => _status,
        () => 6,
        NullLogger.Instance);

    private Playlist Vibe(string prompt = "music for a rainy day")
    {
        var playlist = new Playlist("Rainy", []) { VibePrompt = prompt };
        _library.AddPlaylist(playlist);
        return playlist;
    }

    [Fact]
    public void A_pool_stops_at_its_size_limit()
    {
        var ranked = _songs.Select((t, n) => new SemanticAudioMatch(t.Id, 1f - n / 10000f)).ToList();

        Assert.Equal(_songs.Take(VibePlaylistPools.MaxPoolSize).Select(t => t.Id), VibePlaylistPools.Cut(ranked));
    }

    [Fact]
    public void A_pool_stops_where_songs_fit_much_worse_than_the_best()
    {
        var ranked = _songs.Select((t, n) => new SemanticAudioMatch(t.Id, 0.5f - n / 100f)).ToList();

        // 0.5 down to 0.35 is 16 songs at 0.01 apart.
        Assert.Equal(16, VibePlaylistPools.Cut(ranked, margin: 0.15f + 1e-4f).Count);
    }

    [Fact]
    public void A_rebuild_installs_the_pool_without_making_it_an_edit()
    {
        var playlist = Vibe();
        var edited = playlist.UpdatedAt;

        var changed = Pools().Refresh();

        Assert.Equal([playlist], changed);
        Assert.Equal(_songs.Take(VibePlaylistPools.MaxPoolSize), playlist.Tracks);
        Assert.Equal(edited, playlist.UpdatedAt);
    }

    [Fact]
    public void Asks_with_the_sentence_and_the_No_vocals_switch()
    {
        var playlist = Vibe("sad piano");
        playlist.VibeInstrumental = true;

        Pools().Refresh();

        Assert.Equal([new SemanticAudioQuery("sad piano", true)], _asked);
    }

    [Fact]
    public void A_pool_already_ranked_from_the_same_sentence_is_left_alone()
    {
        var playlist = Vibe();
        var pools = Pools();
        pools.Refresh();

        pools.Refresh();
        Assert.Single(_asked);

        playlist.VibePrompt = "music to go jogging";
        pools.Refresh();
        Assert.Equal(2, _asked.Count);
    }

    [Fact]
    public void Before_anything_is_analysed_a_pool_keeps_what_it_had_and_is_tried_again_later()
    {
        var playlist = Vibe();
        playlist.Materialize([_songs[7]]);
        var pools = Pools();
        _ready = false;

        Assert.Empty(pools.Refresh());
        Assert.Equal([_songs[7]], playlist.Tracks);

        _ready = true;
        Assert.Equal([playlist], pools.Refresh());
    }

    [Fact]
    public void An_extension_is_the_next_songs_down_that_the_pool_and_the_device_do_not_have()
    {
        _score = n => 1f - n / 100f;
        var playlist = Vibe();
        var pools = Pools();
        pools.Refresh();
        var pool = playlist.Tracks.Count;

        var more = pools.Extend(playlist, new HashSet<Guid> { _songs[pool].Id }, count: 3);

        Assert.Equal(_songs.Skip(pool + 1).Take(3), more);
    }

    [Fact]
    public void A_preview_is_the_head_of_the_pool_the_sentence_would_make()
    {
        var preview = Pools().Preview("music for a rainy day", instrumental: false, count: 5);

        Assert.Equal(_songs.Take(5), preview);
        Assert.Empty(_library.Playlists);
    }

    [Fact]
    public void While_the_analysis_runs_pools_are_rebuilt_at_most_once_an_hour()
    {
        var playlist = Vibe();
        var pools = Pools();
        _status = _status with { State = SemanticAudioState.Analysing, SongsSearchable = 10 };
        pools.PollStatus();
        pools.Refresh();
        Assert.Single(_asked);

        // Ten more songs heard a minute later: not yet.
        _status = _status with { SongsSearchable = 20 };
        pools.PollStatus();
        pools.Refresh();
        Assert.Single(_asked);

        // The analysis finishing: at once.
        _status = _status with { State = SemanticAudioState.UpToDate, SongsSearchable = 300 };
        pools.PollStatus();
        pools.Refresh();
        Assert.Equal(2, _asked.Count);
        Assert.NotEmpty(playlist.Tracks);
    }

    [Fact]
    public void Only_Vibe_Playlists_are_touched()
    {
        var ordinary = new Playlist("Mine", [_songs[3]]);
        _library.AddPlaylist(ordinary);

        Pools().Refresh();

        Assert.Equal([_songs[3]], ordinary.Tracks);
        Assert.Empty(_asked);
    }
}
