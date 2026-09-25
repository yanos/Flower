using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// Library.RemoveTracks and LibraryRemoval on their own, against a real
// database - what "Remove from Library" leaves behind on one device,
// whichever host it is. The sync half (the server removing too) is in
// SyncScenarioTests.
[Collection("PlatformDataDirectory")]
public class LibraryRemovalTests : PinnedDataDirectory
{
    private static Track File(string path, string title) => new()
    {
        Path = path, Title = title, Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(100),
    };

    private (Library Library, TrackRepository Repository) Open(string dbPath, List<Track>? seed = null)
    {
        var repository = new TrackRepository(new FlowerDb(dbPath));
        if (seed != null)
            repository.ReplaceAll(seed);
        return (new Library(repository.LoadAll(), NullLogger<Library>.Instance, repository, null, repository), repository);
    }

    [Fact]
    public void A_file_removed_and_kept_stays_out_of_rescans_after_a_restart()
    {
        var dbPath = Path.Combine(DataDirectory, "flower.db");
        var (library, _) = Open(dbPath, [File("/music/a.mp3", "A"), File("/music/b.mp3", "B")]);

        LibraryRemoval.Remove(library, [library.Tracks.Single(t => t.Title == "B")], deleteFiles: false, NullLogger.Instance);
        var (restarted, repository) = Open(dbPath);
        restarted.UpdateTracks([File("/music/a.mp3", "A"), File("/MUSIC/B.mp3", "B")]);

        Assert.Equal(["A"], restarted.Tracks.Select(t => t.Title));
        Assert.Equal(["A"], repository.LoadAll().Select(t => t.Title));
    }

    [Fact]
    public void Removing_a_track_takes_it_out_of_ordinary_playlists_as_an_edit_and_leaves_smart_ones_to_re_evaluate()
    {
        var a = File("/music/a.mp3", "A");
        var b = File("/music/b.mp3", "B");
        var library = new Library([a, b]);
        var ordinary = new Playlist(Guid.NewGuid(), "Mix", [a, b], DateTimeOffset.UtcNow.AddDays(-1));
        var smart = new Playlist(Guid.NewGuid(), "Smart", [a, b], DateTimeOffset.UtcNow.AddDays(-1),
            rules: SmartPlaylistRules.MatchAll());
        library.AddPlaylist(ordinary);
        library.AddPlaylist(smart);
        var smartStamp = smart.UpdatedAt;

        library.RemoveTracks([b], []);

        Assert.Equal(["A"], ordinary.Tracks.Select(t => t.Title));
        Assert.True(ordinary.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(smartStamp, smart.UpdatedAt);
    }

    [Fact]
    public void Deleting_the_files_moves_them_to_the_trash_and_does_not_need_to_exclude_them()
    {
        var path = Path.Combine(DataDirectory, "song.mp3");
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        var (library, repository) = Open(Path.Combine(DataDirectory, "flower.db"), [File(path, "Song")]);

        var result = LibraryRemoval.Remove(library, library.Tracks.ToList(), deleteFiles: true, NullLogger.Instance);

        Assert.Equal(1, result.FilesTrashed);
        Assert.Equal(0, result.FilesDeleted);
        Assert.False(System.IO.File.Exists(path));
        Assert.Empty(library.Tracks);
        Assert.Empty(repository.LoadExcludedPaths().Select(e => e.Path));
    }

    // A platform with no trash - a phone - deletes outright, and says so.
    [Fact]
    public void Where_there_is_no_trash_the_files_are_deleted_for_good()
    {
        var path = Path.Combine(DataDirectory, "song.mp3");
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        var library = new Library([File(path, "Song")]);
        var previous = FileTrash.Override;
        FileTrash.Override = _ => false;
        try
        {
            var result = LibraryRemoval.Remove(library, library.Tracks.ToList(), deleteFiles: true, NullLogger.Instance);

            Assert.Equal(0, result.FilesTrashed);
            Assert.Equal(1, result.FilesDeleted);
            Assert.False(System.IO.File.Exists(path));
        }
        finally
        {
            FileTrash.Override = previous;
        }
    }

    // A file the trash refuses - a read-only mount - is left where it is,
    // reported, and kept out of scans so the removal still holds.
    [Fact]
    public void A_file_that_cannot_be_trashed_stays_out_of_the_library()
    {
        var path = Path.Combine(DataDirectory, "song.mp3");
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        var library = new Library([File(path, "Song")]);
        var previous = FileTrash.Override;
        FileTrash.Override = _ => throw new IOException("Read-only file system");
        try
        {
            var result = LibraryRemoval.Remove(library, library.Tracks.ToList(), deleteFiles: true, NullLogger.Instance);
            library.UpdateTracks([File(path, "Song")]);

            Assert.Equal([path], result.FilesNotDeleted);
            Assert.True(System.IO.File.Exists(path));
            Assert.Empty(library.Tracks);
        }
        finally
        {
            FileTrash.Override = previous;
        }
    }

    // Settings' Restore: off the list, and the next scan brings the song back -
    // across a restart, since the list is in the database.
    [Fact]
    public void A_restored_file_comes_back_with_the_next_scan_and_stays_restored_after_a_restart()
    {
        var dbPath = Path.Combine(DataDirectory, "flower.db");
        var (library, _) = Open(dbPath, [File("/music/a.mp3", "A"), File("/music/b.mp3", "B")]);
        LibraryRemoval.Remove(library, [library.Tracks.Single(t => t.Title == "B")], deleteFiles: false, NullLogger.Instance);
        Assert.Equal(["/music/b.mp3"], library.ExcludedPaths.Select(e => e.Path));

        Assert.Equal(1, library.RestoreExcludedPaths(["/MUSIC/B.MP3"]));
        var (restarted, repository) = Open(dbPath);
        restarted.UpdateTracks([File("/music/a.mp3", "A"), File("/music/b.mp3", "B")]);

        Assert.Empty(restarted.ExcludedPaths);
        Assert.Empty(repository.LoadExcludedPaths());
        Assert.Equal(["A", "B"], restarted.Tracks.Select(t => t.Title).Order());
    }

    [Fact]
    public void Restoring_a_path_that_is_not_on_the_list_does_nothing()
    {
        var library = new Library([File("/music/a.mp3", "A")]);

        Assert.Equal(0, library.RestoreExcludedPaths(["/music/a.mp3"]));
    }

    [Fact]
    public void The_list_is_newest_removal_first()
    {
        var library = new Library([File("/music/a.mp3", "A"), File("/music/b.mp3", "B")]);
        library.RemoveTracks([library.Tracks[0]], ["/music/a.mp3"]);
        System.Threading.Thread.Sleep(5);
        library.RemoveTracks([library.Tracks[0]], ["/music/b.mp3"]);

        Assert.Equal(["/music/b.mp3", "/music/a.mp3"], library.ExcludedPaths.Select(e => e.Path));
    }
}
