using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class SmartPlaylistGraphTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SmartCondition In(Guid playlistId) =>
        new(SmartField.Playlist, SmartOperator.Is, new SmartValue.PlaylistRef(playlistId));

    private static SmartCondition NotIn(Guid playlistId) =>
        new(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(playlistId));

    private static SmartCondition Genre(string genre) =>
        new(SmartField.Genre, SmartOperator.Is, new SmartValue.Text(genre));

    private static Track T(string title, string? genre = null, int plays = 0) =>
        new() { Title = title, Genre = genre, PlayCount = plays, Duration = TimeSpan.FromMinutes(3) };

    [Fact]
    public void A_playlist_with_no_membership_rules_depends_on_nothing()
    {
        Assert.Empty(SmartPlaylistGraph.DependenciesOf(SmartPlaylistRules.MatchAll(Genre("Rock"))));
    }

    [Fact]
    public void A_dependency_is_evaluated_before_the_playlist_that_references_it()
    {
        var heard = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [fresh] = SmartPlaylistRules.MatchAll(Genre("Rock"), NotIn(heard)),
            [heard] = SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0))),
        };

        var order = SmartPlaylistGraph.EvaluationOrder(smart);

        Assert.Equal([heard, fresh], order);
    }

    [Fact]
    public void A_chain_is_ordered_all_the_way_down()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(NotIn(b)),
            [b] = SmartPlaylistRules.MatchAll(NotIn(c)),
            [c] = SmartPlaylistRules.MatchAll(Genre("Rock")),
        };

        Assert.Equal([c, b, a], SmartPlaylistGraph.EvaluationOrder(smart));
    }

    // An ordinary playlist holds its members outright, so it is always ready to
    // be asked about and never needs ordering.
    [Fact]
    public void A_reference_to_an_ordinary_playlist_adds_no_ordering_constraint()
    {
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [Guid.NewGuid()] = SmartPlaylistRules.MatchAll(NotIn(Guid.NewGuid())),
        };

        Assert.Single(SmartPlaylistGraph.EvaluationOrder(smart));
    }

    [Fact]
    public void Two_playlists_that_reference_each_other_are_refused()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(NotIn(b)),
            [b] = SmartPlaylistRules.MatchAll(NotIn(a)),
        };

        var thrown = Assert.Throws<SmartPlaylistCycleException>(() => SmartPlaylistGraph.EvaluationOrder(smart));

        Assert.Contains(a, thrown.Cycle);
        Assert.Contains(b, thrown.Cycle);
    }

    [Fact]
    public void A_playlist_referencing_itself_is_refused()
    {
        var a = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules> { [a] = SmartPlaylistRules.MatchAll(NotIn(a)) };

        var thrown = Assert.Throws<SmartPlaylistCycleException>(() => SmartPlaylistGraph.EvaluationOrder(smart));

        Assert.Equal([a, a], thrown.Cycle);
    }

    [Fact]
    public void A_longer_loop_is_refused_and_named()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(NotIn(b)),
            [b] = SmartPlaylistRules.MatchAll(NotIn(c)),
            [c] = SmartPlaylistRules.MatchAll(NotIn(a)),
        };

        var thrown = Assert.Throws<SmartPlaylistCycleException>(() => SmartPlaylistGraph.EvaluationOrder(smart));

        Assert.Equal([a, b, c, a], thrown.Cycle);
    }

    // Two playlists both referencing a third is a diamond, not a loop - the
    // shape a naive "have I seen this before?" check would reject.
    [Fact]
    public void A_shared_dependency_is_not_mistaken_for_a_loop()
    {
        var shared = Guid.NewGuid();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var top = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [top]    = SmartPlaylistRules.MatchAll(In(left), In(right)),
            [left]   = SmartPlaylistRules.MatchAll(In(shared)),
            [right]  = SmartPlaylistRules.MatchAll(In(shared)),
            [shared] = SmartPlaylistRules.MatchAll(Genre("Rock")),
        };

        var order = SmartPlaylistGraph.EvaluationOrder(smart).ToList();

        Assert.Equal(4, order.Count);
        Assert.True(order.IndexOf(shared) < order.IndexOf(left));
        Assert.True(order.IndexOf(shared) < order.IndexOf(right));
        Assert.True(order.IndexOf(left) < order.IndexOf(top));
        Assert.True(order.IndexOf(right) < order.IndexOf(top));
    }

    // --- What the editor is allowed to offer ------------------------------

    [Fact]
    public void The_editor_never_offers_the_playlist_being_edited()
    {
        var a = Guid.NewGuid();
        var other = Guid.NewGuid();

        var candidates = SmartPlaylistGraph.ReferenceCandidates(a, [a, other], new Dictionary<Guid, SmartPlaylistRules>());

        Assert.Equal([other], candidates);
    }

    [Fact]
    public void The_editor_never_offers_a_playlist_that_already_depends_on_this_one()
    {
        var a = Guid.NewGuid();
        var dependsOnA = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a]          = SmartPlaylistRules.MatchAll(Genre("Rock")),
            [dependsOnA] = SmartPlaylistRules.MatchAll(NotIn(a)),
            [unrelated]  = SmartPlaylistRules.MatchAll(Genre("Jazz")),
        };

        var candidates = SmartPlaylistGraph.ReferenceCandidates(a, [a, dependsOnA, unrelated], smart);

        Assert.Equal([unrelated], candidates);
    }

    // The transitive case, which is the one a per-playlist check would miss:
    // C depends on B depends on A, so A must not be offered C either.
    [Fact]
    public void The_editor_never_offers_a_playlist_that_depends_on_this_one_through_a_chain()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(Genre("Rock")),
            [b] = SmartPlaylistRules.MatchAll(NotIn(a)),
            [c] = SmartPlaylistRules.MatchAll(NotIn(b)),
        };

        Assert.Empty(SmartPlaylistGraph.ReferenceCandidates(a, [a, b, c], smart));
    }

    [Fact]
    public void Rules_that_would_close_a_loop_are_reported_before_they_are_saved()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(Genre("Rock")),
            [b] = SmartPlaylistRules.MatchAll(NotIn(a)),
        };

        Assert.True(SmartPlaylistGraph.WouldCycle(a, SmartPlaylistRules.MatchAll(NotIn(b)), smart));
        Assert.False(SmartPlaylistGraph.WouldCycle(a, SmartPlaylistRules.MatchAll(Genre("Jazz")), smart));
    }

    // --- Evaluating the whole graph ---------------------------------------

    [Fact]
    public void Evaluate_all_feeds_each_playlist_the_fresh_contents_of_the_one_it_references()
    {
        var heardId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var library = new List<Track> { T("Played", "Rock", plays: 3), T("Unplayed", "Rock"), T("Jazz thing", "Jazz") };
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [freshId] = SmartPlaylistRules.MatchAll(Genre("Rock"), NotIn(heardId)),
            [heardId] = SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0))),
        };

        var results = SmartPlaylistEvaluator.EvaluateAll(smart, library, null, Now);

        Assert.Equal(["Played"], results[heardId].Select(t => t.Title));
        Assert.Equal(["Unplayed"], results[freshId].Select(t => t.Title));
    }

    // Order is not incidental here: evaluating Fresh Rock first would have
    // matched against an empty Already Heard and let the played track in.
    [Fact]
    public void Evaluate_all_is_unaffected_by_the_order_the_playlists_are_stored_in()
    {
        var heardId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var library = new List<Track> { T("Played", "Rock", plays: 3), T("Unplayed", "Rock") };
        var heard = SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0)));
        var fresh = SmartPlaylistRules.MatchAll(Genre("Rock"), NotIn(heardId));

        var freshFirst = SmartPlaylistEvaluator.EvaluateAll(
            new Dictionary<Guid, SmartPlaylistRules> { [freshId] = fresh, [heardId] = heard }, library, null, Now);
        var heardFirst = SmartPlaylistEvaluator.EvaluateAll(
            new Dictionary<Guid, SmartPlaylistRules> { [heardId] = heard, [freshId] = fresh }, library, null, Now);

        Assert.Equal(freshFirst[freshId].Select(t => t.Title), heardFirst[freshId].Select(t => t.Title));
        Assert.Equal(["Unplayed"], freshFirst[freshId].Select(t => t.Title));
    }

    [Fact]
    public void Evaluate_all_resolves_ordinary_playlists_through_the_callback()
    {
        var ordinary = Guid.NewGuid();
        var smartId = Guid.NewGuid();
        var picked = T("Picked", "Rock");
        var library = new List<Track> { picked, T("Ignored", "Rock") };
        var smart = new Dictionary<Guid, SmartPlaylistRules> { [smartId] = SmartPlaylistRules.MatchAll(In(ordinary)) };

        var results = SmartPlaylistEvaluator.EvaluateAll(
            smart, library, id => id == ordinary ? new HashSet<Guid> { picked.Id } : null, Now);

        Assert.Equal(["Picked"], results[smartId].Select(t => t.Title));
    }

    [Fact]
    public void Evaluate_all_refuses_a_library_whose_rules_form_a_loop()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var smart = new Dictionary<Guid, SmartPlaylistRules>
        {
            [a] = SmartPlaylistRules.MatchAll(NotIn(b)),
            [b] = SmartPlaylistRules.MatchAll(NotIn(a)),
        };

        Assert.Throws<SmartPlaylistCycleException>(() => SmartPlaylistEvaluator.EvaluateAll(smart, [T("x")], null, Now));
    }
}
