using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// Where every artist's picture or covers come from, for every place that
// draws one (ArtistIcons).
public class ArtistIconsTests
{
    private static Track Song(string title, string album, string artist, int plays = 0) => new()
    {
        Title = title, Album = album, Artists = artist, PlayCount = plays,
        Path = $"/music/{artist}/{album}/{title}.mp3",
    };

    // Made of the artist's albums alone, most played first, and the same
    // icon however the name is cased, as the library counts them one artist.
    [AvaloniaFact]
    public void An_artists_icon_is_made_of_their_own_albums()
    {
        var icons = new ArtistIcons();
        icons.LibraryChanged([Song("a", "Quiet", "X"), Song("b", "Loud", "X", plays: 5), Song("c", "Other's", "Y")]);

        var icon = icons.For("X", thumbnail: true);

        Assert.Equal(new[] { "Loud", "Quiet" }, icon.AlbumsInPlayOrder.Select(t => t.Album));
        Assert.Same(icon, icons.For("x", thumbnail: true));
    }

    // A row's and a header's are two sizes of one icon, from the same songs.
    [AvaloniaFact]
    public void A_rows_icon_and_a_headers_are_of_the_same_albums()
    {
        var icons = new ArtistIcons();
        icons.LibraryChanged([Song("a", "One", "X"), Song("b", "Two", "X", plays: 3)]);

        Assert.Equal(
            icons.For("X", thumbnail: false).AlbumsInPlayOrder,
            icons.For("X", thumbnail: true).AlbumsInPlayOrder);
    }

    [AvaloniaFact]
    public void A_name_with_no_songs_has_an_empty_icon()
    {
        var icons = new ArtistIcons();
        icons.LibraryChanged([Song("a", "One", "X")]);

        Assert.Same(AlbumCollage.Empty, icons.For("Nobody", thumbnail: true));
    }

    // An album added since is in the icon drawn after the library changes,
    // rather than the one made before it.
    [AvaloniaFact]
    public void An_artists_icon_follows_the_library()
    {
        var icons = new ArtistIcons();
        icons.LibraryChanged([Song("a", "One", "X")]);
        var before = icons.For("X", thumbnail: true);

        icons.LibraryChanged([Song("a", "One", "X"), Song("b", "Two", "X")]);

        Assert.NotSame(before, icons.For("X", thumbnail: true));
        Assert.Equal(2, icons.For("X", thumbnail: true).AlbumsInPlayOrder.Count);
    }
}
