using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Server.Services;

namespace Flower.Server.Tests.MusicLibrary;

// The server remembers where each id's cover art is, rather than reading every
// song's tags and listing their folder on every request
// (docs/CODE-REVIEW-2026-10.md C2). What a cache like that can get wrong is
// serving a picture that has changed or gone, or keeping a "no art" after
// art has arrived - so that is what these are about. The songs are not audio, so
// TagLib finds no embedded picture and the folder's cover.jpg is the art.
public sealed class CoverArtCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "flower-covers-" + Guid.NewGuid().ToString("N"));

    public CoverArtCacheTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Track Song(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return new Track { Path = path, Title = name, Album = "Doolittle", AlbumArtists = "Pixies", Artists = "Pixies" };
    }

    private void WriteCover(byte[] bytes, DateTime writtenAt)
    {
        var cover = Path.Combine(_folder, "cover.jpg");
        File.WriteAllBytes(cover, bytes);
        File.SetLastWriteTimeUtc(cover, writtenAt);
    }

    private static CoverArtCache CacheOver(Library library) => new(library, NullLogger<CoverArtCache>.Instance);

    [Fact]
    public void An_album_id_finds_the_cover_in_its_folder_and_notices_when_it_changes()
    {
        var library = new Library([Song("01 Debaser.mp3"), Song("02 Tame.mp3")]);
        var albumId = library.Snapshot.AlbumsById.Keys.Single();
        using var covers = CacheOver(library);
        WriteCover([0xFF, 0xD8, 1], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal([0xFF, 0xD8, 1], covers.Find(albumId)!.Bytes);

        WriteCover([0xFF, 0xD8, 2, 2], new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal([0xFF, 0xD8, 2, 2], covers.Find(albumId)!.Bytes);
    }

    // Nothing tells the server a cover was dropped into a folder, so the
    // folder itself is what is checked: adding a file to it moves its
    // modified time.
    [Fact]
    public void A_cover_added_to_a_folder_that_had_none_is_found_at_the_next_request()
    {
        var song = Song("01 Debaser.mp3");
        var library = new Library([song]);
        using var covers = CacheOver(library);

        Assert.Null(covers.Find(song.Id.ToKey()));

        WriteCover([0xFF, 0xD8, 1], DateTime.UtcNow);

        Assert.Equal([0xFF, 0xD8, 1], covers.Find(song.Id.ToKey())!.Bytes);
    }

    [Fact]
    public void A_deleted_cover_is_no_longer_served()
    {
        var song = Song("01 Debaser.mp3");
        var library = new Library([song]);
        using var covers = CacheOver(library);
        WriteCover([0xFF, 0xD8, 1], DateTime.UtcNow);
        Assert.NotNull(covers.Find(song.Id.ToKey()));

        File.Delete(Path.Combine(_folder, "cover.jpg"));

        Assert.Null(covers.Find(song.Id.ToKey()));
    }

    // The library's change token moves on every play, which is why the cache
    // is not keyed on it: a house full of listeners would empty it constantly.
    [Fact]
    public void A_play_forgets_nothing()
    {
        var song = Song("01 Debaser.mp3");
        var library = new Library([song]);
        using var covers = CacheOver(library);
        WriteCover([0xFF, 0xD8, 1], DateTime.UtcNow);
        covers.Find(song.Id.ToKey());

        library.NotifyTrackChanged(song, TrackChange.PlayFinished);

        Assert.Equal(1, covers.Count);
    }
}
