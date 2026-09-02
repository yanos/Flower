using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Phase 3 of docs/SMART-PLAYLIST-PLAN.md: when a recomputation runs, what it
// writes, and - the half that is easy to get wrong - which library events have
// to trigger one.
public class SmartPlaylistRefresherTests
{
    private static Track T(string title, int playCount = 0, DateTimeOffset? added = null) =>
        new Track
        {
            Title = title,
            Path = $"/music/{title}.mp3",
            PlayCount = playCount,
            DateAdded = added ?? DateTimeOffset.UtcNow.AddYears(-1),
        };

    // Records what reached the database, so a test can tell "recomputed" from
    // "recomputed and persisted" - a pass that finds nothing must not write.
    private sealed class RecordingPlaylistStore : IPlaylistStore
    {
        public int Saves { get; private set; }

        public void Save(IEnumerable<Playlist> playlists) => Saves++;
    }

    private static (Library Library, RecordingPlaylistStore Store) NewLibrary(params Track[] tracks)
    {
        var store = new RecordingPlaylistStore();
        return (new Library(tracks.ToList(), NullLogger<Library>.Instance, null, store), store);
    }

    private static SmartPlaylistRefresher NewRefresher(Library library) =>
        new(library, NullLogger<SmartPlaylistRefresher>.Instance);

    private static Playlist Smart(string name, SmartPlaylistRules rules)
    {
        var playlist = new Playlist(name, new List<Track>()) { Rules = rules };
        return playlist;
    }

