using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Services;

namespace Flower.Tests.Playlists;

// The planner on its own: each playlist read against the copy this device last
// agreed with the server on, by the table in docs/PLAYLIST-SYNC-PLAN.md, "The
// protocol". Played end to end against a server in SyncScenarioTests.
public class PlaylistSyncPlannerTests
{
    private static Track T(string title, int durationSeconds = 200) =>
        new Track { Title = title, Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(durationSeconds), Path = $"/music/{title}.mp3" };

    private static PlaylistSyncTrackDto Dto(string title, int durationSeconds = 200) =>
        new PlaylistSyncTrackDto(title, "Artist", "Album", durationSeconds);

    private static readonly Dictionary<Guid, PlaylistAgreement> NothingAgreed = new();

    private static PlaylistSyncDecision PlanOne(
        Playlist? local, PlaylistSyncPlaylistDto? remote, PlaylistAgreement? agreed = null, PlaylistDeletionDto? deletion = null)
    {
        var id = local?.Id ?? remote?.Id ?? deletion!.Id;
        var agreements = agreed == null ? NothingAgreed : new Dictionary<Guid, PlaylistAgreement> { [id] = agreed };
        return Assert.Single(PlaylistSyncPlanner.Plan(
            local == null ? [] : [local], remote == null ? [] : [remote], deletion == null ? [] : [deletion],
            agreements, new PlaylistTrackIndex([])));
    }

    // A playlist this device holds, agreed with the server at revision 10.
    private static (Playlist Local, PlaylistAgreement Agreed) Agreed(string name = "Favorites", params string[] titles)
    {
        var local = new Playlist(name, titles.Select(t => T(t)).ToList());
        return (local, new PlaylistAgreement(10, PlaylistSyncMapper.ToDto(local) with { Revision = 10 }));
    }

    private static PlaylistSyncPlaylistDto MovedOn(PlaylistSyncPlaylistDto copy) => copy with { Revision = 20, History = [10] };

