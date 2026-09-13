using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Phase 5 of docs/SMART-PLAYLIST-PLAN.md: rules crossing the wire, and the
// merge rule that follows from them - a smart playlist is decided on its query,
// never on the tracks that query happened to produce on each device.
public class SmartPlaylistSyncTests
{
    private static Track T(string title, int durationSeconds = 200) =>
        new Track
        {
            Title = title,
            Artists = "Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Path = $"/music/{title}.mp3",
        };

    private static PlaylistSyncTrackDto Dto(string title, int durationSeconds = 200) =>
        new(title, "Artist", "Album", durationSeconds);

    private static SmartPlaylistRules Played =>
        SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0)));

    private static readonly Func<Guid, DateTimeOffset?> NoBaseline = _ => null;

    // ---- SmartPlaylistRules.Equivalent -------------------------------------

    // The reason the method exists at all: == on the record says no, because
    // Conditions is an IReadOnlyList and records compare one by reference.
    [Fact]
    public void The_same_rules_in_two_different_list_types_are_equivalent_but_not_equal()
    {
        var fromArray = Played;
        var fromList = new SmartPlaylistRules(
            MatchMode.All,
            new List<SmartCondition>
            {
                new(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0)),
            });

        Assert.NotEqual(fromArray, fromList);
        Assert.True(SmartPlaylistRules.Equivalent(fromArray, fromList));
    }

    [Fact]
    public void Two_nulls_are_equivalent_and_a_null_never_matches_rules()
    {
        Assert.True(SmartPlaylistRules.Equivalent(null, null));
        Assert.False(SmartPlaylistRules.Equivalent(Played, null));
        Assert.False(SmartPlaylistRules.Equivalent(null, Played));
    }

    [Fact]
    public void Every_part_of_a_rule_set_is_compared()
    {
        var baseline = Played with { Limit = new SmartLimit(25, LimitUnit.Items, LimitSelector.Random) };

        Assert.True(SmartPlaylistRules.Equivalent(baseline, baseline with { }));
        Assert.False(SmartPlaylistRules.Equivalent(baseline, baseline with { Mode = MatchMode.Any }));
        Assert.False(SmartPlaylistRules.Equivalent(baseline, baseline with { LiveUpdating = false }));
        Assert.False(SmartPlaylistRules.Equivalent(baseline, baseline with
        {
            Limit = new SmartLimit(25, LimitUnit.Items, LimitSelector.MostPlayed),
        }));
        Assert.False(SmartPlaylistRules.Equivalent(baseline, baseline with
        {
            Conditions = [new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(1))],
        }));
    }

    // A nested value has to be compared by what it says too - Range holds two
    // SmartValues of its own, and it is the one case that recurses.
    [Fact]
    public void A_range_condition_is_compared_by_value()
    {
        SmartPlaylistRules Between(int to) => SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Year, SmartOperator.Between,
                new SmartValue.Range(new SmartValue.Number(1970), new SmartValue.Number(to))));

        Assert.True(SmartPlaylistRules.Equivalent(Between(1979), Between(1979)));
        Assert.False(SmartPlaylistRules.Equivalent(Between(1979), Between(1989)));
    }

    // ---- The wire ----------------------------------------------------------

    [Fact]
    public void The_rules_travel_with_the_playlist_and_come_back()
    {
        var playlist = new Playlist("Played", [T("A")]) { Rules = Played };

        var dto = PlaylistSyncMapper.ToDto(playlist);
        Assert.True(SmartPlaylistRules.Equivalent(Played, dto.Rules));

        var received = PlaylistSyncMapper.ToPlaylist(dto, [T("A")]);
        Assert.True(received.IsSmart);
        Assert.True(SmartPlaylistRules.Equivalent(Played, received.Rules));
        Assert.Equal(playlist.Id, received.Id);
        // The peer's materialized contents come too, so the playlist is not
        // empty in the window before this device evaluates it for itself - and
        // permanently, if its rules say not to live-update.
        Assert.Equal(["A"], received.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void An_ordinary_playlist_carries_no_rules_and_arrives_ordinary()
    {
        var dto = PlaylistSyncMapper.ToDto(new Playlist("Hand picked", [T("A")]));

        Assert.Null(dto.Rules);
        Assert.False(PlaylistSyncMapper.ToPlaylist(dto, [T("A")]).IsSmart);
    }

    // The manifest is serialized reflection-based on the server (SyncEndpoints)
    // and source-generated on the client, and SmartValue is a polymorphic
    // hierarchy - the one part of this DTO that can serialize cleanly and come
    // back as something else.
    [Fact]
    public void A_manifest_survives_a_JSON_round_trip_with_every_value_shape_intact()
    {
        var rules = new SmartPlaylistRules(
            MatchMode.Any,
            [
                new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")),
                new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)),
                new SmartCondition(SmartField.Duration, SmartOperator.LessThan, new SmartValue.Duration(TimeSpan.FromMinutes(3))),
                new SmartCondition(SmartField.Starred, SmartOperator.Is, new SmartValue.Bool(true)),
                new SmartCondition(SmartField.Comment, SmartOperator.IsEmpty, SmartValue.None.Instance),
                new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(Guid.NewGuid())),
                new SmartCondition(SmartField.Year, SmartOperator.Between,
                    new SmartValue.Range(new SmartValue.Number(1970), new SmartValue.Number(1979))),
            ],
            new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed),
            LiveUpdating: false);

        var manifest = PlaylistSyncMapper.ToManifest(
            "fingerprint", [new Playlist("Everything", [T("A")]) { Rules = rules }]);

        var back = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(JsonSerializer.Serialize(manifest))!;

        Assert.True(SmartPlaylistRules.Equivalent(rules, Assert.Single(back.Playlists).Rules));
    }

    // ---- The merge ---------------------------------------------------------

    private static Playlist Local(Guid id, string name, SmartPlaylistRules? rules, DateTimeOffset updatedAt, params Track[] tracks) =>
        new(id, name, tracks.ToList(), updatedAt, rules: rules);

    private static PlaylistSyncPlaylistDto Remote(Guid id, string name, SmartPlaylistRules? rules, DateTimeOffset updatedAt, params PlaylistSyncTrackDto[] tracks) =>
        new(id, name, updatedAt, tracks.ToList(), rules);

    private static PlaylistSyncDecision PlanOne(Playlist local, PlaylistSyncPlaylistDto remote, Func<Guid, DateTimeOffset?>? baseline = null) =>
        Assert.Single(PlaylistSyncPlanner.Plan([local], [remote], baseline ?? NoBaseline));

    // The whole point of shipping rules rather than members: two devices
    // holding different music materialize the same query differently, and that
    // is not a disagreement.
    [Fact]
    public void The_same_rules_over_different_libraries_are_NoChange_despite_different_tracks()
    {
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;

        var decision = PlanOne(
            Local(id, "Played", Played, updatedAt, T("A"), T("B")),
            Remote(id, "Played", Played, updatedAt, Dto("A")));

        Assert.Equal(PlaylistSyncDecisionKind.NoChange, decision.Kind);
    }

    // The trap this branch exists for. Without it the contents differ, neither
    // side has moved UpdatedAt (materialization deliberately does not), and the
    // (false, false) arm of the ordinary merge is Conflict - so a smart
    // playlist would prompt on every single sync, forever, over a difference
    // that is correct.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Conflict_is_unreachable_for_a_playlist_that_is_smart_on_both_sides(bool withBaseline)
    {
        var id = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddHours(-2);
        var baseline = withBaseline ? older.AddHours(-1) : (DateTimeOffset?)null;

        // Both sides edited the rules since the baseline - the shape that is a
        // real conflict for an ordinary playlist.
        var mine = Played with { Limit = new SmartLimit(10, LimitUnit.Items, LimitSelector.MostPlayed) };
        var theirs = Played with { Limit = new SmartLimit(50, LimitUnit.Items, LimitSelector.Random) };

        var decision = PlanOne(
            Local(id, "Played", mine, older.AddMinutes(30), T("A")),
            Remote(id, "Played", theirs, older.AddMinutes(10), Dto("B")),
            _ => baseline);

        Assert.NotEqual(PlaylistSyncDecisionKind.Conflict, decision.Kind);
        Assert.Equal(PlaylistSyncDecisionKind.KeepLocal, decision.Kind);
    }

    [Fact]
    public void The_more_recent_rule_edit_wins()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var edited = Played with { Mode = MatchMode.Any };

        Assert.Equal(
            PlaylistSyncDecisionKind.AdoptRemote,
            PlanOne(Local(id, "Played", Played, now.AddMinutes(-5)), Remote(id, "Played", edited, now)).Kind);

        Assert.Equal(
            PlaylistSyncDecisionKind.KeepLocal,
            PlanOne(Local(id, "Played", edited, now), Remote(id, "Played", Played, now.AddMinutes(-5))).Kind);
    }

    [Fact]
    public void Renaming_a_smart_playlist_still_propagates()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var decision = PlanOne(
            Local(id, "Played", Played, now.AddMinutes(-5)),
            Remote(id, "Heard before", Played, now));

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, decision.Kind);
    }

    // Someone converted it on one device (see
    // PlaylistManagementViewModel.ConvertToOrdinary). Newest wins outright,
    // change of kind included - and a manual playlist that wins brings its
    // track list with it, which is why the decision carries the whole side
    // rather than just the rules.
    [Fact]
    public void Smart_on_one_side_and_manual_on_the_other_is_settled_by_the_newer_edit()
    {
        var now = DateTimeOffset.UtcNow;

        var converted = Guid.NewGuid();
        var adopt = PlanOne(
            Local(converted, "Played", Played, now.AddMinutes(-5), T("A")),
            Remote(converted, "Played", null, now, Dto("A"), Dto("B")));
        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, adopt.Kind);
        Assert.Equal(2, adopt.Remote!.Tracks.Count);

        var madeSmart = Guid.NewGuid();
        var keep = PlanOne(
            Local(madeSmart, "Played", null, now, T("A"), T("B")),
            Remote(madeSmart, "Played", Played, now.AddMinutes(-5), Dto("A")));
        Assert.Equal(PlaylistSyncDecisionKind.KeepLocal, keep.Kind);
        Assert.Equal(2, keep.Local!.Tracks.Count);
    }

    // The case a real device hit: a peer running a build from before rules
    // travelled at all received this playlist, kept the tracks, dropped the
    // query, and pushed nothing back - so the two sides now hold the same
    // playlist, at the same UpdatedAt, with rules on one side only.
    //
    // Losing rules is never a user edit (the only way out of being smart is an
    // edit, which moves UpdatedAt), so the side that still has them is simply
    // the unlossy copy of the same version, whichever end of the wire it is on.
    // Handing the tie to local instead would make the answer depend on which
    // device happened to be running the sync: the stripped copy would win on
    // the device holding it and then be pushed back over the good one, killing
    // the query everywhere rather than healing it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void At_the_same_UpdatedAt_the_side_that_still_has_rules_wins(bool localHasTheRules)
    {
        var id = Guid.NewGuid();
        var sameMoment = DateTimeOffset.UtcNow;

        var decision = PlanOne(
            Local(id, "Downloaded", localHasTheRules ? Played : null, sameMoment, T("A"), T("B")),
            Remote(id, "Downloaded", localHasTheRules ? null : Played, sameMoment, Dto("A"), Dto("B")));

        Assert.Equal(
            localHasTheRules ? PlaylistSyncDecisionKind.KeepLocal : PlaylistSyncDecisionKind.AdoptRemote,
            decision.Kind);
    }

    // The healing above must not become "rules come back from the dead": once
    // someone really does convert a smart playlist to a hand-picked one, that
    // edit moves UpdatedAt, and the newer side wins on time as usual.
    [Fact]
    public void A_deliberate_conversion_to_a_manual_playlist_still_wins_on_time()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var decision = PlanOne(
            Local(id, "Downloaded", Played, now.AddMinutes(-5), T("A")),
            Remote(id, "Downloaded", null, now, Dto("A"), Dto("B")));

        Assert.Equal(PlaylistSyncDecisionKind.AdoptRemote, decision.Kind);
    }

    // A membership rule pointing at a playlist this device does not have. The
    // rules still arrive intact - resolving the reference is the evaluator's
    // problem, and it answers "matches nothing" rather than failing.
    [Fact]
    public void A_membership_rule_naming_an_unknown_playlist_still_arrives()
    {
        var id = Guid.NewGuid();
        var rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.Is, new SmartValue.PlaylistRef(Guid.NewGuid())));

        var received = PlaylistSyncMapper.ToPlaylist(
            Remote(id, "From a playlist I do not have", rules, DateTimeOffset.UtcNow), [T("A")]);

        Assert.True(SmartPlaylistRules.Equivalent(rules, received.Rules));
        Assert.Empty(SmartPlaylistEvaluator.Evaluate(
            received.Rules!, [T("A")], new SmartPlaylistContext(DateTimeOffset.UtcNow, _ => null)));
    }
}