    private static SmartPlaylistRules Played =>
        SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0)));

    [Fact]
    public void Refresh_materializes_a_smart_playlist_from_the_library()
    {
        var (library, store) = NewLibrary(T("A", playCount: 3), T("B"));
        var playlist = Smart("Played", Played);
        library.ResetPlaylists([playlist]);

        using var refresher = NewRefresher(library);
        var changed = refresher.Refresh();

        Assert.Equal([playlist], changed);
        Assert.Equal(["A"], playlist.Tracks.Select(t => t.Title));
        Assert.Equal(1, store.Saves);
    }

    // The invariant "both ends bake from the same recipe" rests on: a
    // recomputation leaves PlaylistSyncPlanner no fingerprint to find, because
    // UpdatedAt is the only thing it consults.
    [Fact]
    public void Refresh_does_not_bump_UpdatedAt()
    {
        var (library, _) = NewLibrary(T("A", playCount: 3));
        var playlist = Smart("Played", Played);
        library.ResetPlaylists([playlist]);
        var before = playlist.UpdatedAt;

        using var refresher = NewRefresher(library);
        refresher.Refresh();

        Assert.NotEmpty(playlist.Tracks);
        Assert.Equal(before, playlist.UpdatedAt);
    }

    // A pass that changes nothing is the common case - it runs on every play -
    // and rewriting the playlist table each time would be a write per song.
    [Fact]
    public void A_pass_that_changes_nothing_does_not_write()
    {
        var (library, store) = NewLibrary(T("A", playCount: 3));
        library.ResetPlaylists([Smart("Played", Played)]);

        using var refresher = NewRefresher(library);
        refresher.Refresh();
        var afterFirst = store.Saves;

        Assert.Empty(refresher.Refresh());
        Assert.Equal(afterFirst, store.Saves);
    }

    [Fact]
    public void Ordinary_playlists_are_left_alone()
    {
        var (library, _) = NewLibrary(T("A", playCount: 3), T("B"));
        var ordinary = new Playlist("Hand picked", [T("B")]);
        library.ResetPlaylists([ordinary]);

        using var refresher = NewRefresher(library);

        Assert.Empty(refresher.Refresh());
        Assert.Equal(["B"], ordinary.Tracks.Select(t => t.Title));
    }

    // "Evaluate once when saved, then freeze" - iTunes' non-live-updating
    // smart playlist. The recurring pass has to skip it entirely.
    [Fact]
    public void A_frozen_playlist_is_not_recomputed_by_the_pass_but_is_by_RefreshOne()
    {
        var (library, _) = NewLibrary(T("A", playCount: 3));
        var playlist = Smart("Frozen", Played with { LiveUpdating = false });
        library.ResetPlaylists([playlist]);

        using var refresher = NewRefresher(library);

        Assert.Empty(refresher.Refresh());
        Assert.Empty(playlist.Tracks);

        Assert.True(refresher.RefreshOne(playlist));
        Assert.Equal(["A"], playlist.Tracks.Select(t => t.Title));
    }

    // The reason the pass evaluates everything together rather than reacting
    // per playlist: a membership rule makes one playlist's result another's
    // input, so the order is not free to be whatever the events decided.
    [Fact]
    public void A_membership_rule_sees_the_result_of_this_pass_not_the_last_one()
    {
        var (library, _) = NewLibrary(T("A", playCount: 3), T("B"));

        var heard = Smart("Already heard", Played);
        var fresh = Smart("Fresh", SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(heard.Id))));

        // Deliberately the order that would be wrong if the pass just walked
        // the list: Fresh depends on Already heard and is listed first.
        library.ResetPlaylists([fresh, heard]);

        using var refresher = NewRefresher(library);
        refresher.Refresh();

        Assert.Equal(["A"], heard.Tracks.Select(t => t.Title));
        Assert.Equal(["B"], fresh.Tracks.Select(t => t.Title));
    }

    // The editor is what keeps a cycle out of the database
    // (SmartPlaylistGraph.ReferenceCandidates), so one here came from a
    // hand-edited database or a peer. Every smart playlist keeps its last good
    // contents rather than the pass emptying them all.
    [Fact]
    public void A_cycle_is_refused_without_disturbing_what_is_already_materialized()
    {
        var (library, store) = NewLibrary(T("A"), T("B"));

        var first = Smart("First", SmartPlaylistRules.MatchAll());
        var second = Smart("Second", SmartPlaylistRules.MatchAll());
        first.Rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(second.Id)));
        second.Rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(first.Id)));

        first.Materialize([T("kept")]);
        library.ResetPlaylists([first, second]);

        using var refresher = NewRefresher(library);

        Assert.Empty(refresher.Refresh());
        Assert.Equal(["kept"], first.Tracks.Select(t => t.Title));
        Assert.Equal(0, store.Saves);
    }

    // A LimitSelector.Random playlist is re-evaluated on every play, since play
    // counts are an input. If the draw moved each time it would reshuffle
    // itself under a listener partway through it.
    [Fact]
    public void A_random_limit_draws_the_same_tracks_from_the_same_library()
    {
        var tracks = Enumerable.Range(0, 40).Select(i => T($"T{i:00}", playCount: 1)).ToArray();
        var (library, _) = NewLibrary(tracks);
        var playlist = Smart("Surprise", Played with
        {
            Limit = new SmartLimit(5, LimitUnit.Items, LimitSelector.Random),
        });
        library.ResetPlaylists([playlist]);

        using var refresher = NewRefresher(library);
        refresher.Refresh();
        var first = playlist.Tracks.Select(t => t.Title).ToList();

        Assert.Equal(5, first.Count);
        Assert.Empty(refresher.Refresh());
        Assert.Equal(first, playlist.Tracks.Select(t => t.Title));
    }

    // The trap the trigger list exists for. Play count and LastPlayedAt
    // deliberately do not raise TracksUpdated - they were split onto
    // TrackStatsChanged (ARCHITECTURE-REVIEW Tier 1.1) - and they are exactly
    // what "Recently Played" and "Most Played" are built on.
    [Fact]
    public async Task A_play_triggers_a_pass()
    {
        var track = T("A");
        var (library, _) = NewLibrary(track);
        var playlist = Smart("Played", Played);
        library.ResetPlaylists([playlist]);

        using var refresher = Started(library);
        Assert.Empty(playlist.Tracks);

        library.IncrementPlayCount(track);

        await Eventually(() => playlist.Tracks.Count == 1);
    }

    // SetStarred reaches neither TracksUpdated nor TrackStatsChanged - it is
    // why Library.TrackStarsChanged exists.
    [Fact]
    public async Task A_star_triggers_a_pass()
    {
        var track = T("A");
        var (library, _) = NewLibrary(track);
        var playlist = Smart("Starred", SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Starred, SmartOperator.Is, new SmartValue.Bool(true))));
        library.ResetPlaylists([playlist]);

        using var refresher = Started(library);
        Assert.Empty(playlist.Tracks);

        library.SetStarred(StarTarget.Song, track.Id.ToString(), starred: true);

        await Eventually(() => playlist.Tracks.Count == 1);
    }

    // A membership rule makes another playlist's contents an input, so editing
    // that playlist is a track-set change for everything referencing it.
    [Fact]
    public async Task Editing_a_referenced_playlist_triggers_a_pass()
    {
        var (library, _) = NewLibrary(T("A"), T("B"));
        var ordinary = new Playlist("Hand picked", []);
        var fresh = Smart("Not hand picked", SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(ordinary.Id))));
        library.ResetPlaylists([ordinary, fresh]);

        using var refresher = Started(library);
        Assert.Equal(2, fresh.Tracks.Count);

        ordinary.AppendTrack(library.Tracks[0]);

        await Eventually(() => fresh.Tracks.Count == 1);
    }

    // The refresher's own write must not come back through PlaylistsChanged
    // and start another pass - see Library.SavePlaylists.
    [Fact]
    public async Task A_pass_does_not_trigger_another_pass()
    {
        var (library, _) = NewLibrary(T("A", playCount: 1));
        library.ResetPlaylists([Smart("Played", Played)]);

        var passes = 0;
        library.PlaylistsChanged += (_, _) => passes++;

        using var refresher = Started(library);
        await Task.Delay(Cooldown * 6);

        Assert.Equal(0, passes);
    }

    [Fact]
    public void Disposing_stops_the_subscriptions()
    {
        var track = T("A");
        var (library, _) = NewLibrary(track);
        var playlist = Smart("Played", Played);
        library.ResetPlaylists([playlist]);

        var refresher = Started(library);
        refresher.Dispose();

        library.IncrementPlayCount(track);
        Assert.Empty(playlist.Tracks);
    }

    // Short enough that the trigger tests are not a wait, long enough that a
    // loaded CI machine still collapses the burst it is there to collapse.
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(20);

    private static SmartPlaylistRefresher Started(Library library)
    {
        SmartPlaylistRefresher.Cooldown = Cooldown;
        var refresher = NewRefresher(library);
        refresher.Start();
        return refresher;
    }

    // Polls rather than sleeping out a fixed span: the pass is debounced onto a
    // timer, so the only honest assertion is "this becomes true", and polling
    // makes the fast case fast without making the slow case flaky.
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(5);
        }

        Assert.True(condition(), "The recomputation pass never produced the expected result.");
    }
}
