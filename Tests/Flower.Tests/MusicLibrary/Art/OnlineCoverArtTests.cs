using System.Linq;

using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// Find Online's half that is not the network: reading what the iTunes search
// answers with, and deciding which of its results are the album being looked
// for. A search by name answers with everything sharing a word with it, so
// what this leaves out matters as much as the order.
public class OnlineCoverArtTests
{
    private static OnlineCoverArt.Candidate Album(string name, string artist, string art = "") =>
        new(name, artist, art.Length > 0 ? art : $"https://example.test/{name}/{artist}/1400x1400bb.jpg");

    [Fact]
    public void Asks_for_the_artwork_at_the_requested_size()
    {
        var url = OnlineCoverArt.LargeArtworkUrl(
            "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/ab/cd/100x100/source/100x100bb.jpg");

        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Music/v4/ab/cd/100x100/source/1400x1400bb.jpg", url);
    }

    [Fact]
    public void Reads_album_results_and_skips_those_without_artwork()
    {
        const string json = """
            {"resultCount":2,"results":[
              {"collectionName":"Rumours","artistName":"Fleetwood Mac","artworkUrl100":"https://a.test/x/100x100bb.jpg"},
              {"collectionName":"Tusk","artistName":"Fleetwood Mac"}
            ]}
            """;

        var found = OnlineCoverArt.Parse(json, byAlbum: true);

        var only = Assert.Single(found);
        Assert.Equal("Rumours", only.Album);
        Assert.Equal("Fleetwood Mac", only.Artist);
        Assert.Equal("https://a.test/x/1400x1400bb.jpg", only.ArtworkUrl);
    }

    [Fact]
    public void A_song_search_is_matched_on_the_song_name()
    {
        const string json = """
            {"results":[{"trackName":"Dreams","collectionName":"Rumours","artistName":"Fleetwood Mac","artworkUrl100":"https://a.test/100x100bb.jpg"}]}
            """;

        Assert.Equal("Dreams", Assert.Single(OnlineCoverArt.Parse(json, byAlbum: false)).Album);
    }

    [Fact]
    public void Leaves_out_results_that_do_not_name_the_album()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Greatest Hits", "Fleetwood Mac"), Album("Rumours", "Fleetwood Mac")],
            "Rumours", "Fleetwood Mac");

        Assert.Equal(["Rumours"], ranked.Select(c => c.Album));
    }

    [Fact]
    public void The_artists_own_album_comes_before_another_artists_of_the_same_name()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Rumours", "A Tribute Band"), Album("Rumours", "Fleetwood Mac")],
            "Rumours", "Fleetwood Mac");

        Assert.Equal(["Fleetwood Mac", "A Tribute Band"], ranked.Select(c => c.Artist));
    }

    [Fact]
    public void A_store_edition_is_the_same_album()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Rumours (Super Deluxe Edition) [Remastered]", "Fleetwood Mac")],
            "rumours", "fleetwood mac");

        Assert.Single(ranked);
    }

    [Fact]
    public void Accents_and_punctuation_do_not_separate_names()
    {
        Assert.Equal(OnlineCoverArt.Normalize("Björk"), OnlineCoverArt.Normalize("bjork"));
        Assert.Equal(OnlineCoverArt.Normalize("Sgt. Pepper's Lonely Hearts Club Band"),
                     OnlineCoverArt.Normalize("Sgt Peppers Lonely Hearts Club Band"));
        Assert.Equal(OnlineCoverArt.Normalize("Hounds of Love - EP"), OnlineCoverArt.Normalize("Hounds of Love"));
    }

    [Fact]
    public void A_name_that_is_all_brackets_still_has_one()
    {
        Assert.Equal("untitled", OnlineCoverArt.Normalize("[Untitled]"));
    }

    [Fact]
    public void The_same_artwork_twice_is_offered_once()
    {
        const string art = "https://a.test/same/1400x1400bb.jpg";
        var ranked = OnlineCoverArt.Rank(
            [Album("Rumours", "Fleetwood Mac", art), Album("Rumours", "Fleetwood Mac", art)],
            "Rumours", "Fleetwood Mac");

        Assert.Single(ranked);
    }

    [Fact]
    public void Nothing_to_search_for_finds_nothing()
    {
        Assert.Empty(OnlineCoverArt.Rank([Album("Rumours", "Fleetwood Mac")], "  ", "Fleetwood Mac"));
    }
}
