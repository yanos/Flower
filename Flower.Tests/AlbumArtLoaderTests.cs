using System;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// AlbumArtLoader had zero coverage (docs/ARCHITECTURE-REVIEW.md §5.4) despite
// carrying more "confirmed on a real device" bug narratives than anything else
// in the codebase: the cache-key collision that made every downloaded track
// show the same wrong cover, and the unguarded decode that turned one corrupt
// embedded picture into an unobserved task fault instead of a placeholder icon.
//
// These are [AvaloniaFact], not [Fact]: Avalonia.Media.Imaging.Bitmap needs a
// platform, and the headless platform is configured with real Skia drawing
// (see TestAppBuilder) precisely so decoding here is genuine - headless's own
// drawing stub reports every image as 1x1 whatever bytes it is handed, which
// would make the scaling and corrupt-input cases assert nothing.
//
// Not covered: the actual peer HTTP fetch. AlbumArtLoader is a static class
// that service-locates PeerTrackResolver/DeviceIdentity out of the
// process-wide Ioc.Default (§2.3), so a test cannot give it a peer pointed at
// a local fake server without fixing the whole process's container for the
// run. The branches on either side of that call - disk cache hit, and no
// resolvable peer - are covered below.
[Collection("PlatformDataDirectory")]
public class AlbumArtLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-albumart-tests").FullName;
    private readonly string? _previousDataDirectory;

    public AlbumArtLoaderTests()
    {
        TestIoc.EnsureConfigured();

        // AlbumArtLoader's remote disk cache lives under AppDataDirectory -
        // unpinned, these tests would write into the real library folder.
        _previousDataDirectory = PlatformDataDirectory.Current;
        PlatformDataDirectory.Current = Path.Combine(_root, "appdata");
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = _previousDataDirectory;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    // The cache is static and lives for the whole test run, so every test uses
    // album/hash names unique to itself rather than sharing fixtures.
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
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

    [AvaloniaFact]
    public async Task Embedded_album_art_is_decoded_and_returned()
    {
        var track = LocalTrack(_root, "embedded.wav", Unique("Album"), SyntheticPng.Build(120, 120));

        var bitmap = await AlbumArtLoader.LoadAsync(track);

        Assert.NotNull(bitmap);
        Assert.Equal(120, bitmap.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task A_track_with_no_embedded_art_and_no_cover_file_returns_null()
    {
        var track = LocalTrack(Dir("bare"), "bare.wav", Unique("Album"), picture: null);

        Assert.Null(await AlbumArtLoader.LoadAsync(track));
    }

    [AvaloniaFact]
    public async Task Art_the_decoder_cannot_read_falls_back_to_null_rather_than_throwing()
    {
        // Not an image at all: the corrupt/truncated/CMYK-JPEG embedded picture
        // that used to fault out of Task.Run unobserved.
        var track = LocalTrack(Dir("corrupt"), "corrupt.wav", Unique("Album"), [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3]);

        Assert.Null(await AlbumArtLoader.LoadAsync(track));
    }

    [AvaloniaFact]
    public async Task Oversized_art_is_decoded_down_to_the_maximum_painted_size()
    {
        var track = LocalTrack(Dir("large"), "large.wav", Unique("Album"), SyntheticPng.Build(1400, 1400));

        var bitmap = await AlbumArtLoader.LoadAsync(track);

        // MaxArtPixels - a 1400x1400 cover is ~7.8 MB of decoded RGBA if kept
        // whole, per album, for the life of the cache entry (Tier 1.2).
        Assert.Equal(768, bitmap!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task Art_smaller_than_the_maximum_is_never_scaled_up()
    {
        var track = LocalTrack(Dir("small"), "small.wav", Unique("Album"), SyntheticPng.Build(300, 300));

        var bitmap = await AlbumArtLoader.LoadAsync(track);

        Assert.Equal(300, bitmap!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task Two_albums_sharing_one_directory_do_not_share_one_cover()
    {
        // Exactly the downloaded-library layout: every downloaded file lands in
        // one flat folder regardless of album. Keyed by directory, the second
        // track here returned the first one's cover - visible in the app as
        // Recently Added's tiles all showing the most recent download's art.
        var downloads = Dir("downloads");
        var first = LocalTrack(downloads, "first.wav", Unique("First"), SyntheticPng.Build(64, 64));
        var second = LocalTrack(downloads, "second.wav", Unique("Second"), SyntheticPng.Build(96, 96));

        var firstArt = await AlbumArtLoader.LoadAsync(first);
        var secondArt = await AlbumArtLoader.LoadAsync(second);

        Assert.Equal(64, firstArt!.PixelSize.Width);
        Assert.Equal(96, secondArt!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task Two_tracks_of_one_album_share_a_single_decoded_bitmap()
    {
        var album = Unique("Shared");
        var first = LocalTrack(Dir("disc-one"), "a.wav", album, SyntheticPng.Build(64, 64));
        var second = LocalTrack(Dir("disc-two"), "b.wav", album, SyntheticPng.Build(64, 64));

        var firstArt = await AlbumArtLoader.LoadAsync(first);
        var secondArt = await AlbumArtLoader.LoadAsync(second);

        // Same instance, not merely an equivalent one: the cache exists so a
        // 40-tile grid decodes each cover once.
        Assert.Same(firstArt, secondArt);
    }

    [AvaloniaFact]
    public async Task Tracks_with_a_blank_album_tag_fall_back_to_keying_on_their_directory()
    {
        var first = LocalTrack(Dir("untagged-one"), "a.wav", album: "", SyntheticPng.Build(64, 64));
        var second = LocalTrack(Dir("untagged-two"), "b.wav", album: "", SyntheticPng.Build(96, 96));

        var firstArt = await AlbumArtLoader.LoadAsync(first);
        var secondArt = await AlbumArtLoader.LoadAsync(second);

        Assert.Equal(64, firstArt!.PixelSize.Width);
        Assert.Equal(96, secondArt!.PixelSize.Width);
    }

    // --- placeholder (synced, Path == null) tracks ---

    private static Track RemoteTrack(string? hash, string? fingerprint = "peer-fp") => new()
    {
        Path = null,
        Album = Unique("Remote"),
        OriginAlbumArtHash = hash,
        OriginDeviceFingerprint = fingerprint,
    };

    private void WriteArtCache(string hash, byte[] bytes)
    {
        var directory = Path.Combine(AppDataDirectory.Path, "AlbumArtCache");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"{hash}.art"), bytes);
    }

    [AvaloniaFact]
    public async Task A_placeholder_track_with_no_art_hash_is_not_fetched_at_all()
    {
        Assert.Null(await AlbumArtLoader.LoadAsync(RemoteTrack(hash: null)));
        Assert.Null(await AlbumArtLoader.LoadAsync(RemoteTrack(hash: "")));
    }

    [AvaloniaFact]
    public async Task A_placeholder_track_with_no_origin_device_is_not_fetched_at_all()
    {
        Assert.Null(await AlbumArtLoader.LoadAsync(RemoteTrack(Unique("hash"), fingerprint: null)));
    }

    [AvaloniaFact]
    public async Task Remote_art_already_on_disk_is_decoded_from_the_cache()
    {
        // Content-addressed by hash, so this survives a restart and the origin
        // peer being offline - which is why it is checked before any peer
        // resolution, and why this test needs no peer at all.
        var hash = Unique("hash");
        WriteArtCache(hash, SyntheticPng.Build(200, 200));

        var bitmap = await AlbumArtLoader.LoadAsync(RemoteTrack(hash));

        Assert.Equal(200, bitmap!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task A_corrupt_cached_art_file_falls_through_to_the_peer_instead_of_throwing()
    {
        var hash = Unique("hash");
        WriteArtCache(hash, [0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9]);

        // No peer is resolvable in tests, so the fall-through ends in null -
        // the placeholder icon - rather than a decode exception escaping.
        Assert.Null(await AlbumArtLoader.LoadAsync(RemoteTrack(hash)));
    }

    [AvaloniaFact]
    public async Task Remote_art_is_cached_in_memory_by_hash_across_tracks()
    {
        var hash = Unique("hash");
        WriteArtCache(hash, SyntheticPng.Build(200, 200));

        var first = await AlbumArtLoader.LoadAsync(RemoteTrack(hash));
        // A different album on a different origin device: the hash is the key,
        // because identical art bytes are identical art.
        var second = await AlbumArtLoader.LoadAsync(RemoteTrack(hash, fingerprint: "other-peer-fp"));

        Assert.Same(first, second);
    }

    [Fact]
    public void ComputeArtHash_is_lowercase_hex_and_content_addressed()
    {
        var hash = AlbumArtLoader.ComputeArtHash([1, 2, 3]);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, AlbumArtLoader.ComputeArtHash([1, 2, 3]));
        Assert.NotEqual(hash, AlbumArtLoader.ComputeArtHash([1, 2, 4]));
    }

    [Fact]
    public void TryGetLocalArtBytes_returns_the_embedded_picture_bytes()
    {
        var picture = SyntheticPng.Build(32, 32);
        var track = LocalTrack(Dir("bytes"), "bytes.wav", Unique("Album"), picture);

        Assert.Equal(picture, AlbumArtLoader.TryGetLocalArtBytes(track));
    }
}
