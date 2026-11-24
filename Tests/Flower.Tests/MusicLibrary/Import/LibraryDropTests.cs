using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Flower.Importer;
using Flower.Services;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.MusicLibrary.Import;

// Songs dropped on the desktop window: copied into the first library folder
// when they come from outside it, under the album artist and album, and left
// where they are when they are already in one.
public class LibraryDropTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("flower-drop-tests").FullName;
    private string Library => Path.Combine(_scratch, "Library");
    private string Elsewhere => Path.Combine(_scratch, "Downloads");

    public LibraryDropTests()
    {
        Directory.CreateDirectory(Library);
        Directory.CreateDirectory(Elsewhere);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LibraryDrop NewDrop() => new([Library], NullLogger.Instance);

    private static string Song(string folder, string fileName, string? album, string? albumArtist, int seconds = 1)
    {
        Directory.CreateDirectory(folder);
        var path = SyntheticWav.CreateFile(folder, fileName, TimeSpan.FromSeconds(seconds), SyntheticWav.Marker(1));
        using var file = TagLib.File.Create(path);
        file.Tag.Title = Path.GetFileNameWithoutExtension(fileName);
        file.Tag.Album = album;
        file.Tag.AlbumArtists = albumArtist == null ? [] : [albumArtist];
        file.Tag.Performers = ["Someone"];
        file.Save();
        return path;
    }

    [Fact]
    public async Task A_song_from_outside_is_copied_in_under_its_album_artist_and_album()
    {
        var source = Song(Elsewhere, "Thunderstruck.wav", "Live: At Donington", "AC/DC");

        var result = await NewDrop().PrepareAsync([source]);

        var copy = Path.Combine(Library, "AC_DC", "Live_ At Donington", "Thunderstruck.wav");
        Assert.Equal([copy], result.Files);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(source));
        Assert.Equal(1, result.Copied);
    }

    // Copying an album off a slow drive is the part of a drop that takes the
    // time, so the count covers every song dropped: the ones copied in and
    // the ones already in the library alike.
    [Fact]
    public async Task Dropping_reports_each_song_dealt_with_of_all_dropped()
    {
        var copied = Song(Elsewhere, "Thunderstruck.wav", "Live: At Donington", "AC/DC");
        var inPlace = Song(Path.Combine(Library, "AC_DC", "Back in Black"), "Hells Bells.wav", "Back in Black", "AC/DC");
        var progress = new RecordingProgress<SongProgress>();

        var result = await NewDrop().PrepareAsync([copied, inPlace], progress);

        Assert.Equal(1, result.Copied);
        Assert.Equal([new(0, 2), new(1, 2), new(2, 2)], progress.Reports);
    }

    [Fact]
    public async Task A_song_with_no_tags_goes_under_unknown_names()
    {
        var source = Song(Elsewhere, "untitled.wav", album: null, albumArtist: null);
        using (var file = TagLib.File.Create(source))
        {
            file.Tag.Performers = [];
            file.Save();
        }

        var result = await NewDrop().PrepareAsync([source]);

        Assert.Equal([Path.Combine(Library, "Unknown Artist", "Unknown Album", "untitled.wav")], result.Files);
    }

    [Fact]
    public async Task A_song_already_in_a_library_folder_is_not_copied()
    {
        var inPlace = Song(Path.Combine(Library, "Somewhere"), "song.wav", "Album", "Artist");

        var result = await NewDrop().PrepareAsync([inPlace]);

        Assert.Equal([inPlace], result.Files);
        Assert.Equal(0, result.Copied);
        Assert.False(Directory.Exists(Path.Combine(Library, "Artist")));
    }

    // Dropping the same song again finds the copy already made; a different
    // song by the same file name is numbered rather than written over it.
    [Fact]
    public async Task Dropping_again_reuses_the_copy_and_a_namesake_is_numbered()
    {
        var first = Song(Elsewhere, "Intro.wav", "Album", "Artist", seconds: 1);
        var namesake = Song(Path.Combine(Elsewhere, "other"), "Intro.wav", "Album", "Artist", seconds: 2);
        var drop = NewDrop();
        var copy = (await drop.PrepareAsync([first])).Files.Single();

        var again = await drop.PrepareAsync([first]);
        var numbered = await drop.PrepareAsync([namesake]);

        Assert.Equal([copy], again.Files);
        Assert.Equal(0, again.Copied);
        Assert.Equal([Path.Combine(Path.GetDirectoryName(copy)!, "Intro 2.wav")], numbered.Files);
    }

    // UF-13: the same song retagged since it was dropped is the same name and
    // often the same size - the tag edit fits the file's padding - but not the
    // same file, so it goes in rather than being taken for the old copy.
    [Fact]
    public async Task A_song_retagged_since_it_was_dropped_is_not_taken_for_the_old_copy()
    {
        var source = Song(Elsewhere, "Intro.wav", "Album", "Artist");
        var drop = NewDrop();
        var copy = (await drop.PrepareAsync([source])).Files.Single();
        var size = new FileInfo(source).Length;
        using (var file = TagLib.File.Create(source))
        {
            file.Tag.Title = "Outro";
            file.Save();
        }
        Assert.Equal(size, new FileInfo(source).Length);

        var again = await drop.PrepareAsync([source]);

        Assert.Equal(1, again.Copied);
        Assert.NotEqual([copy], again.Files);
    }

    [Fact]
    public void A_drop_says_what_did_not_go_in_and_what_came_back()
    {
        Assert.Null(LibraryDrop.Outcome(0, 0));
        Assert.Equal("2 songs could not be added", LibraryDrop.Outcome(2, 0));
        Assert.Equal("1 song could not be added. 1 removed song is back in the library", LibraryDrop.Outcome(1, 1));
    }

    [Fact]
    public async Task A_dropped_folder_is_walked_for_its_songs_only()
    {
        var album = Path.Combine(Elsewhere, "Album");
        Song(album, "one.wav", "Album", "Artist");
        Song(Path.Combine(album, "CD2"), "two.wav", "Album", "Artist");
        File.WriteAllText(Path.Combine(album, "cover.txt"), "");

        var result = await NewDrop().PrepareAsync([album]);

        Assert.Equal(["one.wav", "two.wav"], result.Files.Select(Path.GetFileName).Order());
        Assert.All(result.Files, f => Assert.StartsWith(Path.Combine(Library, "Artist", "Album"), f));
    }

    [Fact]
    public async Task With_no_library_folder_nothing_is_copied()
    {
        var source = Song(Elsewhere, "song.wav", "Album", "Artist");

        var result = await new LibraryDrop([], NullLogger.Instance).PrepareAsync([source]);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.Failed);
    }
}