    [Fact]
    public void A_playlist_only_on_the_server_is_adopted()
    {
        var remote = new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Road Trip", DateTimeOffset.UtcNow, [Dto("A")], Revision: 5);

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, PlanOne(null, remote).Kind);
    }

    // New here, or lost by a server restored from a backup older than it: it
    // goes up as new either way.
    [Fact]
    public void A_playlist_the_server_has_no_record_of_goes_up_as_new()
    {
        var (local, agreed) = Agreed("Road Trip", "A");

        var fresh = PlanOne(local, null);
        var lost = PlanOne(local, null, agreed);

        Assert.Equal((PlaylistSyncDecisionKind.Push, 0L), (fresh.Kind, fresh.BaseRevision));
        Assert.Equal((PlaylistSyncDecisionKind.Push, 0L), (lost.Kind, lost.BaseRevision));
    }

    [Fact]
    public void The_same_content_with_nothing_agreed_is_agreed_on()
    {
        var local = new Playlist("Favorites", [T("A"), T("B")]);
        var remote = PlaylistSyncMapper.ToDto(local) with { Revision = 5 };

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, PlanOne(local, remote).Kind);
    }

    [Fact]
    public void Different_content_with_nothing_agreed_is_asked_about()
    {
        var local = new Playlist("Favorites (mine)", [T("A")]);
        var remote = new PlaylistSyncPlaylistDto(local.Id, "Favorites (theirs)", DateTimeOffset.UtcNow, [Dto("B")], Revision: 5);

        Assert.Equal(PlaylistSyncDecisionKind.Conflict, PlanOne(local, remote).Kind);
    }

    [Fact]
    public void Unchanged_on_both_sides_is_no_change()
    {
        var (local, agreed) = Agreed("Favorites", "A");

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, PlanOne(local, agreed.Copy!).Kind);
    }

    [Fact]
    public void Changed_here_only_goes_up_over_the_agreed_revision()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.AppendTrack(T("B"));

        var decision = PlanOne(local, agreed.Copy!, agreed);

        Assert.Equal((PlaylistSyncDecisionKind.Push, 10L), (decision.Kind, decision.BaseRevision));
    }

    // UF-22: what decides is the content, not the clock - an edit undone is
    // no edit, whatever it did to UpdatedAt.
    [Fact]
    public void An_edit_and_its_undo_is_no_change()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.AppendTrack(T("B"));
        local.RemoveEntriesAt([1]);

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, PlanOne(local, agreed.Copy!, agreed).Kind);
    }

    [Fact]
    public void Changed_on_the_server_only_is_adopted()
    {
        var (local, agreed) = Agreed("Favorites", "A");

        var decision = PlanOne(local, MovedOn(agreed.Copy! with { Name = "Favorites Renamed" }), agreed);

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, decision.Kind);
    }

    // Moving a playlist into a folder, or to a new place beside the others,
    // changes nothing about its songs or its name, and has to read as an edit
    // all the same or it never leaves the device it was made on.
    [Fact]
    public void A_playlist_moved_into_a_folder_or_a_new_place_is_a_change_to_carry()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.ParentId = Guid.NewGuid();
        var (placed, placedAgreed) = Agreed("Other", "A");
        placed.SortKey = 2.5;

        Assert.Equal(PlaylistSyncDecisionKind.Push, PlanOne(local, agreed.Copy!, agreed).Kind);
        Assert.Equal(PlaylistSyncDecisionKind.Push, PlanOne(placed, placedAgreed.Copy!, placedAgreed).Kind);
    }

    [Fact]
    public void A_folder_and_where_a_playlist_sits_travel_and_come_back()
    {
        var folder = Playlist.NewFolder("Rock");
        var inside = new Playlist("Live", new List<Track>()) { ParentId = folder.Id, SortKey = 2.5 };

        var folderBack = PlaylistSyncMapper.ToPlaylist(PlaylistSyncMapper.ToDto(folder), new List<Track>());
        var insideBack = PlaylistSyncMapper.ToPlaylist(PlaylistSyncMapper.ToDto(inside), new List<Track>());

        Assert.True(folderBack.IsFolder);
        Assert.Equal((folder.Id, 2.5), (insideBack.ParentId!.Value, insideBack.SortKey!.Value));
        Assert.Equal(inside.UpdatedAt, insideBack.UpdatedAt);
    }

    [Fact]
    public void Changed_on_both_sides_differently_is_asked_about()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.Name = "Favorites (mine)";

        var decision = PlanOne(local, MovedOn(agreed.Copy! with { Name = "Favorites (theirs)" }), agreed);

        Assert.Equal(PlaylistSyncDecisionKind.Conflict, decision.Kind);
    }

    // UF-23: renamed here, a song added there. Merged from the agreed copy,
    // and sent over the server's revision, with nobody asked.
    [Fact]
    public void Changed_on_both_sides_in_ways_that_combine_is_merged()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.Name = "Road Trip";
        var theirs = MovedOn(agreed.Copy! with { Tracks = [.. agreed.Copy!.Tracks, Dto("B")] });

        var decision = PlanOne(local, theirs, agreed);

        Assert.Equal((PlaylistSyncDecisionKind.Merge, 20L), (decision.Kind, decision.BaseRevision));
        Assert.Equal("Road Trip", decision.Merged!.Name);
        Assert.Equal(["A", "B"], decision.Merged.Entries.Select(e => e.Track?.Title ?? e.Song!.Title));
    }

    // This device's edit is already in the server's: there is nothing to send.
    [Fact]
    public void A_merge_that_comes_out_as_the_servers_copy_is_adopted()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.AppendTrack(T("B"));
        var mine = PlaylistSyncMapper.ToDto(local);
        var theirs = MovedOn(mine with { Tracks = [.. mine.Tracks, Dto("C")], Revision = 10 });

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, PlanOne(local, theirs, agreed).Kind);
    }

    // The same edit made on both sides, or this device's own change whose
    // answer never came back.
    [Fact]
    public void Changed_on_both_sides_to_the_same_content_is_agreed_on()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.Name = "Renamed";

        var decision = PlanOne(local, MovedOn(agreed.Copy! with { Name = "Renamed" }), agreed);

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, decision.Kind);
    }

    // UF-6: a server restored from a backup older than what this device
    // agreed. Whatever this device holds goes back up.
    [Fact]
    public void A_server_behind_what_this_device_agreed_gets_this_devices_copy_back()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        var restored = agreed.Copy! with { Revision = 4, History = [1, 2, 3] };

        var decision = PlanOne(local, restored, agreed);

        Assert.Equal((PlaylistSyncDecisionKind.Push, 4L), (decision.Kind, decision.BaseRevision));
    }

    // Older than every revision the server remembers: no history in common.
    [Fact]
    public void An_agreed_revision_the_server_no_longer_remembers_is_no_history_in_common()
    {
        var (local, agreed) = Agreed("Favorites", "A");
        local.Name = "Mine";

        var decision = PlanOne(local, agreed.Copy! with { Name = "Theirs", Revision = 90, History = [80] }, agreed);

        Assert.Equal(PlaylistSyncDecisionKind.Conflict, decision.Kind);
    }

    [Fact]
    public void Deleted_here_and_unchanged_on_the_server_is_deleted_there()
    {
        var (_, agreed) = Agreed("Road Trip", "A");

        var decision = PlanOne(null, agreed.Copy!, agreed);

        Assert.Equal((PlaylistSyncDecisionKind.PushDelete, 10L), (decision.Kind, decision.BaseRevision));
    }

    // Edit beats delete.
    [Fact]
    public void Deleted_here_and_edited_on_the_server_comes_back()
    {
        var (_, agreed) = Agreed("Road Trip", "A");

        var decision = PlanOne(null, MovedOn(agreed.Copy! with { Name = "Renamed" }), agreed);

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, decision.Kind);
    }

    [Fact]
    public void Deleted_on_the_server_over_an_agreed_version_and_unchanged_here_is_deleted_here()
    {
        var (local, agreed) = Agreed("Road Trip", "A");

        var decision = PlanOne(local, null, agreed, new PlaylistDeletionDto(local.Id, 20, [10]));

        Assert.Equal(PlaylistSyncDecisionKind.DeleteHere, decision.Kind);
    }

    [Fact]
    public void Deleted_on_the_server_and_edited_here_goes_back_up_over_the_deletion()
    {
        var (local, agreed) = Agreed("Road Trip", "A");
        local.AppendTrack(T("B"));

        var decision = PlanOne(local, null, agreed, new PlaylistDeletionDto(local.Id, 20, [10]));

        Assert.Equal((PlaylistSyncDecisionKind.Push, 20L), (decision.Kind, decision.BaseRevision));
    }

    // UF-21: with nothing agreed, this device cannot tell whether its copy
    // holds an edit, so it asks once rather than bringing the playlist back.
    [Fact]
    public void Deleted_on_the_server_with_nothing_agreed_here_is_asked_about()
    {
        var local = new Playlist("Road Trip", [T("A")]);

        var decision = PlanOne(local, null, deletion: new PlaylistDeletionDto(local.Id, 20, [10]));

        Assert.Equal(PlaylistSyncDecisionKind.ConfirmDeletion, decision.Kind);
    }

    [Fact]
    public void Deleted_on_both_sides_leaves_only_the_agreement_to_forget()
    {
        var (local, agreed) = Agreed("Road Trip", "A");

        Assert.Equal(PlaylistSyncDecisionKind.Forget, PlanOne(null, null, agreed, new PlaylistDeletionDto(local.Id, 20, [10])).Kind);
    }

    [Fact]
    public void Plan_produces_one_decision_per_distinct_playlist_id_across_both_sides()
    {
        var shared = new Playlist("Shared", [T("A")]);
        var mine = new Playlist("Mine", [T("B")]);
        var theirs = new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Theirs", DateTimeOffset.UtcNow, [Dto("C")], Revision: 3);

        var decisions = PlaylistSyncPlanner.Plan(
            [shared, mine], [PlaylistSyncMapper.ToDto(shared) with { Revision = 2 }, theirs], [], NothingAgreed, new PlaylistTrackIndex([]));

        Assert.Equal(new[] { shared.Id, mine.Id, theirs.Id }.Order(), decisions.Select(d => d.PlaylistId).Order());
    }

    // A Vibe Playlist is decided on its sentence: the songs are the server's
    // ranking, which the device takes as they stand (PlaylistSyncService).
    [Fact]
    public void The_same_sentence_on_both_sides_is_no_change_whatever_the_songs()
    {
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var local = new Playlist(id, "Rainy", [T("A")], at, vibePrompt: "music for a rainy day");
        var remote = new PlaylistSyncPlaylistDto(id, "Rainy", at, [Dto("B"), Dto("C")], VibePrompt: "music for a rainy day", Revision: 4);

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, PlanOne(local, remote).Kind);
    }

    [Fact]
    public void A_newer_sentence_wins_on_either_side()
    {
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var local = new Playlist(id, "Rainy", [], at, vibePrompt: "music for a rainy day");
        var newerThere = new PlaylistSyncPlaylistDto(id, "Rainy", at.AddMinutes(1), [], VibePrompt: "rain on a tin roof", Revision: 4);
        var olderThere = newerThere with { UpdatedAt = at.AddMinutes(-1) };

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, PlanOne(local, newerThere).Kind);
        Assert.Equal(PlaylistSyncDecisionKind.Push, PlanOne(local, olderThere).Kind);
    }

    // A playlist made ordinary, pushed, then turned into a Vibe Playlist by
    // the editor's save: the sentence goes up.
    [Fact]
    public void A_playlist_given_a_sentence_here_goes_up_over_the_ordinary_copy_there()
    {
        var local = new Playlist("New Vibe Playlist", []);
        var agreed = new PlaylistAgreement(10, PlaylistSyncMapper.ToDto(local) with { Revision = 10 });
        local.VibePrompt = "music for a rainy day";

        Assert.Equal(PlaylistSyncDecisionKind.Push, PlanOne(local, agreed.Copy!, agreed).Kind);
    }

    [Fact]
    public void The_sentence_and_switch_survive_the_wire()
    {
        var playlist = new Playlist("Rainy", [T("A")]) { VibePrompt = "sad piano", VibeInstrumental = true };

        var back = PlaylistSyncMapper.ToPlaylist(PlaylistSyncMapper.ToDto(playlist), [T("A")]);

        Assert.Equal("sad piano", back.VibePrompt);
        Assert.True(back.VibeInstrumental);
    }

    // The server rebuilds a pool without moving UpdatedAt, so /info's token
    // has to move with the songs or a device never comes back for them.
    [Fact]
    public void The_playlists_token_moves_when_a_vibe_playlists_pool_does()
    {
        var playlist = new Playlist("Rainy", [T("A")]) { VibePrompt = "music for a rainy day" };
        var before = PlaylistSyncMapper.TokenOf([playlist]);

        playlist.Materialize([T("B")]);

        Assert.NotEqual(before, PlaylistSyncMapper.TokenOf([playlist]));
    }
}
