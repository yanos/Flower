using System;
using System.Collections.Generic;
using System.Linq;
using Flower.Models;
using Xunit;
namespace Flower.Tests;

public class PlaylistTests
{
    private static Track T(string title) => new Track { Title = title, Path = $"/music/{title}.mp3" };

    // Regression test: App.axaml.cs constructs MainPlaylist directly from
    // Library.Tracks (new MainPlaylist(library.Tracks)) and, on every rescan,
    // calls mainPlaylist.ReplaceAll(freshTracks) immediately before
    // library.UpdateTracks(freshTracks) - see App.axaml.cs and
    // MainViewModel.RebuildDatabaseAsync. If the Playlist constructor aliased
    // the passed-in list instead of copying it, ReplaceAll's Clear()+AddRange()
    // would mutate Library.Tracks itself (the same underlying list object) in
    // place *before* UpdateTracks got a chance to read it as "previous" state -
    // silently discarding PlayCount/DateAdded/ImportedPlayCount on every single
    // rescan, confirmed against the real reported bug.
    [Fact]
    public void Constructor_copies_the_track_list_so_a_caller_holding_the_same_reference_is_unaffected_by_ReplaceAll()
    {
        var source = new List<Track> { T("A") };
        var playlist = new Playlist("Main", source);

        playlist.ReplaceAll(new List<Track> { T("B") });

        Assert.Single(source);
        Assert.Equal("A", source[0].Title);
    }

