using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// An album is drawn with the first of its songs, in disc and track order,
// that has a picture - not whichever was added last, which could be a bonus
// track carrying a cover of its own.
public class AlbumCoverTests
{
    private static Track Song(int disc, int number, int art) =>
        new() { Title = $"{disc}-{number}", Album = "Album", DiscNumber = (uint)disc, TrackNumber = (uint)number, ArtWidth = art, ArtHeight = art };

    [Fact]
    public void An_albums_cover_is_its_first_song_with_a_picture()
    {
        var bare = Song(1, 1, art: 0);
        var second = Song(1, 2, art: 600);
        var bonus = Song(2, 1, art: 300);

        Assert.Same(second, AlbumCover.TrackOf([bonus, second, bare]));
    }

    [Fact]
    public void An_album_with_no_picture_is_stood_for_by_its_first_song()
    {
        var first = Song(1, 1, art: 0);

        Assert.Same(first, AlbumCover.TrackOf([Song(1, 2, art: 0), first]));
    }
}
