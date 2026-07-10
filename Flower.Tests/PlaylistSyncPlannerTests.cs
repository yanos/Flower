using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class PlaylistSyncPlannerTests
{
    private static Track T(string title, int durationSeconds = 200) =>
        new Track { Title = title, Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(durationSeconds), Path = $"/music/{title}.mp3" };

    private static PlaylistSyncTrackDto Dto(string title, int durationSeconds = 200) =>
        new PlaylistSyncTrackDto(title, "Artist", "Album", durationSeconds);

    private static readonly Func<Guid, DateTimeOffset?> NoBaseline = _ => null;

    [Fact]
    public void Playlist_only_on_remote_is_adopted()
    {
        var id = Guid.NewGuid();
        var remote = new PlaylistSyncPlaylistDto(id, "Road Trip", DateTimeOffset.UtcNow, new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist>(), new List<PlaylistSyncPlaylistDto> { remote }, NoBaseline);

        var decision = Assert.Single(decisions);
        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, decision.Kind);
        Assert.Equal(id, decision.PlaylistId);
    }

    [Fact]
    public void Playlist_only_on_local_is_kept()
    {
        var id = Guid.NewGuid();
        var local = new Playlist(id, "Road Trip", new List<Track> { T("A") }, DateTimeOffset.UtcNow);

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto>(), NoBaseline);

        var decision = Assert.Single(decisions);
        Assert.Equal(PlaylistSyncDecisionKind.KeepLocal, decision.Kind);
    }

    [Fact]
    public void Identical_content_on_both_sides_is_NoChange_even_with_no_baseline()
    {
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var local = new Playlist(id, "Favorites", new List<Track> { T("A"), T("B") }, updatedAt);
        var remote = new PlaylistSyncPlaylistDto(id, "Favorites", updatedAt, new List<PlaylistSyncTrackDto> { Dto("A"), Dto("B") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, NoBaseline);

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Only_local_changed_since_baseline_keeps_local()
    {
        var id = Guid.NewGuid();
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var local = new Playlist(id, "Favorites", new List<Track> { T("A"), T("B") }, baseline.AddMinutes(5));
        var remote = new PlaylistSyncPlaylistDto(id, "Favorites", baseline, new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, _ => baseline);

        Assert.Equal(PlaylistSyncDecisionKind.KeepLocal, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Only_remote_changed_since_baseline_adopts_remote()
    {
        var id = Guid.NewGuid();
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var local = new Playlist(id, "Favorites", new List<Track> { T("A") }, baseline);
        var remote = new PlaylistSyncPlaylistDto(id, "Favorites Renamed", baseline.AddMinutes(5), new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, _ => baseline);

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Both_sides_changed_since_baseline_is_a_conflict()
    {
        var id = Guid.NewGuid();
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var local = new Playlist(id, "Favorites (mine)", new List<Track> { T("A") }, baseline.AddMinutes(3));
        var remote = new PlaylistSyncPlaylistDto(id, "Favorites (theirs)", baseline.AddMinutes(5), new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, _ => baseline);

        Assert.Equal(PlaylistSyncDecisionKind.Conflict, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Differing_content_with_no_prior_baseline_is_a_conflict_not_an_automatic_pick()
    {
        var id = Guid.NewGuid();
        var local = new Playlist(id, "Favorites (mine)", new List<Track> { T("A") }, DateTimeOffset.UtcNow);
        var remote = new PlaylistSyncPlaylistDto(id, "Favorites (theirs)", DateTimeOffset.UtcNow, new List<PlaylistSyncTrackDto> { Dto("B") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, NoBaseline);

        Assert.Equal(PlaylistSyncDecisionKind.Conflict, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Renaming_a_playlist_does_not_change_its_identity_for_matching()
    {
        // Same Id, different Name on each side, but same track content and equal
        // UpdatedAt - this exercises that Id (not Name) is what pairs local/remote.
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var local = new Playlist(id, "Old Name", new List<Track> { T("A") }, updatedAt);
        var remote = new PlaylistSyncPlaylistDto(id, "Old Name", updatedAt, new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto> { remote }, NoBaseline);

        var decision = Assert.Single(decisions);
        Assert.Equal(id, decision.PlaylistId);
        Assert.Equal(PlaylistSyncDecisionKind.NoChange, decision.Kind);
    }

    [Fact]
    public void Playlist_deleted_remotely_since_a_prior_baseline_is_deleted_not_kept()
    {
        // Regression test for the reported bug: deleting a playlist on one
        // device never removed it from the other, even after a restart -
        // Plan previously had no way to distinguish "remote never had this"
        // from "remote deleted this", and treated both as KeepLocal.
        var id = Guid.NewGuid();
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var local = new Playlist(id, "Road Trip", new List<Track> { T("A") }, baseline);

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist> { local }, new List<PlaylistSyncPlaylistDto>(), _ => baseline);

        Assert.Equal(PlaylistSyncDecisionKind.Delete, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Playlist_deleted_locally_since_a_prior_baseline_is_deleted_not_adopted()
    {
        var id = Guid.NewGuid();
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var remote = new PlaylistSyncPlaylistDto(id, "Road Trip", baseline, new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist>(), new List<PlaylistSyncPlaylistDto> { remote }, _ => baseline);

        Assert.Equal(PlaylistSyncDecisionKind.Delete, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Playlist_only_on_remote_with_no_baseline_is_still_adopted_not_deleted()
    {
        // A genuinely new playlist (never agreed upon before) must not be
        // mistaken for a deletion just because one side lacks it.
        var id = Guid.NewGuid();
        var remote = new PlaylistSyncPlaylistDto(id, "Road Trip", DateTimeOffset.UtcNow, new List<PlaylistSyncTrackDto> { Dto("A") });

        var decisions = PlaylistSyncPlanner.Plan(new List<Playlist>(), new List<PlaylistSyncPlaylistDto> { remote }, NoBaseline);

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, Assert.Single(decisions).Kind);
    }

    [Fact]
    public void Plan_produces_one_decision_per_distinct_playlist_id_across_both_sides()
    {
        var sharedId   = Guid.NewGuid();
        var localOnly  = Guid.NewGuid();
        var remoteOnly = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var local = new List<Playlist>
        {
            new Playlist(sharedId, "Shared", new List<Track> { T("A") }, now),
            new Playlist(localOnly, "Mine", new List<Track> { T("B") }, now),
        };
        var remote = new List<PlaylistSyncPlaylistDto>
        {
            new PlaylistSyncPlaylistDto(sharedId, "Shared", now, new List<PlaylistSyncTrackDto> { Dto("A") }),
            new PlaylistSyncPlaylistDto(remoteOnly, "Theirs", now, new List<PlaylistSyncTrackDto> { Dto("C") }),
        };

        var decisions = PlaylistSyncPlanner.Plan(local, remote, NoBaseline);

        Assert.Equal(3, decisions.Count);
        Assert.Equal(new[] { sharedId, localOnly, remoteOnly }.OrderBy(x => x),
            decisions.Select(d => d.PlaylistId).OrderBy(x => x));
    }
}
