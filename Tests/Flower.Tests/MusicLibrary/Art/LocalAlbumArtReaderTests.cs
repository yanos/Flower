using System;
using System.IO;

using Flower.DeviceChecks;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// The lookup that used to exist three times (AlbumArtLoader, the app's own
// listener, Flower.Server's media endpoints) and had drifted between them - the
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

    // A song whose picture frame a tagger wrote with no description, so the
    // image's own first bytes - FF D8 FF E0, up to the zero after them - read
    // as one, and the picture comes back five bytes short and undecodable.
    // Bill Callahan's Apocalypse, as it is on the owner's server.
    [Fact]
    public void A_picture_whose_first_bytes_were_read_as_its_description_is_put_back_together()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0xFF, 0xD9];
        var path = SongWithPicture(jpeg[5..], description: "\u00FF\u00D8\u00FF\u00E0");

        var art = LocalAlbumArtReader.EmbeddedIn(path);

        Assert.NotNull(art);
        Assert.Equal(jpeg, art.Bytes);
    }

    // A description that is words is left alone, and so is a picture that is
    // an image already.
    [Fact]
    public void A_picture_with_a_real_description_is_read_as_it_is()
    {
        byte[] notAnImage = [0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
        Assert.Equal(notAnImage, LocalAlbumArtReader.EmbeddedIn(SongWithPicture(notAnImage, "Front"))!.Bytes);

        var png = SyntheticPng.Build(4, 4);
        Assert.Equal(png, LocalAlbumArtReader.EmbeddedIn(SongWithPicture(png, "\u00FF\u00D8\u00FF\u00E0"))!.Bytes);
    }

    private string SongWithPicture(byte[] picture, string description)
    {
        var path = SyntheticWav.CreateFile(_directory, $"{Guid.NewGuid():N}.wav", TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));
        using var file = TagLib.File.Create(path);
        file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(picture)) { Description = description }];
        file.Save();
        return path;
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
