using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Playlists;

// What a playlist sync session installs when the user went on editing while it
// ran (PlaylistSessionOverlay): the session's decision, laid over every
// playlist created, edited or deleted here since it planned.
public class PlaylistSessionOverlayTests
{
    private static readonly Dictionary<string, Track> Songs =
        "ABCDE".ToDictionary(c => c.ToString(), c => new Track { Title = c.ToString() });

    private static readonly DateTimeOffset Then = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // One playlist as it stood at some moment: the session planned from
    // minute 0, the other side edited at minute 1, this device at minute 2.
    private static Playlist Version(Guid id, string titles, string name = "Mix", int minutesLater = 0) =>
        new(id, name, titles.Select(c => Songs[c.ToString()]).ToList(), Then.AddMinutes(minutesLater));

    private static string Titles(Playlist playlist) => string.Concat(playlist.Tracks.Select(t => t.Title));

    private static Dictionary<Guid, PlannedPlaylist> Planned(params Playlist[] playlists) =>
        playlists.ToDictionary(p => p.Id, PlannedPlaylist.Of);

    [Fact]
    public void A_playlist_left_alone_while_the_session_ran_takes_what_the_session_decided()
    {
        var id = Guid.NewGuid();
        var here = Version(id, "ABC");
        var decided = Version(id, "ABCD", minutesLater: 1);

        var result = PlaylistSessionOverlay.Apply([decided], Planned(here), [here]);

        Assert.Same(decided, Assert.Single(result.Installed));
        Assert.Empty(result.HeldBack);
        Assert.Empty(result.Merged);
    }

    [Fact]
    public void A_playlist_the_session_brought_from_the_other_side_is_installed()
    {
        var fromServer = Version(Guid.NewGuid(), "AB", name: "Theirs");

        var result = PlaylistSessionOverlay.Apply([fromServer], Planned(), []);

        Assert.Same(fromServer, Assert.Single(result.Installed));
    }

    [Fact]
    public void A_playlist_deleted_here_while_the_session_ran_stays_deleted()
    {
        var id = Guid.NewGuid();
        var planned = Version(id, "ABC");

        var result = PlaylistSessionOverlay.Apply([Version(id, "ABCD", minutesLater: 1)], Planned(planned), []);

        Assert.Empty(result.Installed);
    }

    [Fact]
    public void A_playlist_created_here_while_the_session_ran_is_kept()
    {
        var created = Version(Guid.NewGuid(), "E", name: "New", minutesLater: 2);

        var result = PlaylistSessionOverlay.Apply([], Planned(), [created]);

        Assert.Same(created, Assert.Single(result.Installed));
        Assert.Empty(result.HeldBack);
    }

    // The session took the other side's newer copy; this device added a song
    // to its own meanwhile. Both are kept, as a version newer than either, so
    // the push carries it back to the other side.
    [Fact]
    public void Edits_on_both_sides_that_combine_are_installed_as_one_newer_version()
    {
        var id = Guid.NewGuid();
        var planned = Version(id, "ABC");
        var theirs = Version(id, "AC", minutesLater: 1);
        var ours = Version(id, "ABCD", minutesLater: 2);

        var result = PlaylistSessionOverlay.Apply([theirs], Planned(planned), [ours]);

        var installed = Assert.Single(result.Installed);
        Assert.Equal("ACD", Titles(installed));
        Assert.True(installed.UpdatedAt > ours.UpdatedAt);
        Assert.Equal(id, Assert.Single(result.Merged));
        Assert.Empty(result.HeldBack);
    }

    // Renamed to two different names: there is no combining that, so this
    // device's edit is kept but held back - not pushed over the other one, and
    // asked about next session.
    [Fact]
    public void Edits_on_both_sides_that_collide_keep_this_devices_and_hold_it_back()
    {
        var id = Guid.NewGuid();
        var planned = Version(id, "ABC");
        var theirs = Version(id, "ABC", name: "Road Trip", minutesLater: 1);
        var ours = Version(id, "ABC", name: "Summer", minutesLater: 2);

        var result = PlaylistSessionOverlay.Apply([theirs], Planned(planned), [ours]);

        Assert.Same(ours, Assert.Single(result.Installed));
        Assert.Equal(id, Assert.Single(result.HeldBack));
        Assert.Empty(result.Merged);
    }

    [Fact]
    public void A_playlist_deleted_on_the_other_side_and_left_alone_here_goes()
    {
        var id = Guid.NewGuid();
        var here = Version(id, "ABC");

        var result = PlaylistSessionOverlay.Apply([], Planned(here), [here]);

        Assert.Empty(result.Installed);
    }

    // An edit beats a delete, here as everywhere else.
    [Fact]
    public void A_playlist_deleted_on_the_other_side_but_edited_here_meanwhile_is_kept()
    {
        var id = Guid.NewGuid();
        var planned = Version(id, "ABC");
        var edited = Version(id, "ABCD", minutesLater: 2);

        var result = PlaylistSessionOverlay.Apply([], Planned(planned), [edited]);

        Assert.Same(edited, Assert.Single(result.Installed));
        Assert.Empty(result.HeldBack);
    }

    // Several at once, each by its own rule, and none disturbing the others.
    [Fact]
    public void Each_playlist_is_settled_on_its_own()
    {
        Guid kept = Guid.NewGuid(), deleted = Guid.NewGuid(), combined = Guid.NewGuid();
        var keptHere = Version(kept, "AB", name: "Kept");
        var deletedPlanned = Version(deleted, "C", name: "Deleted");
        var combinedPlanned = Version(combined, "ABC", name: "Combined");
        var created = Version(Guid.NewGuid(), "E", name: "Created", minutesLater: 2);

        var result = PlaylistSessionOverlay.Apply(
            [keptHere, Version(deleted, "CD", name: "Deleted", minutesLater: 1), Version(combined, "AC", name: "Combined", minutesLater: 1)],
            Planned(keptHere, deletedPlanned, combinedPlanned),
            [keptHere, Version(combined, "ABCD", name: "Combined", minutesLater: 2), created]);

        Assert.Equal(
            ["Combined:ACD", "Created:E", "Kept:AB"],
            result.Installed.Select(p => $"{p.Name}:{Titles(p)}").Order());
        Assert.Equal(combined, Assert.Single(result.Merged));
    }
}
