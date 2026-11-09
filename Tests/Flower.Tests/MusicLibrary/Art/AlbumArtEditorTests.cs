using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// The cover change Track Info makes, against a real file: the
// picture lands in the tag, the cached bitmap is let go, and the library says
// the art changed so every view showing that cover repaints it.
public class AlbumArtEditorTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static string NewTrackFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return SyntheticWav.CreateFile(directory, "track.wav", TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
    }

    [Fact]
    public async Task Writing_a_cover_puts_it_in_the_file_and_says_the_art_changed()
    {
        var track = new Track { Title = "T", Path = NewTrackFile() };
        var library = new Library([track]);
        TrackChangedEventArgs? changed = null;
        library.TrackChanged += (_, e) => changed = e;
        var editor = new AlbumArtEditor([track], library, new AppSettings(), null, null, NullLogger.Instance);

        Assert.True(editor.CanWrite);
        var error = await editor.WriteAsync(Png, "image/png");

        Assert.Null(error);
        Assert.Equal(Png, LocalAlbumArtReader.ForFile(track.Path)!.Bytes);
        Assert.Equal(TrackChange.Artwork, changed?.Change);
        Assert.Same(track, Assert.Single(changed!.Tracks));
    }

    // The picture's size is stored on the track (Track.ArtWidth), so a smart
    // playlist of songs without one has to hear that a song now has one - and
    // that a removed one is gone.
    [Fact]
    public async Task Writing_and_removing_a_cover_measures_the_file_again()
    {
        var track = new Track { Title = "T", Path = NewTrackFile() };
        var editor = new AlbumArtEditor([track], new Library([track]), new AppSettings(), null, null, NullLogger.Instance);

        await editor.WriteAsync(SyntheticPng.Build(640, 480), "image/png");
        Assert.Equal((640, 480), (track.ArtWidth, track.ArtHeight));

        await editor.RemoveAsync();
        Assert.Equal((0, 0), (track.ArtWidth, track.ArtHeight));
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_reported_rather_than_thrown()
    {
        var track = new Track { Title = "T", Path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp3") };
        var editor = new AlbumArtEditor([track], new Library([track]), new AppSettings(), null, null, NullLogger.Instance);

        var error = await editor.WriteAsync(Png, "image/png");

        Assert.Equal("The artwork couldn't be written to the file.", error);
    }

    // --- Only the songs picked -------------------------------------------

    // Songs of one album, each a file in a folder of its own, so a cover file
    // in one folder could never stand in for a picture in the tag.
    private static (Library Library, List<Track> Songs) Album(int count)
    {
        var songs = Enumerable.Range(0, count)
            .Select(i => new Track { Title = $"{i}", Album = "Moon Pix", AlbumArtists = "Cat Power", Path = NewTrackFile() })
            .ToList();
        return (new Library(songs), songs);
    }

    // Songs of one album sharing one folder, as an album usually is.
    private static (Library Library, List<Track> Songs, string Folder) AlbumInOneFolder(int count)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var songs = Enumerable.Range(0, count)
            .Select(i => new Track
            {
                Title = $"{i}", Album = "Moon Pix", AlbumArtists = "Cat Power",
                Path = SyntheticWav.CreateFile(folder, $"{i}.wav", TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp()),
            })
            .ToList();
        return (new Library(songs), songs, folder);
    }

    [Fact]
    public async Task A_cover_goes_on_the_picked_songs_and_the_rest_of_the_album_keeps_its_own()
    {
        var (library, songs) = Album(3);
        TrackChangedEventArgs? changed = null;
        library.TrackChanged += (_, e) => changed = e;
        var editor = new AlbumArtEditor([songs[0], songs[1]], library, new AppSettings(), null, null, NullLogger.Instance);

        Assert.Equal(2, editor.FileCount);
        Assert.Null(await editor.WriteAsync(Png, "image/png"));

        Assert.Equal(Png, LocalAlbumArtReader.EmbeddedIn(songs[0].Path!)!.Bytes);
        Assert.Equal(Png, LocalAlbumArtReader.EmbeddedIn(songs[1].Path!)!.Bytes);
        Assert.Null(LocalAlbumArtReader.EmbeddedIn(songs[2].Path!));
        Assert.Equal(songs.Take(2), changed!.Tracks.OrderBy(t => t.Title));
    }

    [Fact]
    public async Task Removing_a_cover_takes_it_off_the_picked_songs_alone()
    {
        var (library, songs) = Album(2);
        foreach (var song in songs)
            AlbumArtWriter.TryWrite(song.Path!, Png, "image/png");
        var editor = new AlbumArtEditor([songs[1]], library, new AppSettings(), null, null, NullLogger.Instance);

        Assert.Null(await editor.RemoveAsync());

        Assert.NotNull(LocalAlbumArtReader.EmbeddedIn(songs[0].Path!));
        Assert.Null(LocalAlbumArtReader.EmbeddedIn(songs[1].Path!));
    }

    // A cover file in the album's folder is every song's there, so with
    // covers kept in the folder, some of an album's songs get theirs in their
    // files instead.
    [Fact]
    public async Task With_covers_kept_in_the_folder_part_of_an_album_gets_its_cover_in_its_files()
    {
        var (library, songs, folder) = AlbumInOneFolder(2);
        var settings = new AppSettings { ArtworkPlacement = ArtworkPlacement.Folder };
        var editor = new AlbumArtEditor([songs[0]], library, settings, null, null, NullLogger.Instance);

        Assert.Null(await editor.WriteAsync(Png, "image/png"));

        Assert.Empty(Directory.EnumerateFiles(folder).Where(LocalAlbumArtReader.IsCoverFile));
        Assert.Equal(Png, LocalAlbumArtReader.EmbeddedIn(songs[0].Path!)!.Bytes);
        Assert.Null(LocalAlbumArtReader.ForFile(songs[1].Path));
    }

    [Fact]
    public async Task With_covers_kept_in_the_folder_a_whole_album_picked_gets_its_cover_there()
    {
        var (library, songs, folder) = AlbumInOneFolder(2);
        var settings = new AppSettings { ArtworkPlacement = ArtworkPlacement.Folder };
        var editor = new AlbumArtEditor(songs, library, settings, null, null, NullLogger.Instance);

        Assert.Null(await editor.WriteAsync(Png, "image/png"));

        Assert.True(File.Exists(Path.Combine(folder, "cover.png")));
        Assert.All(songs, song => Assert.Null(LocalAlbumArtReader.EmbeddedIn(song.Path!)));
    }
}