    [Fact]
    public void AppendTrack_adds_to_the_end()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("B") });
        playlist.AppendTrack(T("C"));

        Assert.Equal(new[] { "A", "B", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void InsertTrack_inserts_at_given_index()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("C") });
        playlist.InsertTrack(1, T("B"));

        Assert.Equal(new[] { "A", "B", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void RemoveTrack_removes_the_given_track()
    {
        var b = T("B");
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), b, T("C") });
        playlist.RemoveTrack(b);

        Assert.Equal(new[] { "A", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void ReplaceAll_clears_and_replaces_contents()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("B") });
        playlist.ReplaceAll(new List<Track> { T("X"), T("Y"), T("Z") });

        Assert.Equal(new[] { "X", "Y", "Z" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void GetTrack_returns_null_when_index_out_of_range()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A") });

        Assert.Null(playlist.GetTrack(5));
        Assert.NotNull(playlist.GetTrack(0));
    }






    // ── Identity ─────────────────────────────────────────────────────────────
    //
    // Track used to be a record, so IndexOf/Remove compared ~40 fields by
    // value. Two genuinely different tracks that happened to share tag data -
    // untagged rips, "Track 01", the silence tracks on a lot of CDs - were
    // indistinguishable, so next/previous/remove all silently acted on
    // whichever one appeared first in the list. These pin the Id-based
    // identity that replaced it; the next/previous half of it now lives in
    // PlaylistControlViewModelTests, which owns queue navigation.

    private static Track Untagged() => new Track { Title = null, Artists = null, Album = null, Path = null };



    [Fact]
    public void RemoveTrack_removes_the_requested_instance_not_the_first_lookalike()
    {
        var first  = Untagged();
        var second = Untagged();
        var playlist = new Playlist("My Mix", new List<Track> { first, second });

        playlist.RemoveTrack(second);

        Assert.Same(first, Assert.Single(playlist.Tracks));
    }

    // Two Tracks describing the same song but constructed separately (one from
    // a disk scan, one from a peer's sync manifest) are deliberately NOT equal:
    // matching those up is SyncKey's job, done explicitly at the points that
    // need it, not something list lookups should be doing implicitly.
    [Fact]
    public void Separately_constructed_tracks_for_the_same_song_are_not_equal()
    {
        var scanned = T("A");
        var synced  = T("A");

        Assert.NotEqual(scanned, synced);
        Assert.Equal(scanned.SyncKey, synced.SyncKey);
    }

    [Fact]
    public void A_clone_keeps_the_original_identity_but_not_its_mutable_dictionary()
    {
        var original = T("A");
        original.RemotePlayCounts["peer"] = 3;

        var clone = original.Clone();
        clone.Path = "http://peer/stream";
        clone.RemotePlayCounts["peer"] = 9;

        // Same track as far as the play queue is concerned - this is what lets
        // a placeholder streamed from a peer still be found in the queue.
        Assert.Equal(original, clone);
        Assert.Equal(3, original.RemotePlayCounts["peer"]);
    }

    // ── MoveTrack (drag-to-reorder) ──────────────────────────────────────────

    // UpdatedAt is the entire basis on which PlaylistSyncPlanner decides "did
    // this side change?", so a drag that reorders nothing must not touch it -
    // otherwise every aborted drag manufactures a sync-visible edit. Same
    // reasoning as RemoveTrack's own no-op guard (ARCHITECTURE-REVIEW 2.4).
    [Fact]
    public void MoveTrack_reorders_and_bumps_UpdatedAt()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var playlist = new Playlist("p", new List<Track> { a, b, c });
        var before = playlist.UpdatedAt;

        Assert.True(playlist.MoveTrack(c, a));

        Assert.Equal(new[] { "C", "A", "B" }, playlist.Tracks.Select(t => t.Title));
        Assert.True(playlist.UpdatedAt > before);
    }

    [Fact]
    public void MoveTrack_to_the_end_reorders_and_bumps_UpdatedAt()
    {
        var a = T("A");
        var b = T("B");
        var playlist = new Playlist("p", new List<Track> { a, b });
        var before = playlist.UpdatedAt;

        Assert.True(playlist.MoveTrack(a, null));

        Assert.Equal(new[] { "B", "A" }, playlist.Tracks.Select(t => t.Title));
        Assert.True(playlist.UpdatedAt > before);
    }

    [Theory]
    // Dropped onto the entry that already follows it.
    [InlineData("A", "B")]
    // Dropped onto itself - before the guard this removed it first, failed to
    // find the target in the shortened list, and silently appended it.
    [InlineData("B", "B")]
    // Already last, dropped at the end.
    [InlineData("C", null)]
    public void MoveTrack_that_changes_nothing_reports_false_and_leaves_UpdatedAt_alone(string dragged, string? insertBefore)
    {
        var tracks = new List<Track> { T("A"), T("B"), T("C") };
        var playlist = new Playlist("p", tracks);
        var before = playlist.UpdatedAt;

        var moved = playlist.MoveTrack(
            tracks.Single(t => t.Title == dragged),
            insertBefore == null ? null : tracks.Single(t => t.Title == insertBefore));

        Assert.False(moved);
        Assert.Equal(new[] { "A", "B", "C" }, playlist.Tracks.Select(t => t.Title));
        Assert.Equal(before, playlist.UpdatedAt);
    }

    [Fact]
    public void MoveTrack_of_a_track_the_playlist_does_not_contain_reports_false()
    {
        var playlist = new Playlist("p", new List<Track> { T("A") });
        var before = playlist.UpdatedAt;

        Assert.False(playlist.MoveTrack(T("stranger"), null));

        Assert.Equal(before, playlist.UpdatedAt);
    }

    // The invariant the whole smart-playlist design rests on. UpdatedAt is the
    // only thing PlaylistSyncPlanner consults to decide "did this side change?"
    // against its per-peer baseline, so a re-evaluation that bumped it would
    // manufacture a sync-visible edit on every device, on every rescan, out of
    // a change nobody made - and make a content conflict reachable for a
    // playlist whose content is not its state.
    [Fact]
    public void Materialize_replaces_the_contents_without_bumping_UpdatedAt()
    {
        var playlist = new Playlist("Smart", new List<Track> { T("A") });
        var before = playlist.UpdatedAt;

        Assert.True(playlist.Materialize(new List<Track> { T("B"), T("C") }));

        Assert.Equal(new[] { "B", "C" }, playlist.Tracks.Select(t => t.Title));
        Assert.Equal(before, playlist.UpdatedAt);
    }

    // So a recomputation pass over every smart playlist writes only the ones
    // that actually moved - which, after the first pass, is usually none.
    [Fact]
    public void Materialize_reports_false_when_the_result_is_the_same_tracks_in_the_same_order()
    {
        var a = T("A");
        var b = T("B");
        var playlist = new Playlist("Smart", new List<Track> { a, b });

        Assert.False(playlist.Materialize(new List<Track> { a, b }));
        Assert.True(playlist.Materialize(new List<Track> { b, a }));
    }

    [Fact]
    public void Materialize_copies_the_list_it_is_given()
    {
        var source = new List<Track> { T("A") };
        var playlist = new Playlist("Smart", new List<Track>());

        playlist.Materialize(source);
        source.Clear();

        Assert.Single(playlist.Tracks);
    }

    // Editing the rules is a real user edit, unlike materializing their result,
    // and it is the one thing about a smart playlist sync has to carry.
    [Fact]
    public void Setting_Rules_is_an_edit_and_bumps_UpdatedAt()
    {
        // An explicit stale UpdatedAt rather than "now", so the assertion
        // below cannot depend on the clock's granularity.
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var playlist = new Playlist(Guid.NewGuid(), "Smart", new List<Track>(), before);
        Assert.False(playlist.IsSmart);

        playlist.Rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")));

        Assert.True(playlist.IsSmart);
        Assert.True(playlist.UpdatedAt > before);
    }
}
