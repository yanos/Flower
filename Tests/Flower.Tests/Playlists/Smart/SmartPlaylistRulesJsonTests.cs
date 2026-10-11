using System;
using System.Collections.Generic;
using System.Text.Json;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Playlists.Smart;

// The rules blob is the whole of what a smart playlist is (Schema V6's
// playlists.rules), so anything that does not survive this round trip is a
// playlist that quietly means something else after a restart.
public class SmartPlaylistRulesJsonTests
{
    private static SmartPlaylistRules RoundTrip(SmartPlaylistRules rules) =>
        Assert.IsType<SmartPlaylistRules>(SmartPlaylistRulesJson.Read(SmartPlaylistRulesJson.Write(rules)));

    [Fact]
    public void Conditions_mode_and_the_limit_all_survive()
    {
        var rules = new SmartPlaylistRules(
            MatchMode.Any,
            [
                new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")),
                new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(3)),
            ],
            new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed),
            LiveUpdating: false);

        var reloaded = RoundTrip(rules);

        // Field by field rather than Assert.Equal on the record: the compiler's
        // generated equality compares Conditions by reference, so two lists of
        // equal conditions are never equal records.
        Assert.Equal(rules.Mode, reloaded.Mode);
        Assert.Equal(rules.Conditions, reloaded.Conditions);
        Assert.Equal(rules.Limit, reloaded.Limit);
        Assert.Equal(rules.LiveUpdating, reloaded.LiveUpdating);
    }

    [Fact]
    public void The_units_inside_a_value_and_a_limit_are_written_by_name_too()
    {
        var json = SmartPlaylistRulesJson.Write(new SmartPlaylistRules(
            MatchMode.Any,
            [new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(2, RelativeUnit.Years))],
            new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed)));

        Assert.Contains("\"Unit\":\"Years\"", json);
        Assert.Contains("\"Unit\":\"Items\"", json);
        Assert.Contains("\"SelectedBy\":\"LeastRecentlyPlayed\"", json);
    }

    // One case per SmartValue shape, because the discriminator is what the
    // stored blob is written in terms of and a case with no test is a case
    // whose tag nothing pins.
    public static TheoryData<SmartValue> EveryValueShape => new()
    {
        new SmartValue.Text("Björk"),
        new SmartValue.Number(120.5),
        new SmartValue.Duration(TimeSpan.FromSeconds(210)),
        new SmartValue.Date(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero)),
        new SmartValue.Relative(30, RelativeUnit.Days),
        new SmartValue.Flag(true),
        new SmartValue.PlaylistRef(Guid.Parse("11111111-2222-3333-4444-555555555555")),
        new SmartValue.Range(new SmartValue.Number(1970), new SmartValue.Number(1979)),
        SmartValue.None.Instance,
    };

    [Theory]
    [MemberData(nameof(EveryValueShape))]
    public void Every_value_shape_round_trips(SmartValue value)
    {
        var rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Title, SmartOperator.Is, value));

        Assert.Equal(value, Assert.Single(RoundTrip(rules).Conditions).Value);
    }

    // Relative is the whole reason SmartValue has a non-literal case: storing
    // the instant "30 days ago" resolves to would freeze the window, and the
    // playlist would go on matching the same four weeks forever. It has to
    // still be an offset after a round trip, not a date.
    [Fact]
    public void A_relative_date_stays_relative_rather_than_collapsing_to_an_instant()
    {
        var rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)));

        var value = Assert.IsType<SmartValue.Relative>(Assert.Single(RoundTrip(rules).Conditions).Value);
        Assert.Equal(30, value.Amount);
        Assert.Equal(RelativeUnit.Days, value.Unit);
    }

    // The blob is what a human reads when a playlist misbehaves on a device
    // they cannot attach a debugger to, so it has to say what it means.
    // Asserted on the JSON itself rather than only through a round trip, which
    // passes just as happily with the small integers this used to write.
    [Fact]
    public void Fields_and_operators_are_stored_by_name()
    {
        var json = SmartPlaylistRulesJson.Write(SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.Playlist, SmartOperator.IsNot, new SmartValue.PlaylistRef(Guid.Empty))));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(nameof(MatchMode.All), document.RootElement.GetProperty("Mode").GetString());

        var condition = document.RootElement.GetProperty("Conditions")[0];
        Assert.Equal(nameof(SmartField.Playlist), condition.GetProperty("Field").GetString());
        Assert.Equal(nameof(SmartOperator.IsNot), condition.GetProperty("Operator").GetString());
        Assert.Equal("playlist", condition.GetProperty("Value").GetProperty("kind").GetString());
    }

    // A blob can arrive from a peer, a newer build, or a hand-edited database.
    // None of those are worth failing a playlist load over - the playlist
    // degrades to an ordinary one holding whatever was last materialized.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{"Mode":0,"Conditions":[{"Field":1,"Operator":1,"Value":{"kind":"from-the-future"}}]}""")]
    public void An_unreadable_blob_reads_as_no_rules_at_all(string? json)
    {
        Assert.Null(SmartPlaylistRulesJson.Read(json));
    }
}
