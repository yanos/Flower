using System;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class SmartPlaylistFieldsTests
{
    [Fact]
    public void Every_declared_field_has_exactly_one_descriptor()
    {
        var declared = Enum.GetValues<SmartField>();

        Assert.Equal(declared.Length, SmartPlaylistFields.All.Length);
        Assert.Equal(declared.Length, SmartPlaylistFields.All.Select(d => d.Field).Distinct().Count());
        foreach (var field in declared)
            Assert.NotNull(SmartPlaylistFields.For(field));
    }

    // The registry's whole point is that nobody else gets to hold their own
    // idea of how to read a field, so a descriptor without a reader would be a
    // field the evaluator has to special-case. Playlist membership is the one
    // deliberate exception - it is not a property of a track at all.
    [Fact]
    public void Every_field_but_playlist_membership_can_read_a_track()
    {
        foreach (var descriptor in SmartPlaylistFields.All)
        {
            var hasAccessor = descriptor.Text is not null
                           || descriptor.Number is not null
                           || descriptor.Duration is not null
                           || descriptor.Date is not null
                           || descriptor.Bool is not null;

            Assert.Equal(descriptor.Field != SmartField.Playlist, hasAccessor);
        }
    }

    [Fact]
    public void An_accessor_matches_the_kind_it_is_declared_with()
    {
        foreach (var d in SmartPlaylistFields.All)
        {
            var declared = d.Kind switch
            {
                SmartValueKind.Text     => d.Text is not null,
                SmartValueKind.Number   => d.Number is not null,
                SmartValueKind.Duration => d.Duration is not null,
                SmartValueKind.Date     => d.Date is not null,
                SmartValueKind.Bool     => d.Bool is not null,
                SmartValueKind.Playlist => true,
                _ => false,
            };

            Assert.True(declared, $"{d.Field} is declared {d.Kind} but has no accessor of that kind.");
        }
    }

    [Fact]
    public void Every_field_offers_at_least_one_operator()
    {
        foreach (var field in Enum.GetValues<SmartField>())
            Assert.NotEmpty(SmartPlaylistFields.OperatorsFor(field));
    }

    [Fact]
    public void Text_fields_offer_contains_and_numeric_fields_do_not()
    {
        Assert.True(SmartPlaylistFields.Supports(SmartField.Genre, SmartOperator.Contains));
        Assert.False(SmartPlaylistFields.Supports(SmartField.PlayCount, SmartOperator.Contains));
    }

    // Only dates can ask for "in the last N days"; offering it on a number
    // would be a rule the evaluator has no way to answer.
    [Fact]
    public void Only_date_fields_offer_relative_windows()
    {
        Assert.True(SmartPlaylistFields.Supports(SmartField.DateAdded, SmartOperator.InTheLast));
        Assert.False(SmartPlaylistFields.Supports(SmartField.Year, SmartOperator.InTheLast));
        Assert.False(SmartPlaylistFields.Supports(SmartField.Duration, SmartOperator.InTheLast));
    }

    [Fact]
    public void An_unknown_field_throws_rather_than_silently_reading_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartPlaylistFields.For((SmartField)9999));
    }

    [Fact]
    public void Year_reads_a_four_digit_tag_and_ignores_one_that_is_not_a_number()
    {
        var year = SmartPlaylistFields.For(SmartField.Year).Number!;

        Assert.Equal(1979, year(new Track { Year = "1979" }));
        Assert.Null(year(new Track { Year = "sometime in the eighties" }));
        Assert.Null(year(new Track { Year = null }));
    }

    // "Plays" in a rule has to mean the same number the track list shows, which
    // includes plays imported from another library and plays a paired device
    // reported.
    [Fact]
    public void Plays_counts_local_imported_and_remote_plays_together()
    {
        var track = new Track { PlayCount = 2, ImportedPlayCount = 3 };
        track.RemotePlayCounts["some-other-device"] = 5;

        Assert.Equal(10, SmartPlaylistFields.For(SmartField.PlayCount).Number!(track));
    }
}
