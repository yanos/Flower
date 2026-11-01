using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

using Xunit;

namespace Flower.Tests.Playlists;

// Playlist folders read back out of a flat list (PlaylistTree): what nests
// where, what a parent that does not hold up means, and what a folder plays.
public class PlaylistTreeTests
{
    private static Playlist Folder(string name, Playlist? parent = null) => Playlist.NewFolder(name, parent?.Id);

    private static Playlist List(string name, Playlist? parent = null, params Track[] tracks) =>
        new(name, tracks.ToList()) { ParentId = parent?.Id };

    private static string Shape(IEnumerable<PlaylistTreeRow> rows) =>
        string.Join(" ", rows.Select(r => new string('>', r.Depth) + r.Playlist.Name));

    [Fact]
    public void Playlists_sit_under_their_folders_in_list_order()
    {
        var rock = Folder("Rock");
        var all = new List<Playlist>
        {
            List("Loose"),
            rock,
            List("Live", rock),
            Folder("Empty"),
        };
        var seventies = Folder("70s", rock);
        all.Add(seventies);
        all.Add(List("Glam", seventies));
        all.Add(List("Studio", rock));

        Assert.Equal("Loose Rock >Live >70s >>Glam >Studio Empty", Shape(PlaylistTree.Flatten(all)));
    }

    [Fact]
    public void A_closed_folder_is_listed_without_what_is_in_it()
    {
        var rock = Folder("Rock");
        var all = new List<Playlist> { rock, List("Live", rock), List("After") };

        Assert.Equal("Rock After", Shape(PlaylistTree.Flatten(all, p => p == rock)));
    }

    // A folder deleted on one device while another moved a playlist into it.
    [Fact]
    public void A_playlist_whose_folder_is_gone_is_at_the_top_level()
    {
        var orphan = new Playlist("Orphan", []) { ParentId = Guid.NewGuid() };
        var all = new List<Playlist> { orphan };

        Assert.Null(PlaylistTree.ParentOf(orphan, all));
        Assert.Equal("Orphan", Shape(PlaylistTree.Flatten(all)));
    }

    [Fact]
    public void A_parent_that_is_not_a_folder_is_no_parent()
    {
        var plain = List("Plain");
        var inside = List("Inside", plain);

        Assert.Null(PlaylistTree.ParentOf(inside, [plain, inside]));
    }

    // Two devices each moving one of two folders into the other.
    [Fact]
    public void Folders_inside_each_other_are_both_at_the_top_level()
    {
        var a = Folder("A");
        var b = Folder("B", a);
        a.ParentId = b.Id;
        var all = new List<Playlist> { a, b, List("In B", b) };

        Assert.Equal("A B >In B", Shape(PlaylistTree.Flatten(all)));
    }

    [Fact]
    public void A_folder_plays_every_song_of_its_playlists_once_in_order()
    {
        var x = new Track { Title = "x" };
        var y = new Track { Title = "y" };
        var z = new Track { Title = "z" };
        var folder = Folder("F");
        var inner = Folder("Inner", folder);
        var all = new List<Playlist>
        {
            folder,
            List("One", folder, x, y),
            inner,
            List("Two", inner, y, z),
            List("Outside", null, z, x),
        };

        Assert.Equal(["x", "y", "z"], PlaylistTree.TracksOf(folder, all).Select(t => t.Title));
        Assert.Equal(["y", "z"], PlaylistTree.TracksOf(inner, all).Select(t => t.Title));
    }

    [Fact]
    public void A_folder_cannot_go_into_itself_or_anything_inside_it()
    {
        var outer = Folder("Outer");
        var inner = Folder("Inner", outer);
        var other = Folder("Other");
        var all = new List<Playlist> { outer, inner, other };

        Assert.False(PlaylistTree.CanMove(outer, outer, all));
        Assert.False(PlaylistTree.CanMove(outer, inner, all));
        Assert.True(PlaylistTree.CanMove(outer, other, all));
        Assert.True(PlaylistTree.CanMove(inner, null, all));
        Assert.Equal(["Other"], PlaylistTree.FoldersFor([outer], all).Select(r => r.Playlist.Name));
    }

