using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The client's half of the one identity scheme (ARCHITECTURE-REVIEW Tier 2.1);
// the server's half is Flower.Server.Tests' IdentityParityTests, and the two
// projects meet at this class rather than at each other.
//
// These used to live in LibraryOpenSubsonicMapperTests, against a mapper that
// was written for the app's own embedded host and outlived it with no callers
// left. What the app still asks of this is the album id it addresses cover art
// by - see ICoverArtUrlResolver - so that is what is pinned here.
public class CatalogIdentityTests
{
    // A compilation: one album artist, many track artists. Deriving the id from
    // Artists fragments the album into one id per track, and the grouped id is
    // then one nothing ever hands out - which is exactly how remote cover art
    // for every compilation used to 404.
    [Fact]
    public void AlbumIdFor_is_the_album_artist_not_the_track_artist()
    {
        var first = new Track
        {
            Title = "One", Artists = "Artist A", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/one.mp3",
        };
        var second = new Track
        {
            Title = "Two", Artists = "Artist B", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/two.mp3",
        };

        Assert.Equal(CatalogIdentity.AlbumIdFor(first), CatalogIdentity.AlbumIdFor(second));
        Assert.Equal(CatalogIdentity.AlbumId("Various Artists", "Compilation"), CatalogIdentity.AlbumIdFor(first));
    }

    // The other branch of EffectiveAlbumArtist: flagged a compilation with the
    // AlbumArtists tag left blank, which must still land every track on one id.
    [Fact]
    public void AlbumIdFor_holds_a_compilation_together_with_no_AlbumArtists_tag()
    {
        var first = new Track
        {
            Title = "One", Artists = "Artist A", IsCompilation = true,
            Album = "Compilation", Path = "/music/one.mp3",
        };
        var second = new Track
        {
            Title = "Two", Artists = "Artist B", IsCompilation = true,
            Album = "Compilation", Path = "/music/two.mp3",
        };

        Assert.Equal(CatalogIdentity.AlbumIdFor(first), CatalogIdentity.AlbumIdFor(second));
    }

    [Theory]
    [InlineData("Beatles", "Abbey Road")]
    [InlineData("  BEATLES  ", "abbey road")] // Normalized: trimmed and lowercased.
    [InlineData("A|B", "C")] // The old plain-text form embedded this separator into the id itself.
    public void Ids_are_opaque_and_normalized(string artist, string album)
    {
        var id = CatalogIdentity.AlbumId(artist, album);

        Assert.Equal(CatalogIdentity.AlbumId(artist.Trim().ToUpperInvariant(), album.ToUpperInvariant()), id);
        Assert.StartsWith("al-", id);
        Assert.DoesNotContain(artist.Trim().ToLowerInvariant(), id);
        Assert.NotEqual(CatalogIdentity.AlbumId(album, artist), id); // Argument order is meaningful.
    }

    // A song's artist id has to point at an artist the album listing mentions,
    // which for a compilation is the album artist and never the track's own.
    [Fact]
    public void ArtistId_for_a_compilation_track_is_the_album_artist()
    {
        var track = new Track
        {
            Title = "One", Artists = "Artist A", AlbumArtists = "Various Artists",
            Album = "Compilation", Path = "/music/one.mp3",
        };

        Assert.Equal(
            CatalogIdentity.ArtistId("Various Artists"),
            CatalogIdentity.ArtistId(track.EffectiveAlbumArtist));
    }
}
