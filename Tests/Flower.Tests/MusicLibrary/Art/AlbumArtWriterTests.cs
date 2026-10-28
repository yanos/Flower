using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// The write half of the album-art story, tested against a real file and read
// back through the reader that every other part of the app uses. Both halves
// matter together rather than separately: the write chooses a picture type and
// a MIME type that the reader is the one to believe, and the two live in
// different classes precisely so that the app and Flower.Server can share them
// - which means nothing else would notice them drifting apart.
public class AlbumArtWriterTests
{
    // A 1x1 PNG, and a 1x1 GIF to replace it with. Real encoded images rather
    // than arbitrary bytes because the sniffing this exercises is by magic
    // number.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] Gif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    // A WAV rather than an mp3 because the test suite can generate one (see
    // SyntheticWav) - TagLib writes an ID3v2 tag into it the same way, which is
    // all this is about.
    private static string NewTrackFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return SyntheticWav.CreateFile(directory, "track.wav", TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
    }

    [Fact]
    public void Written_art_reads_back_with_the_type_it_was_written_as()
    {
        var path = NewTrackFile();

        Assert.True(AlbumArtWriter.TryWrite(path, Png, "image/png"));

        var art = LocalAlbumArtReader.ForFile(path);
        Assert.NotNull(art);
        Assert.Equal(Png, art!.Bytes);
        Assert.Equal("image/png", art.MimeType);
    }

    // The point of replacing rather than appending: the reader takes the first
    // picture in the tag, so a second write that merely added one would leave
    // the album still showing the old cover.
    [Fact]
    public void A_second_write_replaces_the_picture_rather_than_adding_one()
    {
        var path = NewTrackFile();

        AlbumArtWriter.TryWrite(path, Png, "image/png");
        AlbumArtWriter.TryWrite(path, Gif, "image/gif");

        var art = LocalAlbumArtReader.ForFile(path);
        Assert.Equal(Gif, art!.Bytes);
        Assert.Equal("image/gif", art.MimeType);
    }

    [Fact]
    public void Removing_the_art_leaves_the_file_with_none()
    {
        var path = NewTrackFile();
        AlbumArtWriter.TryWrite(path, Png, "image/png");

        Assert.True(AlbumArtWriter.TryRemove(path));
        Assert.Null(LocalAlbumArtReader.ForFile(path));
    }

    // Removing from a file that never had any is a success, not a no-op worth
    // reporting as failure - the caller asked for a file with no embedded art
    // and that is what it has.
    [Fact]
    public void Removing_art_that_was_never_there_succeeds()
    {
        Assert.True(AlbumArtWriter.TryRemove(NewTrackFile()));
    }

