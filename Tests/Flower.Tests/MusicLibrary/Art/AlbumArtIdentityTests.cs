using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.MusicLibrary.Art;

[Collection("PlatformDataDirectory")]
public class AlbumArtIdentityTests : PinnedDataDirectory
{
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private string Dir(string name)
    {
        var path = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // A real WAV carrying a real embedded picture, read back through real
    // TagLib# - the same path Importer and LocalAlbumArtReader take.
    private static Track LocalTrack(string directory, string fileName, string album, byte[]? picture)
    {
        var path = SyntheticWav.CreateFile(directory, fileName, TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));

        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Album = album;
            file.Tag.AlbumArtists = ["Album Artist"];
            if (picture != null)
                file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(picture)) { MimeType = "image/png" }];

            file.Save();
        }

        return new Track { Path = path, Album = album, AlbumArtists = "Album Artist" };
    }
    private static Track RemoteTrack(string? hash, string? fingerprint = "peer-fp") => new()
    {
        Path = null,
        Album = Unique("Remote"),
        OriginAlbumArtId = hash,
        OriginDeviceFingerprint = fingerprint,
    };

    // Track Info over several songs shows their cover only when it is theirs
    // to share - one song without any is enough to make it Multiple Album Art.
    [Fact]
    public void Songs_share_one_cover_only_when_every_one_carries_the_same_picture()
    {
        var picture = SyntheticPng.Build(64, 64);
        var a = LocalTrack(Dir("share-a"), "a.wav", Unique("A"), picture);
        var b = LocalTrack(Dir("share-b"), "b.wav", Unique("B"), picture);
        var other = LocalTrack(Dir("share-c"), "c.wav", Unique("C"), SyntheticPng.Build(96, 96));
        var none = LocalTrack(Dir("share-d"), "d.wav", Unique("D"), picture: null);
        var alsoNone = LocalTrack(Dir("share-e"), "e.wav", Unique("E"), picture: null);

        Assert.True(AlbumArtLoader.ShareOneCover([a, b]));
        Assert.False(AlbumArtLoader.ShareOneCover([a, b, other]));
        Assert.False(AlbumArtLoader.ShareOneCover([a, none]));
        Assert.True(AlbumArtLoader.ShareOneCover([none, alsoNone]));
    }

    // A server's songs name their cover by album, and are compared by that name.
    [Fact]
    public void Songs_of_the_server_share_one_cover_when_they_name_the_same_one()
    {
        var album = Unique("album");

        Assert.True(AlbumArtLoader.ShareOneCover([RemoteTrack(album), RemoteTrack(album)]));
        Assert.False(AlbumArtLoader.ShareOneCover([RemoteTrack(album), RemoteTrack(Unique("album"))]));
    }
    [Fact]
    public void TryGetLocalArtBytes_returns_the_embedded_picture_bytes()
    {
        var picture = SyntheticPng.Build(32, 32);
        var track = LocalTrack(Dir("bytes"), "bytes.wav", Unique("Album"), picture);

        Assert.Equal(picture, AlbumArtLoader.TryGetLocalArtBytes(track));
    }
}
