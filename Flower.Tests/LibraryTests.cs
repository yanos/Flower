using System;
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
    public void UpdateTracks_preserves_DateAdded_for_a_track_matched_by_path()
    {
        var originalDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track>
        {
            new Track { Title = "Old", Path = "/music/a.mp3", DateAdded = originalDate }
        });

        // Simulates a rescan: Importer builds a brand-new Track for the same file
        // (tags re-read from disk), defaulting DateAdded to "now" like a genuinely
        // new file would - UpdateTracks must recognize it's the same file by Path
        // and keep the original date instead.
        var rescanned = new Track { Title = "Old (retagged)", Path = "/music/a.mp3" };

        library.UpdateTracks(new List<Track> { rescanned });

        Assert.Equal(originalDate, library.Tracks.Single().DateAdded);
    }

    [Fact]
    public void UpdateTracks_matches_paths_case_insensitively()
    {
        var originalDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track> { new Track { Path = "/Music/A.mp3", DateAdded = originalDate } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/a.mp3" } });

        Assert.Equal(originalDate, library.Tracks.Single().DateAdded);
    }

    [Fact]
    public void UpdateTracks_leaves_DateAdded_alone_for_a_track_with_no_previous_match()
    {
        var freshDate = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track> { new Track { Path = "/music/other.mp3" } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/new.mp3", DateAdded = freshDate } });

        Assert.Equal(freshDate, library.Tracks.Single().DateAdded);
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
