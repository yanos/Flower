using System.Collections.Generic;
using System.Linq;
using Flower.Models;

namespace Flower.Tests;

public class LibraryTests
{
    [Fact]
    public void UpdateTracks_replaces_the_track_list()
    {
        var library = new Library(new List<Track> { new Track { Title = "Old" } });
        library.UpdateTracks(new List<Track> { new Track { Title = "New1" }, new Track { Title = "New2" } });

        Assert.Equal(2, library.Tracks.Count);
        Assert.DoesNotContain(library.Tracks, t => t.Title == "Old");
    }

    [Fact]
    public void UpdateTracks_raises_TracksUpdated_exactly_once()
    {
        var library = new Library(new List<Track>());
        int raised = 0;
        library.TracksUpdated += (_, _) => raised++;

        library.UpdateTracks(new List<Track> { new Track { Title = "A" } });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void UpdateTracks_copies_the_list_so_later_mutating_the_source_has_no_effect()
    {
        var source = new List<Track> { new Track { Title = "A" } };
        var library = new Library(source);

        source.Add(new Track { Title = "B" });

        Assert.Single(library.Tracks);
    }

    [Fact]
    public void AddPlaylist_appends_to_Playlists()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());

        library.AddPlaylist(playlist);

        Assert.Single(library.Playlists);
        Assert.Same(playlist, library.Playlists.Single());
    }

    [Fact]
    public void RemovePlaylist_removes_the_given_playlist()
    {
        var library = new Library(new List<Track>());
        var keep = new Playlist("Keep", new List<Track>());
        var remove = new Playlist("Remove", new List<Track>());
        library.AddPlaylist(keep);
        library.AddPlaylist(remove);

        library.RemovePlaylist(remove);

        Assert.Single(library.Playlists);
        Assert.Same(keep, library.Playlists.Single());
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
}
