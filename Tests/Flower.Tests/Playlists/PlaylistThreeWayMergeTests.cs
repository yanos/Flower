using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Playlists;

// Two edits of one playlist made from the copy both sides last agreed on -
// this device's and the server's - combined, or refused when both cannot be
// kept, which is when sync asks (PlaylistThreeWayMerge).
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
        PlaylistThreeWayMerge.Merge(Version(before), Version(ours, minutesLater: 1), Version(theirs, minutesLater: 2)) is { } merged
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

    // Which folder it is in combines the way its name does.
    [Fact]
    public void A_move_on_one_side_and_an_addition_on_the_other_are_both_kept()
    {
        var folder = Guid.NewGuid();
        var ours = Version("ABC", minutesLater: 1);
        ours.ParentId = folder;

        var merged = PlaylistThreeWayMerge.Merge(Version("ABC"), ours, Version("ABCD", minutesLater: 2));

        Assert.NotNull(merged);
        Assert.Equal(folder, merged.ParentId);
        Assert.Equal("ABCD", string.Concat(merged.Tracks.Select(t => t.Title)));
    }

    [Fact]
    public void A_new_place_on_one_side_and_an_addition_on_the_other_are_both_kept()
    {
        var ours = Version("ABC", minutesLater: 1);
        ours.SortKey = 3;

        var merged = PlaylistThreeWayMerge.Merge(Version("ABC"), ours, Version("ABCD", minutesLater: 2));

        Assert.Equal(3, merged?.SortKey);
        Assert.Equal("ABCD", string.Concat(merged!.Tracks.Select(t => t.Title)));
    }

    [Fact]
    public void Both_sides_moving_it_to_different_folders_cannot_be_combined()
    {
        var ours = Version("ABC", minutesLater: 1);
        ours.ParentId = Guid.NewGuid();
        var theirs = Version("ABC", minutesLater: 2);
        theirs.ParentId = Guid.NewGuid();

        Assert.Null(PlaylistThreeWayMerge.Merge(Version("ABC"), ours, theirs));
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
        var before = Version("AB");

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

        var merged = PlaylistThreeWayMerge.Merge(Version("ABC"), ours, theirs)!;

        Assert.True(merged.UpdatedAt > ours.UpdatedAt);
        Assert.True(merged.UpdatedAt > theirs.UpdatedAt);
        Assert.Equal(Id, merged.Id);
    }

    private static SmartPlaylistRules Genre(string genre) => new(
        MatchMode.All,
        [new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text(genre))],
        null,
        LiveUpdating: true);

    private static Playlist Smart(string genre, string name = "Mix", int minutesLater = 0) =>
        new(Id, name, Tracks("AB"), Then.AddMinutes(minutesLater), rules: Genre(genre));

    [Fact]
    public void A_smart_playlist_renamed_on_one_side_and_given_new_rules_on_the_other_keeps_both()
    {
        var merged = PlaylistThreeWayMerge.Merge(Smart("Jazz"), Smart("Jazz", "Evening", 1), Smart("Blues", minutesLater: 2));

        Assert.Equal("Evening", merged!.Name);
        Assert.True(SmartPlaylistRules.Equivalent(Genre("Blues"), merged.Rules));
    }

    // No hand-built list is at stake, so nobody is asked: the newer rules win.
    [Fact]
    public void Rules_changed_on_both_sides_keep_the_newer()
    {
        var merged = PlaylistThreeWayMerge.Merge(Smart("Jazz"), Smart("Blues", minutesLater: 2), Smart("Soul", minutesLater: 1));

        Assert.True(SmartPlaylistRules.Equivalent(Genre("Blues"), merged!.Rules));
    }

    [Fact]
    public void A_vibe_playlists_sentence_and_name_combine_the_same_way()
    {
        Playlist Vibe(string prompt, string name = "Mix", int minutesLater = 0) =>
            new(Id, name, Tracks("AB"), Then.AddMinutes(minutesLater), vibePrompt: prompt);

        var merged = PlaylistThreeWayMerge.Merge(Vibe("rain"), Vibe("rain", "Grey day", 1), Vibe("rain at night", minutesLater: 2));

        Assert.Equal(("Grey day", "rain at night"), (merged!.Name, merged.VibePrompt));
    }

    [Fact]
    public void A_folder_renamed_on_one_side_and_moved_on_the_other_keeps_both()
    {
        var parent = Guid.NewGuid();
        Playlist Folder(string name, Guid? parentId, int minutesLater) =>
            new(Id, name, [], Then.AddMinutes(minutesLater), isFolder: true, parentId: parentId);

        var merged = PlaylistThreeWayMerge.Merge(Folder("Moods", null, 0), Folder("Feelings", null, 1), Folder("Moods", parent, 2));

        Assert.Equal(("Feelings", (Guid?)parent), (merged!.Name, merged.ParentId));
    }

    // Rules added on one side make it another kind of playlist, with no rows
    // to combine with the other side's.
    [Fact]
    public void A_playlist_that_became_smart_on_one_side_is_not_combined()
    {
        var smart = new Playlist(Id, "Mix", Tracks("ABD"), Then.AddMinutes(1), rules: Genre("Jazz"));

        Assert.Null(PlaylistThreeWayMerge.Merge(Version("ABC"), smart, Version("ABCE", minutesLater: 2)));
    }

    // UF-24: which copy left is known, not counted.
    [Fact]
    public void Removing_one_copy_of_a_song_is_not_mistaken_for_the_other()
    {
        var original = Version("AAB");
        var ours = new Playlist(Id, "Mix", [], Then, entries: original.Entries);
        var theirs = new Playlist(Id, "Mix", [], Then, entries: original.Entries);
        ours.RemoveEntriesAt([0]);
        theirs.InsertTrack(1, Songs["C"]);

        var merged = PlaylistThreeWayMerge.Merge(original, ours, theirs);

        Assert.Equal("CAB", string.Concat(merged!.Tracks.Select(t => t.Title)));
    }
}
