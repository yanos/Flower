using System;
using System.IO;

using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

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
}
