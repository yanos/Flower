using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.MusicLibrary.Import;

// A library following its folders while the app or the server runs: a song
// that arrives is read in on its own, and one that goes - or anything the
// watcher cannot account for one file at a time - is left to the full rescan,
// which is where the care for moves and unplugged drives lives.
public class LibraryFolderWatcherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-watcher-tests").FullName;
    private readonly Library _library = new([]);
    private int _rescans;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Driven by hand - Note, FlushAsync, PollAsync - with the operating
    // system's own notifications off, so none arrives part-way through a test
    // and starts a pass of its own. listensForChanges is the one test that
    // goes through them.
    private LibraryFolderWatcher NewWatcher(Func<Task>? scanInFlight = null, bool listensForChanges = false)
    {
        var watcher = new LibraryFolderWatcher(_library, () =>
            {
                Interlocked.Increment(ref _rescans);
                return Task.CompletedTask;
            },
            NullLogger<LibraryFolderWatcher>.Instance, NullLogger<Importer.Importer>.Instance,
            quietPeriod: TimeSpan.FromMilliseconds(200), scanInFlight, pollInterval: TimeSpan.Zero, listensForChanges);
        watcher.Watch([_root]);
        return watcher;
    }

    private string Song(string relativeFolder, string fileName, string title)
    {
        var folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);
        var path = SyntheticWav.CreateFile(folder, fileName, TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Album = "Album";
        file.Tag.Performers = ["Artist"];
        file.Save();
        return path;
    }

    [Fact]
    public async Task A_song_copied_into_a_library_folder_is_added_without_a_rescan()
    {
        using var watcher = NewWatcher();
        var path = Song("Artist/Album", "one.wav", "One");

        watcher.Note(path);
        await watcher.FlushAsync();

        Assert.Equal("One", Assert.Single(_library.Tracks).Title);
        Assert.Equal(0, _rescans);
    }

    [Fact]
    public async Task A_folder_moved_in_whole_is_walked_for_its_songs()
    {
        using var watcher = NewWatcher();
        Song("Moved/Album", "one.wav", "One");
        Song("Moved/Album/CD2", "two.wav", "Two");

        watcher.Note(Path.Combine(_root, "Moved"));
        await watcher.FlushAsync();

        Assert.Equal(["One", "Two"], _library.Tracks.Select(t => t.Title).Order());
    }

    [Fact]
    public async Task A_song_that_goes_away_is_left_to_a_full_rescan()
    {
        using var watcher = NewWatcher();
        var path = Song("Artist/Album", "one.wav", "One");
        watcher.Note(path);
        await watcher.FlushAsync();

        File.Delete(path);
        watcher.Note(path);
        await watcher.FlushAsync();

        Assert.Equal(1, _rescans);
    }

    // A song edited in another program comes back with its new tags and its
    // own history, the way a rescan would bring it.
    [Fact]
    public async Task A_song_changed_on_disk_is_read_again_keeping_its_plays()
    {
        using var watcher = NewWatcher();
        var path = Song("Artist/Album", "one.wav", "One");
        watcher.Note(path);
        await watcher.FlushAsync();
        var before = Assert.Single(_library.Tracks);
        before.PlayCount = 5;

        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "One (Remastered)";
            file.Save();
        }
        watcher.Note(path);
        await watcher.FlushAsync();

        var after = Assert.Single(_library.Tracks);
        Assert.Equal("One (Remastered)", after.Title);
        Assert.Equal(5, after.PlayCount);
        Assert.Equal(before.Id, after.Id);
    }

    // The server moves songs in its own folders (an admin device's move, a
    // replaced file) and tells the library as it does; by the time the
    // watcher's pass runs, the old path is no longer a song anybody has, and
    // a minutes-long rescan of a NAS share for it would be for nothing.
    [Fact]
    public async Task A_song_the_library_has_already_moved_costs_no_rescan()
    {
        using var watcher = NewWatcher();
        var from = Song("Artist/Album", "one.wav", "One");
        watcher.Note(from);
        await watcher.FlushAsync();

        var to = Path.Combine(_root, "Artist", "Album", "01 One.wav");
        File.Move(from, to);
        watcher.Note(from);
        watcher.Note(to);
        _library.MoveTrack(_library.Tracks.Single(), to);
        await watcher.FlushAsync();

        Assert.Equal(0, _rescans);
        Assert.Equal(to, Assert.Single(_library.Tracks).Path);
    }

    // A network share reports nothing another machine does to it, so the
    // folders are also listed - and what the listing finds is dealt with as
    // if it had been seen happening.
    [Fact]
    public async Task Listing_the_folders_finds_a_song_nothing_reported()
    {
        using var watcher = NewWatcher();
        Song("Artist/Album", "one.wav", "One");

        await watcher.PollAsync();
        await watcher.FlushAsync();

        Assert.Equal("One", Assert.Single(_library.Tracks).Title);
        Assert.Equal(0, _rescans);
    }

    [Fact]
    public async Task Listing_the_folders_rescans_for_a_song_whose_file_has_gone()
    {
        using var watcher = NewWatcher();
        var keep = Song("Artist/Album", "one.wav", "One");
        var lose = Song("Artist/Album", "two.wav", "Two");
        watcher.Note(keep);
        watcher.Note(lose);
        await watcher.FlushAsync();

        File.Delete(lose);
        await watcher.PollAsync();
        await watcher.FlushAsync();

        Assert.Equal(1, _rescans);
    }

    // An unmounted share lists as an empty folder, and must not read as
    // every song in it having been deleted.
    [Fact]
    public async Task A_folder_that_lists_nothing_is_not_read_as_every_song_gone()
    {
        using var watcher = NewWatcher();
        var path = Song("Artist/Album", "one.wav", "One");
        watcher.Note(path);
        await watcher.FlushAsync();

        Directory.Delete(Path.Combine(_root, "Artist"), recursive: true);
        await watcher.PollAsync();
        await watcher.FlushAsync();

        Assert.Equal(0, _rescans);
    }

    // A pass waits for a scan already running, which may have listed the
    // folders before the song arrived and would write the library back
    // without it.
    [Fact]
    public async Task A_song_is_added_only_after_a_scan_in_flight_finishes()
    {
        var scan = new TaskCompletionSource();
        using var watcher = NewWatcher(scanInFlight: () => scan.Task);
        var path = Song("Artist/Album", "one.wav", "One");

        watcher.Note(path);
        var pass = watcher.FlushAsync();
        await Task.Delay(100);
        Assert.Empty(_library.Tracks);

        // The scan's own write, which knew nothing of the song.
        _library.UpdateTracks([]);
        scan.SetResult();
        await pass;

        Assert.Equal("One", Assert.Single(_library.Tracks).Title);
    }

    // Other programs keep their own files in music folders - Music.app its
    // database, Finder its .DS_Store, a desktop its trash - and none of it
    // may start a pass.
    [Fact]
    public async Task What_a_scan_would_not_take_starts_nothing()
    {
        using var watcher = NewWatcher();
        var notes = Path.Combine(_root, "notes.txt");
        File.WriteAllText(notes, "");
        var trashed = Song(".Trash/Album", "gone.wav", "Gone");
        var temporary = Path.Combine(_root, "partial.tmp");

        watcher.Note(notes);
        watcher.Note(trashed);
        watcher.Note(temporary);
        watcher.Note(Path.Combine(Path.GetTempPath(), "elsewhere.wav"));
        await watcher.FlushAsync();

        Assert.Empty(_library.Tracks);
        Assert.Equal(0, _rescans);
    }

    [Fact]
    public async Task A_song_removed_from_the_library_on_purpose_stays_out()
    {
        using var watcher = NewWatcher();
        var path = Song("Artist/Album", "one.wav", "One");
        watcher.Note(path);
        await watcher.FlushAsync();
        _library.RemoveTracks([.. _library.Tracks], [path]);

        watcher.Note(path);
        await watcher.FlushAsync();

        Assert.Empty(_library.Tracks);
    }

    // The whole thing, through the operating system's own change
    // notifications rather than Note.
    [Fact]
    public async Task A_song_copied_in_shows_up_by_itself()
    {
        using var watcher = NewWatcher(listensForChanges: true);
        // Give the watcher a moment to be listening before the file lands.
        await Task.Delay(300);

        Song("Artist/Album", "one.wav", "One");

        var clock = Stopwatch.StartNew();
        while (_library.Tracks.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(100);

        Assert.Equal("One", Assert.Single(_library.Tracks).Title);
    }
}
