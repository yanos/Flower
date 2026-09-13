using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class SmartPlaylistEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SmartPlaylistContext Context(
        Func<Guid, IReadOnlySet<Guid>?>? membership = null,
        DateTimeOffset? now = null) =>
        new(now ?? Now, membership, new Random(1));

    private static Track T(
        string title = "Song",
        string? artist = "Artist",
        string? album = "Album",
        string? genre = null,
        string? year = null,
        int plays = 0,
        TimeSpan? duration = null,
        DateTimeOffset? dateAdded = null,
        DateTimeOffset? lastPlayed = null,
        bool starred = false) =>
        new Track
        {
            Title = title,
            Artists = artist,
            Album = album,
            Genre = genre,
            Year = year,
            PlayCount = plays,
            Duration = duration ?? TimeSpan.FromMinutes(3),
            DateAdded = dateAdded ?? Now,
            LastPlayedAt = lastPlayed,
            Starred = starred,
        };

    private static List<Track> Evaluate(SmartPlaylistRules rules, IReadOnlyList<Track> tracks, SmartPlaylistContext? context = null) =>
        SmartPlaylistEvaluator.Evaluate(rules, tracks, context ?? Context());

    private static SmartCondition Is(SmartField field, string value) =>
        new(field, SmartOperator.Is, new SmartValue.Text(value));

    // --- Text -------------------------------------------------------------

    [Fact]
    public void Text_is_matches_the_whole_value_and_not_a_prefix_of_it()
    {
        var tracks = new List<Track> { T(genre: "Rock"), T(genre: "Rockabilly"), T(genre: "Jazz") };

        var matched = Evaluate(SmartPlaylistRules.MatchAll(Is(SmartField.Genre, "Rock")), tracks);

        Assert.Equal("Rock", Assert.Single(matched).Genre);
    }

    // The same folding the search box uses, for the same reason: the track is in
    // the library whether or not the keyboard can produce an umlaut.
    [Fact]
    public void Text_comparison_ignores_case_and_accents_the_way_search_does()
    {
        var tracks = new List<Track> { T(artist: "Björk"), T(artist: "Motörhead") };

        Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(Is(SmartField.Artists, "bjork")), tracks));
        Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Artists, SmartOperator.Contains, new SmartValue.Text("motorhead"))), tracks));
    }

    [Theory]
    [InlineData(SmartOperator.Contains,       "ock",  1)]
    [InlineData(SmartOperator.DoesNotContain, "ock",  2)]
    [InlineData(SmartOperator.StartsWith,     "ro",   1)]
    [InlineData(SmartOperator.StartsWith,     "ock",  0)]
    [InlineData(SmartOperator.EndsWith,       "ck",   1)]
    [InlineData(SmartOperator.EndsWith,       "ro",   0)]
    public void Text_operators_match_the_expected_number_of_tracks(SmartOperator op, string value, int expected)
    {
        var tracks = new List<Track> { T(genre: "Rock"), T(genre: "Jazz"), T(genre: null) };

        var matched = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.Genre, op, new SmartValue.Text(value))), tracks);

        Assert.Equal(expected, matched.Count);
    }

    // A track with no genre is not "genre is Rock", but it is "genre is not
    // Rock" - the answer someone would give out loud.
    [Fact]
    public void A_missing_value_fails_positive_operators_and_passes_negative_ones()
    {
        var tracks = new List<Track> { T(genre: null) };

        Assert.Empty(Evaluate(SmartPlaylistRules.MatchAll(Is(SmartField.Genre, "Rock")), tracks));
        Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Genre, SmartOperator.IsNot, new SmartValue.Text("Rock"))), tracks));
    }

    [Fact]
    public void Is_empty_finds_untagged_tracks_and_treats_whitespace_as_untagged()
    {
        var tracks = new List<Track> { T(genre: "Rock"), T(genre: null), T(genre: "   ") };

        var empty = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.Genre, SmartOperator.IsEmpty, new SmartValue.None())), tracks);
        var filled = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.Genre, SmartOperator.IsNotEmpty, new SmartValue.None())), tracks);

        Assert.Equal(2, empty.Count);
        Assert.Single(filled);
    }

    // --- Numbers and durations -------------------------------------------

    [Fact]
    public void Numeric_operators_compare_a_parsed_year_tag()
    {
        var tracks = new List<Track> { T(year: "1969"), T(year: "1979"), T(year: "1989"), T(year: "not a year") };

        var after = Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Year, SmartOperator.GreaterThan, new SmartValue.Number(1970))), tracks);

        Assert.Equal(["1979", "1989"], after.Select(t => t.Year));
    }

    [Fact]
    public void Between_is_inclusive_and_accepts_its_bounds_in_either_order()
    {
        var tracks = new List<Track> { T(year: "1969"), T(year: "1979"), T(year: "1989") };
        var backwards = new SmartCondition(SmartField.Year, SmartOperator.Between,
            new SmartValue.Range(new SmartValue.Number(1989), new SmartValue.Number(1969)));

        var matched = Evaluate(SmartPlaylistRules.MatchAll(backwards), tracks);

        Assert.Equal(3, matched.Count);
    }

    [Fact]
    public void Durations_compare_as_time_not_as_a_bare_number()
    {
        var tracks = new List<Track>
        {
            T(title: "Short", duration: TimeSpan.FromMinutes(2)),
            T(title: "Epic",  duration: TimeSpan.FromMinutes(12)),
        };

        var matched = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(
            SmartField.Duration, SmartOperator.GreaterThan, new SmartValue.Duration(TimeSpan.FromMinutes(10)))), tracks);

        Assert.Equal("Epic", Assert.Single(matched).Title);
    }

    [Fact]
    public void Plays_uses_the_same_total_the_track_list_shows()
    {
        var imported = T(title: "Imported");
        imported.ImportedPlayCount = 40;

        var matched = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(
            SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(10))), [T(plays: 1), imported]);

        Assert.Equal("Imported", Assert.Single(matched).Title);
    }

    // --- Dates ------------------------------------------------------------

    [Fact]
    public void In_the_last_resolves_against_the_context_clock()
    {
        var tracks = new List<Track>
        {
            T(title: "Fresh", dateAdded: Now.AddDays(-3)),
            T(title: "Old",   dateAdded: Now.AddDays(-90)),
        };
        var rules = SmartPlaylistRules.MatchAll(new SmartCondition(
            SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)));

        Assert.Equal("Fresh", Assert.Single(Evaluate(rules, tracks)).Title);
    }

    // The bug that storing a resolved instant would produce: the same rules,
    // read a year later, must answer differently. This is the whole reason
    // SmartValue.Relative exists.
    [Fact]
    public void The_same_relative_rule_answers_differently_as_time_passes()
    {
        var tracks = new List<Track> { T(title: "Fresh", dateAdded: Now.AddDays(-3)) };
        var rules = SmartPlaylistRules.MatchAll(new SmartCondition(
            SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)));

        Assert.Single(Evaluate(rules, tracks, Context(now: Now)));
        Assert.Empty(Evaluate(rules, tracks, Context(now: Now.AddYears(1))));
    }

    [Fact]
    public void Not_in_the_last_includes_tracks_that_were_never_played()
    {
        var tracks = new List<Track>
        {
            T(title: "Never"),
            T(title: "Yesterday", lastPlayed: Now.AddDays(-1)),
            T(title: "Ages ago",  lastPlayed: Now.AddDays(-400)),
        };

        var matched = Evaluate(SmartPlaylistRules.MatchAll(new SmartCondition(
            SmartField.LastPlayedAt, SmartOperator.NotInTheLast, new SmartValue.Relative(6, RelativeUnit.Months))), tracks);

        Assert.Equal(["Never", "Ages ago"], matched.Select(t => t.Title));
    }

    // Calendar arithmetic, not a 30-day approximation.
    [Fact]
    public void Months_are_calendar_months()
    {
        var march31 = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
            SmartPlaylistEvaluator.Resolve(new SmartValue.Relative(1, RelativeUnit.Months), march31));
    }

    [Fact]
    public void A_date_is_matches_the_whole_day_not_an_instant()
    {
        var tracks = new List<Track> { T(dateAdded: new DateTimeOffset(2026, 3, 3, 17, 42, 0, TimeSpan.Zero)) };
        var midnight = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);

        Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.DateAdded, SmartOperator.Is, new SmartValue.Date(midnight))), tracks));
    }

    [Fact]
    public void Never_played_is_findable_as_an_empty_date()
    {
        var tracks = new List<Track> { T(title: "Never"), T(title: "Played", lastPlayed: Now) };

        var matched = Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.LastPlayedAt, SmartOperator.IsEmpty, new SmartValue.None())), tracks);

        Assert.Equal("Never", Assert.Single(matched).Title);
    }

    // --- Booleans and membership -----------------------------------------

    [Fact]
    public void Booleans_match_both_ways_round()
    {
        var tracks = new List<Track> { T(title: "Starred", starred: true), T(title: "Not") };

        Assert.Equal("Starred", Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Starred, SmartOperator.Is, new SmartValue.Bool(true))), tracks)).Title);
        Assert.Equal("Not", Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Starred, SmartOperator.IsNot, new SmartValue.Bool(true))), tracks)).Title);
    }

    [Fact]
    public void Membership_resolves_a_referenced_playlist_through_the_context()
    {
        var inside = T(title: "In");
        var outside = T(title: "Out");
        var other = Guid.NewGuid();
        var context = Context(id => id == other ? new HashSet<Guid> { inside.Id } : null);

        var matched = Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.Is, new SmartValue.PlaylistRef(other))), [inside, outside], context);

        Assert.Equal("In", Assert.Single(matched).Title);
    }

    // The half-synced peer: a rule naming a playlist this device has never seen
    // must not fail the whole playlist, and "is not in it" is then true of
    // everything.
    [Fact]
    public void A_reference_to_a_playlist_this_device_lacks_resolves_to_empty()
    {
        var tracks = new List<Track> { T(title: "A"), T(title: "B") };
        var missing = new SmartValue.PlaylistRef(Guid.NewGuid());

        Assert.Empty(Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.Is, missing)), tracks));
        Assert.Equal(2, Evaluate(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, missing)), tracks).Count);
    }

    // --- Match modes ------------------------------------------------------

    [Fact]
    public void All_narrows_and_any_widens()
    {
        var tracks = new List<Track> { T(genre: "Rock", year: "1979"), T(genre: "Jazz", year: "1979"), T(genre: "Rock", year: "2001") };
        SmartCondition rock = Is(SmartField.Genre, "Rock");
        var seventies = new SmartCondition(SmartField.Year, SmartOperator.LessThan, new SmartValue.Number(1980));

        Assert.Single(Evaluate(SmartPlaylistRules.MatchAll(rock, seventies), tracks));
        Assert.Equal(3, Evaluate(SmartPlaylistRules.MatchAny(rock, seventies), tracks).Count);
    }

    // A smart playlist with no rules yet is the whole library, not an empty
    // list - the state it is in between "New Smart Playlist" and the first rule.
    [Fact]
    public void No_conditions_means_the_whole_library_in_either_mode()
    {
        var tracks = new List<Track> { T(), T() };

        Assert.Equal(2, Evaluate(SmartPlaylistRules.MatchAll(), tracks).Count);
        Assert.Equal(2, Evaluate(SmartPlaylistRules.MatchAny(), tracks).Count);
    }

    [Fact]
    public void An_unlimited_playlist_keeps_the_library_order_it_was_given()
    {
        var tracks = new List<Track> { T(title: "C"), T(title: "A"), T(title: "B") };

        Assert.Equal(["C", "A", "B"], Evaluate(SmartPlaylistRules.MatchAll(), tracks).Select(t => t.Title));
    }

    // --- Limits -----------------------------------------------------------

    [Fact]
    public void A_limit_selects_by_its_selector_and_returns_that_order()
    {
        var tracks = new List<Track>
        {
            T(title: "Hot",   plays: 50),
            T(title: "Warm",  plays: 20),
            T(title: "Cold",  plays: 1),
        };
        var rules = SmartPlaylistRules.MatchAll() with { Limit = new SmartLimit(2, LimitUnit.Items, LimitSelector.MostPlayed) };

        Assert.Equal(["Hot", "Warm"], Evaluate(rules, tracks).Select(t => t.Title));
    }

    // Neglected songs are exactly the ones a "least recently played" list is
    // for, so never-played must sort first rather than being treated as unknown.
    [Fact]
    public void Least_recently_played_puts_never_played_tracks_first()
    {
        var tracks = new List<Track>
        {
            T(title: "Recent", lastPlayed: Now.AddDays(-1)),
            T(title: "Never"),
            T(title: "Stale",  lastPlayed: Now.AddYears(-2)),
        };
        var rules = SmartPlaylistRules.MatchAll() with { Limit = new SmartLimit(3, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed) };

        Assert.Equal(["Never", "Stale", "Recent"], Evaluate(rules, tracks).Select(t => t.Title));
    }

    [Fact]
    public void A_time_limit_stops_before_overshooting_its_budget()
    {
        var tracks = new List<Track>
        {
            T(title: "Ten",   plays: 3, duration: TimeSpan.FromMinutes(10)),
            T(title: "Eight", plays: 2, duration: TimeSpan.FromMinutes(8)),
            T(title: "Two",   plays: 1, duration: TimeSpan.FromMinutes(2)),
        };
        var rules = SmartPlaylistRules.MatchAll() with { Limit = new SmartLimit(13, LimitUnit.Minutes, LimitSelector.MostPlayed) };

        // Ten fits, Eight would overshoot and is skipped, Two still fits.
        Assert.Equal(["Ten", "Two"], Evaluate(rules, tracks).Select(t => t.Title));
    }

    [Fact]
    public void A_random_limit_is_reproducible_for_a_given_seed()
    {
        var tracks = Enumerable.Range(0, 20).Select(i => T(title: $"Track {i}")).ToList();
        var rules = SmartPlaylistRules.MatchAll() with { Limit = new SmartLimit(5, LimitUnit.Items, LimitSelector.Random) };

        var first = Evaluate(rules, tracks, new SmartPlaylistContext(Now, random: new Random(7))).Select(t => t.Title);
        var second = Evaluate(rules, tracks, new SmartPlaylistContext(Now, random: new Random(7))).Select(t => t.Title);

        Assert.Equal(first, second);
    }

    // --- Malformed rules --------------------------------------------------

    [Fact]
    public void A_condition_whose_value_does_not_fit_its_field_is_rejected_loudly()
    {
        var rules = SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.Year, SmartOperator.Is, new SmartValue.Text("1979")));

        Assert.Throws<SmartPlaylistRuleException>(() => Evaluate(rules, [T()]));
        Assert.Single(SmartPlaylistEvaluator.Validate(rules));
    }

    [Fact]
    public void A_field_that_does_not_support_an_operator_is_rejected_loudly()
    {
        var rules = SmartPlaylistRules.MatchAll(new SmartCondition(SmartField.PlayCount, SmartOperator.Contains, new SmartValue.Text("1")));

        Assert.Throws<SmartPlaylistRuleException>(() => Evaluate(rules, [T()]));
        Assert.Single(SmartPlaylistEvaluator.Validate(rules));
    }

    // Rules can arrive from a peer running a newer Flower that knows fields
    // this one does not.
    [Fact]
    public void An_unknown_field_from_a_newer_peer_is_reported_rather_than_thrown_by_validation()
    {
        var rules = SmartPlaylistRules.MatchAll(new SmartCondition((SmartField)9999, SmartOperator.Is, new SmartValue.Text("x")));

        Assert.Single(SmartPlaylistEvaluator.Validate(rules));
    }

    [Fact]
    public void Validation_passes_a_well_formed_rule_set()
    {
        var rules = SmartPlaylistRules.MatchAll(
            Is(SmartField.Genre, "Rock"),
            new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)),
            new SmartCondition(SmartField.Starred, SmartOperator.Is, new SmartValue.Bool(true)))
            with { Limit = new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed) };

        Assert.Empty(SmartPlaylistEvaluator.Validate(rules));
    }

    [Fact]
    public void A_limit_of_zero_is_reported_as_a_permanently_empty_playlist()
    {
        var rules = SmartPlaylistRules.MatchAll() with { Limit = new SmartLimit(0, LimitUnit.Items, LimitSelector.Random) };

        Assert.Single(SmartPlaylistEvaluator.Validate(rules));
    }
}
