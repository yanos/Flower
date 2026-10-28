using System.Collections.Generic;
using System.Linq;

using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// Which of Deezer's artists is the library's, for their picture. The shapes
// below are Deezer's own, trimmed, as /search/artist and /artist/<id>/albums
// answered them in October 2026 - including the two real collisions that make
// a name not enough: Nirvana beside Nirvana (UK), and two acts called Ohmme.
public sealed class DeezerArtistSearchTests
{
    private const string NirvanaSearch = """
        {"data":[
          {"id":281527041,"name":"Nirvana (UK)","picture_xl":"https://cdn-images.dzcdn.net/images/artist/ad67af8b0dca71d5e69f1afd8e045c4e/1000x1000-000000-80-0-0.jpg"},
          {"id":415,"name":"Nirvana","picture_xl":"https://cdn-images.dzcdn.net/images/artist/3ec5542ff520ee74e2befdaba32ef2ef/1000x1000-000000-80-0-0.jpg"},
          {"id":1429039,"name":"Approaching Nirvana","picture_xl":"https://cdn-images.dzcdn.net/images/artist/1c7d2e357e657f89dc2e50e41dd04a92/1000x1000-000000-80-0-0.jpg"}
        ],"total":3}
        """;

    [Fact]
    public void A_search_is_read_as_each_artist_and_their_largest_picture()
    {
        var found = DeezerArtistSearch.ParseArtists(NirvanaSearch);

        Assert.Equal([281527041L, 415L, 1429039L], found.Select(a => a.Id));
        Assert.Equal("Nirvana", found[1].Name);
        Assert.EndsWith("/1000x1000-000000-80-0-0.jpg", found[1].PictureUrl);
    }

    [Fact]
    public void An_artist_Deezer_has_no_photo_of_has_no_picture_rather_than_the_silhouette()
    {
        var found = DeezerArtistSearch.ParseArtists("""
            {"data":[
              {"id":1,"name":"Empty Hash","picture_xl":"https://e-cdns-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg"},
              {"id":2,"name":"Hash Of Nothing","picture_xl":"https://e-cdns-images.dzcdn.net/images/artist/d41d8cd98f00b204e9800998ecf8427e/1000x1000-000000-80-0-0.jpg"},
              {"id":3,"name":"No Field"}
            ]}
            """);

        Assert.All(found, a => Assert.Null(a.PictureUrl));
    }

    [Fact]
    public void Deezer_refusing_is_not_read_as_finding_nobody()
    {
        // Its answer to the fifty-first request in five seconds - an HTTP 200.
        const string quota = """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""";

        Assert.Throws<DeezerRefusedException>(() => DeezerArtistSearch.ParseArtists(quota));
        Assert.Throws<DeezerRefusedException>(() => DeezerArtistSearch.ParseAlbumTitles(quota));
    }

    [Fact]
    public void Only_artists_named_as_the_library_names_them_are_looked_into()
    {
        var candidates = DeezerArtistSearch.Candidates(DeezerArtistSearch.ParseArtists(NirvanaSearch), "nirvana");

        // "(UK)" is bracketed and normalises away, so both Nirvanas are in;
        // a name that merely contains it is not.
        Assert.Equal([281527041L, 415L], candidates.Select(a => a.Id));
    }

    [Fact]
    public void Of_two_artists_with_one_name_the_one_whose_albums_the_library_has_is_chosen()
    {
        var candidates = DeezerArtistSearch.Candidates(DeezerArtistSearch.ParseArtists(NirvanaSearch), "Nirvana");
        IReadOnlyList<IReadOnlyList<string>> albums =
        [
            ["Local Anaesthetic", "Dedicated To Markos III"],
            ["Nevermind (Remastered)", "In Utero (20th Anniversary Remaster)", "MTV Unplugged In New York"],
        ];

        var chosen = DeezerArtistSearch.Choose(candidates, albums, ["Nevermind", "In Utero"]);

        Assert.Equal(415, chosen?.Id);
    }

    [Fact]
    public void An_album_whose_name_holds_the_librarys_counts_as_that_album()
    {
        DeezerArtistSearch.Artist radiohead = new(399, "Radiohead", "https://example.test/r.jpg");

        var chosen = DeezerArtistSearch.Choose([radiohead], [["OK Computer OKNOTOK 1997 2017"]], ["OK Computer"]);

        Assert.Equal(399, chosen?.Id);
    }

    [Fact]
    public void A_short_name_inside_a_longer_one_is_not_the_same_album()
    {
        DeezerArtistSearch.Artist other = new(7, "Up", "https://example.test/u.jpg");

        Assert.Null(DeezerArtistSearch.Choose([other], [["Upstairs at Eric's"]], ["Up"]));
    }

    [Fact]
    public void A_namesake_sharing_none_of_the_librarys_albums_is_no_picture_at_all()
    {
        DeezerArtistSearch.Artist ohmme = new(1, "Ohmme", "https://example.test/o.jpg");

        // A picture of somebody else is worse than the covers.
        Assert.Null(DeezerArtistSearch.Choose([ohmme], [["Somebody Else's Record"]], ["Parts", "Fantasy"]));
    }

    [Fact]
    public void An_artist_with_no_album_in_the_library_takes_a_single_exact_namesake()
    {
        DeezerArtistSearch.Artist only = new(1, "Ohmme", "https://example.test/o.jpg");
        DeezerArtistSearch.Artist other = new(2, "Ohmme", "https://example.test/p.jpg");

        // Nothing to compare: one of that name is taken, two cannot be told apart.
        Assert.Equal(1, DeezerArtistSearch.Choose([only], [], [])?.Id);
        Assert.Null(DeezerArtistSearch.Choose([only, other], [], []));
    }
}