    [Fact]
    public void A_playlist_cannot_go_into_another_listeners_folder()
    {
        var guests = new Playlist(Guid.NewGuid(), "Guest's", [], DateTimeOffset.UtcNow, listener: "5306d3aa", isFolder: true);
        var owners = List("Owner's");

        Assert.False(PlaylistTree.CanMove(owners, guests, [guests, owners]));
    }

    // --- Order (Playlist.SortKey, PlaylistTree.Place) ----------------------

    private static Playlist Placed(string name, double key) => new(name, []) { SortKey = key };

    private static string Names(IEnumerable<Playlist> playlists) => string.Join(" ", playlists.Select(p => p.Name));

    [Fact]
    public void Placed_playlists_come_in_their_order_and_the_never_placed_after_them_as_listed()
    {
        var all = new List<Playlist> { List("C"), Placed("B", 2), List("D"), Placed("A", 1) };

        Assert.Equal("A B C D", Shape(PlaylistTree.Flatten(all)));
    }

    [Fact]
    public void A_playlist_dragged_before_another_goes_there()
    {
        var a = List("A");
        var b = List("B");
        var c = List("C");
        var all = new List<Playlist> { a, b, c };

        Assert.True(PlaylistTree.Place([c], null, 0, all));

        Assert.Equal("C A B", Shape(PlaylistTree.Flatten(all)));
    }

    // Once a folder's playlists have places, a drag gives a new one to the
    // playlist dragged and to nothing else - so it is one playlist's edit to
    // sync, not the whole folder's.
    [Fact]
    public void Once_placed_a_drag_changes_only_the_playlist_dragged()
    {
        var a = List("A");
        var b = List("B");
        var c = List("C");
        var all = new List<Playlist> { a, b, c };
        PlaylistTree.Place([c], null, 0, all);
        var (aEdited, bEdited) = (a.UpdatedAt, b.UpdatedAt);

        PlaylistTree.Place([a], null, 2, all);

        Assert.Equal("C B A", Shape(PlaylistTree.Flatten(all)));
        Assert.True(aEdited < a.UpdatedAt);
        Assert.Equal(bEdited, b.UpdatedAt);
    }

    [Fact]
    public void Dragged_onto_its_own_place_nothing_changes()
    {
        var a = Placed("A", 1);
        var b = Placed("B", 2);
        var edited = a.UpdatedAt;

        Assert.False(PlaylistTree.Place([a], null, 0, [a, b]));
        Assert.Equal(edited, a.UpdatedAt);
    }

    [Fact]
    public void Dragged_into_a_folder_it_goes_there_at_the_place_given()
    {
        var rock = Folder("Rock");
        var live = List("Live", rock);
        var studio = List("Studio", rock);
        var loose = List("Loose");
        var all = new List<Playlist> { rock, live, studio, loose };

        PlaylistTree.Place([loose], rock, 1, all);

        Assert.Equal(rock.Id, loose.ParentId);
        Assert.Equal("Live Loose Studio", Names(PlaylistTree.ChildrenOf(rock, all)));
    }

    [Fact]
    public void A_folder_is_not_placed_inside_itself()
    {
        var outer = Folder("Outer");
        var inner = Folder("Inner", outer);

        Assert.False(PlaylistTree.Place([outer], inner, 0, [outer, inner]));
        Assert.Null(outer.ParentId);
    }

    // Halving one gap again and again runs out of room between two keys;
    // then the folder is numbered afresh and the order still holds.
    [Fact]
    public void Dragged_into_the_same_gap_many_times_the_order_still_holds()
    {
        var first = List("First");
        var last = List("Last");
        var all = new List<Playlist> { first, last };
        PlaylistTree.Place([last], null, 1, all);
        PlaylistTree.Place([first], null, 0, all);

        var added = new List<string>();
        for (var i = 0; i < 80; i++)
        {
            var next = List($"N{i}");
            all.Add(next);
            PlaylistTree.Place([next], null, 1, all);
            added.Insert(0, next.Name);
        }

        Assert.Equal(["First", .. added, "Last"], PlaylistTree.Flatten(all).Select(r => r.Playlist.Name));
    }
}
