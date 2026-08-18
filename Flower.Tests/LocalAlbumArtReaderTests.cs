using System;
using System.IO;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The lookup that used to exist three times (AlbumArtLoader, SyncHttpServer,
// Flower.Server's SubsonicEndpoints) and had drifted between them - the
// server's copy accepted only .jpg/.jpeg/.png, so an album with a cover.webp
// showed art in the app and 404'd from a self-hosted server for the same
// library. Now one implementation, so these cases hold for all three.
public class LocalAlbumArtReaderTests : IDisposable
{
    private readonly string _directory;

    public LocalAlbumArtReaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "flower-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // Not a real audio file: TagLib throws on it, which is exactly the
    // fall-through into the cover-file branch these cases are about.
    private string TrackFile()
    {
        var path = Path.Combine(_directory, "song.mp3");
        File.WriteAllText(path, "not really an mp3");
        return path;
    }

    private string ArtFile(string name, byte[]? bytes = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3, 4]);
        return path;
    }

    [Theory]
    [InlineData("cover.jpg", "image/jpeg")]
    [InlineData("cover.jpeg", "image/jpeg")]
    [InlineData("folder.png", "image/png")]
    [InlineData("cover.webp", "image/webp")]  // the format the server's copy used to refuse
    [InlineData("cover.gif", "image/gif")]
    [InlineData("folder.tif", "image/tiff")]
    public void A_cover_or_folder_file_is_found_and_typed_by_its_extension(string name, string mimeType)
    {
        var track = TrackFile();
        ArtFile(name);

        var art = LocalAlbumArtReader.ForFile(track);

        Assert.NotNull(art);
        Assert.Equal(mimeType, art.MimeType);
        Assert.Equal([1, 2, 3, 4], art.Bytes);
    }

    [Fact]
    public void The_extension_match_is_case_insensitive_like_the_stem_match()
    {
        var track = TrackFile();
        ArtFile("COVER.PNG");

        Assert.Equal("image/png", LocalAlbumArtReader.ForFile(track)!.MimeType);
    }

    [Fact]
    public void An_image_that_is_not_named_cover_or_folder_is_ignored()
    {
        var track = TrackFile();
        ArtFile("band-photo.jpg");

        Assert.Null(LocalAlbumArtReader.ForFile(track));
    }

    [Fact]
    public void A_cover_file_in_an_extension_nothing_can_decode_is_ignored()
    {
        var track = TrackFile();
        ArtFile("cover.txt");

        Assert.Null(LocalAlbumArtReader.ForFile(track));
    }

    [Fact]
    public void A_directory_with_no_art_at_all_yields_null_rather_than_throwing()
    {
        Assert.Null(LocalAlbumArtReader.ForFile(TrackFile()));
    }

    // A placeholder track (Path == null) has no local file by definition -
    // this is reached on every synced-but-not-downloaded track, so it has to
    // be a plain null rather than an exception.
    [Fact]
    public void A_null_or_missing_path_yields_null()
    {
        Assert.Null(LocalAlbumArtReader.ForFile(null));
        Assert.Null(LocalAlbumArtReader.ForFile(Path.Combine(_directory, "nope", "gone.mp3")));
    }
}
