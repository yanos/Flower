using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Playlists;

// The library's playlists: PlaylistsChanged and when it is not raised,
// ReplacePlaylists' short-circuit, concurrency, and playlist entries rebound to
// the fresh Track instances a rescan produces.
public class LibraryPlaylistTests
{
    // The rest of the PlaylistsUnchanged short-circuit (ARCHITECTURE-REVIEW
    // §5.7). Id+UpdatedAt is the whole comparison, so each of the four ways a
    // set can differ has to be told apart from "identical" on its own - and
    // "identical" has to mean *equal*, not the same instance, since the merge
    // on the sync path rebuilds Playlist objects from a peer's manifest rather
    // than handing back the ones already held.
    private static Library LibraryHolding(params Playlist[] playlists)
    {
        var library = new Library(new List<Track>());
        foreach (var playlist in playlists)
            library.AddPlaylist(playlist);
        return library;
    }

    private static (int updated, int changed) CountEventsFrom(Library library, List<Playlist> replacement)
    {
        var updated = 0;
        var changed = 0;
        library.PlaylistsUpdated += (_, _) => updated++;
        library.PlaylistsChanged += (_, _) => changed++;
        library.ReplacePlaylists(replacement);
        return (updated, changed);
    }

    // The point of keeping the Id: everything else addresses a track by it.
    [Fact]
    public void UpdateTracks_keeps_a_renamed_file_in_the_playlists_holding_it()
    {
        var before = new Track { Title = "Fabienk", Album = "Vol.II", Path = "/music/old.mp3" };
        var library = new Library(new List<Track> { before });
        library.AddPlaylist(new Playlist("Mix", new List<Track> { before }));

        var renamed = new Track { Title = "Fabienk", Album = "Vol.II", Path = "/music/new.mp3" };
        library.UpdateTracks(new List<Track> { renamed });

        var track = Assert.Single(Assert.Single(library.Playlists).Tracks);
        Assert.Same(renamed, track);
    }