    [Fact]
    public void A_path_that_is_not_a_media_file_fails_rather_than_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "not audio");

        Assert.False(AlbumArtWriter.TryWrite(path, Png, "image/png"));
        Assert.False(AlbumArtWriter.TryRemove(path));
    }

    // What the server uses to refuse a body that claims to be an image and
    // is not, and what names a copy of art dragged out of Track Info.
    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    [InlineData(null, ".jpg")]
    [InlineData("application/octet-stream", ".jpg")]
    public void An_extension_is_chosen_for_every_type_and_guessed_for_the_rest(string? mimeType, string expected)
    {
        Assert.Equal(expected, LocalAlbumArtReader.ExtensionForMimeType(mimeType));
    }

    [Fact]
    public void Image_bytes_are_recognised_by_their_magic_number()
    {
        Assert.Equal("image/png", LocalAlbumArtReader.MimeTypeForBytes(Png));
        Assert.Equal("image/gif", LocalAlbumArtReader.MimeTypeForBytes(Gif));
        Assert.Null(LocalAlbumArtReader.MimeTypeForBytes("this is not an image at all"u8.ToArray()));
        Assert.Null(LocalAlbumArtReader.MimeTypeForBytes([]));
        Assert.Null(LocalAlbumArtReader.MimeTypeForBytes(null));
    }

    // --- Into the album's folder (ArtworkPlacement.Folder) ----------------

    // Songs of one album in a folder of their own, each with a picture of its
    // own, and in a library that knows them.
    private static (Library Library, List<Track> Songs, string Folder) AlbumFolder(params string[] albums)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var songs = albums.Select((album, i) =>
        {
            var path = SyntheticWav.CreateFile(folder, $"{i}.wav", TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
            AlbumArtWriter.TryWrite(path, Gif, "image/gif");
            return new Track { Path = path, Title = $"{i}", Album = album, AlbumArtists = "Artist" };
        }).ToList();
        return (new Library(songs), songs, folder);
    }

    // The cover goes beside the songs, and their own pictures - which would
    // win over it - come out, so it is the one every song of the folder is
    // drawn with: the ones not picked as well, since the file is theirs too.
    [Fact]
    public void A_cover_saved_to_the_folder_is_every_song_in_its_cover()
    {
        var (library, songs, folder) = AlbumFolder("Moon Pix", "Moon Pix", "Moon Pix");

        var results = AlbumArtWriter.Apply(library, [songs[0]], new LocalAlbumArt(Png, "image/png"), ArtworkPlacement.Folder);

        Assert.Equal(songs.Select(s => s.Path), results.Select(r => r.Track.Path).Order());
        Assert.All(results, r => Assert.True(r.Written));
        Assert.True(File.Exists(Path.Combine(folder, "cover.png")));
        Assert.All(songs, song =>
        {
            Assert.Null(LocalAlbumArtReader.EmbeddedIn(song.Path!));
            Assert.Equal(Png, LocalAlbumArtReader.ForFile(song.Path)!.Bytes);
        });
    }

    // A folder.jpg or an older cover.gif left beside the new one could be
    // the one the reader lists first.
    [Fact]
    public void A_cover_saved_to_the_folder_replaces_the_one_there()
    {
        var (library, songs, folder) = AlbumFolder("Moon Pix");
        File.WriteAllBytes(Path.Combine(folder, "folder.gif"), Gif);

        AlbumArtWriter.Apply(library, songs, new LocalAlbumArt(Png, "image/png"), ArtworkPlacement.Folder);

        Assert.Equal(["cover.png"], Directory.EnumerateFiles(folder).Where(LocalAlbumArtReader.IsCoverFile).Select(Path.GetFileName));
    }

    // A folder of loose songs from several albums would lend one album's
    // cover to all of them, so the picked songs get it in their files.
    [Fact]
    public void A_folder_shared_with_another_album_gets_no_cover_file()
    {
        var (library, songs, folder) = AlbumFolder("Moon Pix", "Greatest Hits");

        var results = AlbumArtWriter.Apply(library, [songs[0]], new LocalAlbumArt(Png, "image/png"), ArtworkPlacement.Folder);

        Assert.Equal(songs[0].Path, Assert.Single(results).Track.Path);
        Assert.False(Directory.EnumerateFiles(folder).Any(LocalAlbumArtReader.IsCoverFile));
        Assert.Equal(Png, LocalAlbumArtReader.EmbeddedIn(songs[0].Path!)!.Bytes);
        Assert.Equal(Gif, LocalAlbumArtReader.EmbeddedIn(songs[1].Path!)!.Bytes);
    }

    // Removing it there takes out both, or the songs' own pictures - or the
    // folder's - would still be drawn.
    [Fact]
    public void Removing_a_cover_from_the_folder_leaves_the_album_with_none()
    {
        var (library, songs, folder) = AlbumFolder("Moon Pix", "Moon Pix");
        File.WriteAllBytes(Path.Combine(folder, "cover.png"), Png);

        var results = AlbumArtWriter.Apply(library, songs, art: null, ArtworkPlacement.Folder);

        Assert.All(results, r => Assert.True(r.Written));
        Assert.All(songs, song => Assert.Null(LocalAlbumArtReader.ForFile(song.Path)));
    }
}
