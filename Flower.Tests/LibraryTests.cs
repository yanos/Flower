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
}