    [Fact]
    public void RemovePlaylist_for_a_playlist_not_in_the_library_is_a_no_op()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());
        library.AddPlaylist(playlist);

        library.RemovePlaylist(new Playlist("Not Present", new List<Track>()));

        Assert.Single(library.Playlists);
    }

    [Fact]
    public void AddPlaylist_raises_PlaylistsChanged()
    {
        var library = new Library(new List<Track>());
        var raised = 0;
        library.PlaylistsChanged += (_, _) => raised++;

        library.AddPlaylist(new Playlist("Mix", new List<Track>()));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void RemovePlaylist_raises_PlaylistsChanged_only_when_it_removed_something()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());
        library.AddPlaylist(playlist);

        var raised = 0;
        library.PlaylistsChanged += (_, _) => raised++;

        library.RemovePlaylist(new Playlist("Not Present", new List<Track>()));
        Assert.Equal(0, raised);

        library.RemovePlaylist(playlist);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ReplacePlaylists_raises_both_events_when_the_set_actually_changed()
    {
        var library = new Library(new List<Track>());
        var updated = 0;
        var changed = 0;
        library.PlaylistsUpdated += (_, _) => updated++;
        library.PlaylistsChanged += (_, _) => changed++;

        library.ReplacePlaylists(new List<Playlist> { new Playlist("From Peer", new List<Track>()) });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ReplacePlaylists_with_an_identical_set_raises_neither_event()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());
        library.AddPlaylist(playlist);

        var updated = 0;
        var changed = 0;
        library.PlaylistsUpdated += (_, _) => updated++;
        library.PlaylistsChanged += (_, _) => changed++;

        library.ReplacePlaylists(new List<Playlist> { playlist });

        Assert.Equal(0, updated);
        Assert.Equal(0, changed);
    }

    // The case the short-circuit actually has to handle on the sync path: an
    // equal-but-not-identical set. A reference comparison would treat this as a
    // change and rebuild the sidebar on every single poll.
    [Fact]
    public void ReplacePlaylists_short_circuits_on_an_equal_set_of_different_instances()
    {
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var library = LibraryHolding(new Playlist(id, "Mix", new List<Track>(), updatedAt));

        var (updated, changed) = CountEventsFrom(library,
            new List<Playlist> { new Playlist(id, "Mix", new List<Track>(), updatedAt) });

        Assert.Equal(0, updated);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void ReplacePlaylists_does_not_short_circuit_when_a_playlist_was_added()
    {
        var kept = new Playlist("Mix", new List<Track>());
        var library = LibraryHolding(kept);

        var (updated, changed) = CountEventsFrom(library,
            new List<Playlist> { kept, new Playlist("From Peer", new List<Track>()) });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ReplacePlaylists_does_not_short_circuit_when_a_playlist_was_removed()
    {
        var kept    = new Playlist("Mix", new List<Track>());
        var dropped = new Playlist("Gone", new List<Track>());
        var library = LibraryHolding(kept, dropped);

        var (updated, changed) = CountEventsFrom(library, new List<Playlist> { kept });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
    }

    // Same count, same ids, but one was edited on the peer - UpdatedAt is the
    // only thing that says so, since the comparison never looks at tracks.
    [Fact]
    public void ReplacePlaylists_does_not_short_circuit_when_only_UpdatedAt_moved()
    {
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var library = LibraryHolding(new Playlist(id, "Mix", new List<Track>(), updatedAt));

        var (updated, changed) = CountEventsFrom(library,
            new List<Playlist> { new Playlist(id, "Mix", new List<Track>(), updatedAt.AddSeconds(1)) });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
    }

    // Same count and same UpdatedAt, but a different playlist entirely - so the
    // comparison cannot rest on UpdatedAt alone.
    [Fact]
    public void ReplacePlaylists_does_not_short_circuit_when_only_the_Id_differs()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var library = LibraryHolding(new Playlist(Guid.NewGuid(), "Mix", new List<Track>(), updatedAt));

        var (updated, changed) = CountEventsFrom(library,
            new List<Playlist> { new Playlist(Guid.NewGuid(), "Mix", new List<Track>(), updatedAt) });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
    }

    // Order is part of the comparison because the sidebar renders playlists in
    // list order - a pure reordering is a real, visible change.
    [Fact]
    public void ReplacePlaylists_does_not_short_circuit_on_a_pure_reordering()
    {
        var first  = new Playlist("First", new List<Track>());
        var second = new Playlist("Second", new List<Track>());
        var library = LibraryHolding(first, second);

        var (updated, changed) = CountEventsFrom(library, new List<Playlist> { second, first });

        Assert.Equal(1, updated);
        Assert.Equal(1, changed);
        Assert.Equal(new[] { second.Id, first.Id }, library.Playlists.Select(p => p.Id).ToArray());
    }

    // A short-circuited call must leave the held instances alone, not just skip
    // the events: swapping in the equal copy would silently detach the Changed
    // subscription that makes in-place edits persist.
    [Fact]
    public void A_short_circuited_ReplacePlaylists_keeps_the_held_instance_and_its_subscription()
    {
        var id = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var held = new Playlist(id, "Mix", new List<Track>(), updatedAt);
        var library = LibraryHolding(held);

        library.ReplacePlaylists(new List<Playlist> { new Playlist(id, "Mix", new List<Track>(), updatedAt) });

        Assert.Same(held, Assert.Single(library.Playlists));

        var raised = 0;
        library.PlaylistsChanged += (_, _) => raised++;
        held.Name = "Renamed";

        Assert.Equal(1, raised);
    }

    // The replacement list is copied in, so a caller reusing its list after the
    // call cannot mutate the library's set behind its back.
    [Fact]
    public void ReplacePlaylists_copies_the_list_it_was_given()
    {
        var library = new Library(new List<Track>());
        var replacement = new List<Playlist> { new Playlist("Mix", new List<Track>()) };
        library.ReplacePlaylists(replacement);

        replacement.Add(new Playlist("Added behind the library's back", new List<Track>()));

        Assert.Single(library.Playlists);
    }

    // The point of the whole exercise: an in-place edit to a playlist the
    // library holds has to reach PlaylistsChanged too, or persistence is still
    // the caller remembering to announce its own mutation.
    [Theory]
    [InlineData("rename")]
    [InlineData("append")]
    [InlineData("remove")]
    [InlineData("reorder")]
    public void Editing_a_held_playlist_in_place_raises_PlaylistsChanged(string edit)
    {
        var a = new Track { Title = "A" };
        var b = new Track { Title = "B" };
        var library = new Library(new List<Track> { a, b });
        var playlist = new Playlist("Mix", new List<Track> { a, b });
        library.AddPlaylist(playlist);

        var raised = 0;
        library.PlaylistsChanged += (_, _) => raised++;

        switch (edit)
        {
            case "rename":
                playlist.Name = "Renamed";
                break;
            case "append":
                playlist.AppendTrack(new Track { Title = "C" });
                break;
            case "remove":
                playlist.RemoveTrack(a);
                break;
            case "reorder":
                playlist.MoveTrack(b, a);
                break;
        }

        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_playlist_dropped_by_ReplacePlaylists_no_longer_raises_PlaylistsChanged()
    {
        var library = new Library(new List<Track>());
        var dropped = new Playlist("Dropped", new List<Track>());
        library.AddPlaylist(dropped);
        library.ReplacePlaylists(new List<Playlist> { new Playlist("Kept", new List<Track>()) });

        var raised = 0;
        library.PlaylistsChanged += (_, _) => raised++;

        dropped.Name = "Renamed after being dropped";

        Assert.Equal(0, raised);
    }

    // Guards the copy-on-write half of the fix: a reader enumerating Playlists
    // must never see the collection mutate underneath it. With Playlists a
    // plain List<Playlist> mutated in place, this threw
    // InvalidOperationException ("Collection was modified").
    [Fact]
    public async Task Enumerating_Playlists_is_unaffected_by_a_concurrent_mutation()
    {
        var library = new Library(new List<Track>());
        for (var i = 0; i < 50; i++)
            library.AddPlaylist(new Playlist($"P{i}", new List<Track>()));

        var stop = false;
        var writer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var extra = new Playlist("Extra", new List<Track>());
                library.AddPlaylist(extra);
                library.RemovePlaylist(extra);
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            for (var i = 0; i < 2000; i++)
            {
                foreach (var playlist in library.Playlists)
                    _ = playlist.Name;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await writer;
        }
    }

    // Concurrent AddPlaylist calls are a read-modify-write over the whole list;
    // unlocked, they lose entries outright.
    [Fact]
    public void Concurrent_AddPlaylist_calls_do_not_lose_playlists()
    {
        var library = new Library(new List<Track>());

        Parallel.For(0, 500, i => library.AddPlaylist(new Playlist($"P{i}", new List<Track>())));

        Assert.Equal(500, library.Playlists.Count);
        Assert.Equal(500, library.Playlists.Select(p => p.Name).Distinct().Count());
    }

    // A rescan replaces Library.Tracks with brand-new Track instances. Playlists
    // resolve their membership to instances exactly once, at startup - so
    // without rebinding, a playlist spent the rest of the session pointing at
    // orphaned objects: a play count incremented via the library never showed up
    // in the playlist view, and vice versa.
    [Fact]
    public void UpdateTracks_repoints_playlists_at_the_freshly_scanned_track_instances()
    {
        var original = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { original });
        var playlist = new Playlist("Mix", new List<Track> { original });
        library.AddPlaylist(playlist);

        library.UpdateTracks(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        Assert.Same(library.Tracks.Single(), playlist.Tracks.Single());

        // And the shared instance really is shared, which is the whole point.
        library.IncrementPlayCount(playlist.Tracks.Single());
        Assert.Equal(1, playlist.Tracks.Single().PlayCount);
    }

    // Rebinding is not an edit. PlaylistSyncPlanner three-way-merges on
    // Playlist.UpdatedAt, so bumping it here would make every launch's rescan
    // look like a local playlist change and manufacture sync conflicts.
    [Fact]
    public void UpdateTracks_does_not_bump_playlist_UpdatedAt_when_rebinding()
    {
        var original = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { original });
        var playlist = new Playlist("Mix", new List<Track> { original });
        library.AddPlaylist(playlist);
        var before = playlist.UpdatedAt;

        library.UpdateTracks(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        Assert.Equal(before, playlist.UpdatedAt);
    }

    // A scan not finding a file is not proof the file is gone (see the
    // carried-forward sync tracks above for the same reasoning) - dropping the
    // entry would silently edit the user's playlist.
    [Fact]
    public void UpdateTracks_keeps_a_playlist_entry_the_fresh_scan_did_not_find()
    {
        var missing = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        var library = new Library(new List<Track> { missing });
        var playlist = new Playlist("Mix", new List<Track> { missing });
        library.AddPlaylist(playlist);

        library.UpdateTracks(new List<Track> { new Track { Title = "B", Path = "/music/b.mp3" } });

        Assert.Same(missing, playlist.Tracks.Single());
    }
}
