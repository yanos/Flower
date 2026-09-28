using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Two edits of one playlist made from the same version - here while a sync
// session ran, and on the other side before it - combined, or refused when
// both cannot be kept (PlaylistThreeWayMerge).
public class PlaylistThreeWayMergeTests
{
    private static readonly Dictionary<string, Track> Songs =
        "ABCDEFG".ToDictionary(c => c.ToString(), c => new Track { Title = c.ToString() });

    private static readonly DateTimeOffset Then = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.NewGuid();

    private static List<Track> Tracks(string titles) => titles.Select(c => Songs[c.ToString()]).ToList();

    private static Playlist Version(string titles, string name = "Mix", int minutesLater = 0) =>
        new(Id, name, Tracks(titles), Then.AddMinutes(minutesLater));

    private static string? Merge(string before, string ours, string theirs) =>
        PlaylistThreeWayMerge.Merge(PlannedPlaylist.Of(Version(before)), Version(ours, minutesLater: 1), Version(theirs, minutesLater: 2)) is { } merged
            ? string.Concat(merged.Tracks.Select(t => t.Title))
            : null;

    [Theory]
    // One side changed, the other did not.
    [InlineData("ABC", "ABC", "ABCD", "ABCD")]
    [InlineData("ABC", "ABCD", "ABC", "ABCD")]
    // Both added a song.
    [InlineData("ABC", "ABCD", "ABCE", "ABCED")]
    // One added, one removed.
    [InlineData("ABC", "ABCD", "AC", "ACD")]
    // Both removed different songs; both removed the same one.
    [InlineData("ABCD", "BCD", "ABC", "BC")]
    [InlineData("ABCD", "ACD", "ACD", "ACD")]
    // An added song lands after the song it followed on its own side.
    [InlineData("ABC", "ADBC", "ABCE", "ADBCE")]
    // One side reordered, the other added: the addition goes into the new order.
    [InlineData("ABC", "CBA", "ABCD", "CBAD")]
    [InlineData("ABC", "ABCD", "CAB", "CABD")]
    // Both reordered the same way.
    [InlineData("ABC", "CBA", "CBA", "CBA")]
    // Both added the same song: once.
    [InlineData("ABC", "ABCD", "ABCD", "ABCD")]
    public void Edits_that_do_not_collide_are_combined(string before, string ours, string theirs, string expected)
    {
        Assert.Equal(expected, Merge(before, ours, theirs));
    }

    [Fact]
    public void Both_sides_reordering_differently_cannot_be_combined()
    {
        Assert.Null(Merge("ABC", "CBA", "BAC"));
    }

    // A song in the playlist twice is two entries: taking the second one out
    // here, while a song is added there, takes out that one and not both.
    [Fact]
    public void A_song_in_twice_is_two_entries()
    {
        Assert.Equal("ABCD", Merge("ABAC", "ABC", "ABACD"));
    }

    [Fact]
    public void A_rename_on_one_side_is_kept_and_two_different_renames_are_not_combined()
    {
        var before = PlannedPlaylist.Of(Version("AB"));

        var renamedHere = PlaylistThreeWayMerge.Merge(before, Version("AB", "Road trip", 1), Version("ABC", minutesLater: 2));
        Assert.Equal("Road trip", renamedHere!.Name);
        Assert.Equal("ABC", string.Concat(renamedHere.Tracks.Select(t => t.Title)));

        Assert.Null(PlaylistThreeWayMerge.Merge(before, Version("AB", "Road trip", 1), Version("AB", "Drive", 2)));
    }

    // The combined copy has to win the push: newer than both edits.
    [Fact]
    public void The_combined_copy_is_newer_than_both_edits()
    {
        var ours = Version("ABD", minutesLater: 1);
        var theirs = Version("ABCE", minutesLater: 2);

        var merged = PlaylistThreeWayMerge.Merge(PlannedPlaylist.Of(Version("ABC")), ours, theirs)!;

        Assert.True(merged.UpdatedAt > ours.UpdatedAt);
        Assert.True(merged.UpdatedAt > theirs.UpdatedAt);
        Assert.Equal(Id, merged.Id);
    }

    [Fact]
    public void Smart_playlists_are_never_combined()
    {
        var rules = new SmartPlaylistRules(
            MatchMode.All,
            [new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz"))],
            null,
            LiveUpdating: true);
        var smart = new Playlist(Id, "Mix", Tracks("ABD"), Then.AddMinutes(1), rules: rules);

        Assert.Null(PlaylistThreeWayMerge.Merge(PlannedPlaylist.Of(Version("ABC")), smart, Version("ABCE", minutesLater: 2)));
    }
}
