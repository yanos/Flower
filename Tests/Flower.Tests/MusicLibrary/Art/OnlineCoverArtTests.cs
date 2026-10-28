using System.Linq;

using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// Find Online's half that is not the network: reading what the iTunes search,
// MusicBrainz, the Cover Art Archive and Bandcamp answer with, what is asked of them,
// and deciding which of the results are the album being looked for. A search
// by name answers with everything sharing a word with it, so what this leaves
// out matters as much as the order.
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

    [Theory]
    [InlineData("https://is1-ssl.mzstatic.com/image/thumb/Music/source/1400x1400bb.jpg", "mzstatic.com")]
    [InlineData("https://coverartarchive.org/release/abc/123-1200.jpg", "coverartarchive.org")]
    [InlineData("not a url", null)]
    public void Names_the_domain_a_cover_comes_from(string url, string? domain) =>
        Assert.Equal(domain, OnlineCoverArt.SourceDomain(url));

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
        Assert.Equal(MetadataNames.Normalize("Björk"), MetadataNames.Normalize("bjork"));
        Assert.Equal(MetadataNames.Normalize("Sgt. Pepper's Lonely Hearts Club Band"),
                     MetadataNames.Normalize("Sgt Peppers Lonely Hearts Club Band"));
        Assert.Equal(MetadataNames.Normalize("Hounds of Love - EP"), MetadataNames.Normalize("Hounds of Love"));
    }

    [Fact]
    public void A_name_that_is_all_brackets_still_has_one()
    {
        Assert.Equal("untitled", MetadataNames.Normalize("[Untitled]"));
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

    // A wide cover can be smaller than a square one, and a picture whose size
    // cannot be read is still offered, after the rest; equal sizes keep the
    // order Rank gave them.
    [Fact]
    public void The_cover_with_the_most_pixels_is_offered_first()
    {
        OnlineCoverArt.Download Found(string name, byte[] bytes) => new(Album(name, "Fleetwood Mac"), bytes);

        var largest = OnlineCoverArt.Largest(
        [
            Found("unreadable", [1, 2, 3]),
            Found("wide", SyntheticPng.Build(60, 20)),
            Found("square", SyntheticPng.Build(40, 40)),
            Found("also square", SyntheticPng.Build(40, 40)),
            Found("small", SyntheticPng.Build(10, 10)),
        ]);

        Assert.Equal(["square", "also square", "wide", "small", "unreadable"], largest.Select(d => d.Candidate.Album));
    }

    [Fact]
    public void Nothing_to_search_for_finds_nothing()
    {
        Assert.Empty(OnlineCoverArt.Rank([Album("Rumours", "Fleetwood Mac")], "  ", "Fleetwood Mac"));
    }

    // What a rip adds to an album's name is not part of it, and a store's
    // search wants every word it is given: "69 Love Songs Disc 1" found
    // nothing anywhere.
    [Fact]
    public void A_disc_number_is_not_part_of_the_album()
    {
        Assert.Equal("69 Love Songs", MetadataNames.BareName("69 Love Songs Disc 1"));
        Assert.Equal("The Wall", MetadataNames.BareName("The Wall (CD 2 of 2)"));
        Assert.Equal("Tome 8", MetadataNames.BareName("Tome 8"));
        Assert.Single(OnlineCoverArt.Rank([Album("69 Love Songs", "The Magnetic Fields")], "69 Love Songs Disc 1", "Magnetic Fields"));
    }

    [Fact]
    public void Reads_release_groups_with_their_credited_artists()
    {
        const string json = """
            {"release-groups":[
              {"id":"4e765614","title":"Transit of Venus","artist-credit":[
                {"name":"Hangedup","joinphrase":" & "},{"name":"Tony Conrad"}]},
              {"title":"No id"}
            ]}
            """;

        var group = Assert.Single(OnlineCoverArt.ParseMusicBrainz(json));
        Assert.Equal("Transit of Venus", group.Album);
        Assert.Equal("Hangedup & Tony Conrad", group.Artist);
        Assert.Equal("https://coverartarchive.org/release-group/4e765614", group.ArtworkUrl);
    }

    [Fact]
    public void Takes_the_front_cover_from_the_archive_at_its_large_size_over_https()
    {
        const string json = """
            {"images":[
              {"front":false,"image":"http://a.test/back.jpg","thumbnails":{"1200":"http://a.test/back-1200.jpg"}},
              {"front":true,"image":"http://a.test/front.jpg","thumbnails":{"250":"http://a.test/front-250.jpg","1200":"http://a.test/front-1200.jpg"}}
            ]}
            """;

        Assert.Equal("https://a.test/front-1200.jpg", OnlineCoverArt.ParseCoverArtListing(json));
        Assert.Null(OnlineCoverArt.ParseCoverArtListing("""{"images":[{"front":false,"image":"https://a.test/b.jpg"}]}"""));
    }

    // Bandcamp's answer carries a 100-pixel thumbnail; the cover is asked for
    // by its art id at 1200 instead. Its search also lists artists, labels
    // and tracks when not told otherwise, none of which is an album's cover.
    [Fact]
    public void Reads_bandcamp_albums_at_their_large_size()
    {
        const string json = """
            {"auto":{"results":[
              {"type":"a","art_id":1319487325,"name":"Transit Of Venus","band_name":"Hangedup & Tony Conrad",
               "img":"https://f4.bcbits.com/img/1319487325_3.jpg"},
              {"type":"b","art_id":42,"name":"Constellation"},
              {"type":"a","art_id":null,"name":"No art"}
            ]}}
            """;

        var album = Assert.Single(OnlineCoverArt.ParseBandcamp(json));
        Assert.Equal("Transit Of Venus", album.Album);
        Assert.Equal("Hangedup & Tony Conrad", album.Artist);
        Assert.Equal("https://f4.bcbits.com/img/a1319487325_10.jpg", album.ArtworkUrl);
        Assert.Equal("bcbits.com", OnlineCoverArt.SourceDomain(album.ArtworkUrl));
    }

    // MusicBrainz indexes a credit word by word, so the album and its lead
    // artist go as phrases and the rest of a credit stays out of it.
    [Fact]
    public void Asks_musicbrainz_for_the_album_by_its_lead_artist()
    {
        Assert.Equal("releasegroup:\"Transit Of Venus\" AND artist:\"Hangedup\"",
            OnlineCoverArt.MusicBrainzQuery("Transit Of Venus", "Hangedup and Tony Conrad"));
        Assert.Equal("releasegroup:\"69 Love Songs\" AND artist:\"Takashi Kokubo\"",
            OnlineCoverArt.MusicBrainzQuery("69 Love Songs Disc 2", "Takashi Kokubo (小久保隆)"));
        Assert.Equal("releasegroup:\"Say \\\"Hi\\\"\"", OnlineCoverArt.MusicBrainzQuery("Say \"Hi\"", ""));
    }

    [Fact]
    public void The_lead_artist_is_the_first_of_a_credit()
    {
        Assert.Equal("Daft Punk", MetadataNames.LeadArtist("Daft Punk, Pharrell Williams"));
        Assert.Equal("Simon", MetadataNames.LeadArtist("Simon & Garfunkel"));
        Assert.Equal("Kendrick Lamar", MetadataNames.LeadArtist("Kendrick Lamar feat. SZA"));
        Assert.Equal("Feathers", MetadataNames.LeadArtist("Feathers"));
    }
}
