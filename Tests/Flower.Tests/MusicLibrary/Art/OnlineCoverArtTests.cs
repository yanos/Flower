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
    private static OnlineCoverArt.Candidate Album(string name, string artist, string art = "", int? year = null) =>
        new(name, artist, art.Length > 0 ? art : $"https://example.test/{name}/{artist}/1400x1400bb.jpg", year);

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
              {"collectionName":"Rumours","artistName":"Fleetwood Mac","artworkUrl100":"https://a.test/x/100x100bb.jpg","releaseDate":"1977-02-04T08:00:00Z"},
              {"collectionName":"Tusk","artistName":"Fleetwood Mac"}
            ]}
            """;

        var found = OnlineCoverArt.Parse(json, byAlbum: true);

        var only = Assert.Single(found);
        Assert.Equal("Rumours", only.Album);
        Assert.Equal("Fleetwood Mac", only.Artist);
        Assert.Equal("https://a.test/x/1400x1400bb.jpg", only.ArtworkUrl);
        Assert.Equal(1977, only.Year);
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
            [Album("Songs in the Key of Life", "A Tribute Band"), Album("Songs in the Key of Life", "Stevie Wonder")],
            "Songs in the Key of Life", "Stevie Wonder");

        Assert.Equal(["Stevie Wonder", "A Tribute Band"], ranked.Select(c => c.Artist));
    }

    // A store asked for a short name alone answers with strangers' albums of
    // it, and showing them was worse than finding nothing; so is someone
    // else's album that merely contains the name.
    [Fact]
    public void Another_artists_album_counts_only_by_a_distinctive_name()
    {
        Assert.Empty(OnlineCoverArt.Rank(
            [Album("Blood", "Collective Soul"), Album("Blood - Single", "In This Moment")], "Blood", "The Unsettlers"));
        Assert.Empty(OnlineCoverArt.Rank(
            [Album("Mellon Collie and the Infinite Sadness - Single", "JP Coakley")],
            "Mellon Collie and the Infinite Sadness: Dawn to Dusk", "Smashing Pumpkins"));
        Assert.Single(OnlineCoverArt.Rank(
            [Album("Alastor: Book of Angels, Vol. 21", "Eyvind Kang")], "Alastor - The Book of Angels Vol. 21", "John Zorn"));
        Assert.Single(OnlineCoverArt.Rank([Album("Blood", "Collective Soul")], "Blood", ""));
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

    // Two services seldom write a long name alike: the same words in
    // another order, a volume number with or without its zero, an article or
    // a "plus" one of them leaves out. Every Book of Angels volume in a real
    // library was found and then left out for this.
    [Theory]
    [InlineData("Book of Angels, Vol. 01 - Astaroth", "Astaroth: Book of Angels, Vol. 1")]
    [InlineData("Book of Angels, Vol. 01 - Astaroth", "Astaroth: Book of Angels, Volume 1")]
    [InlineData("Ipos: The Book Of Angels Vol 14", "Ipos: Book of Angels, Vol. 14")]
    [InlineData("Confusion is Sex Plus Kill Yr. Idols", "Confusion Is Sex / Kill Yr. Idols")]
    public void The_same_words_in_another_order_or_form_are_the_same_album(string tag, string found)
    {
        var only = Assert.Single(OnlineCoverArt.Rank([Album(found, "Someone Else")], tag, ""));
        Assert.Equal(found, only.Album);
        Assert.Empty(OnlineCoverArt.Rank([Album("Azazel: Book of Angels, Vol. 2", "Someone Else")], tag, ""));
    }

    // A two-disc album tagged with each disc's own title finds the album.
    [Fact]
    public void A_discs_subtitle_still_finds_its_album()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Mellon Collie and the Infinite Sadness - Single", "JP Coakley"),
             Album("Mellon Collie and the Infinite Sadness", "The Smashing Pumpkins")],
            "Mellon Collie And The Infinite Sadness: Dawn To Dusk", "Smashing Pumpkins");

        Assert.Equal(["The Smashing Pumpkins"], ranked.Select(c => c.Artist));
    }

    [Fact]
    public void One_artist_of_a_credit_is_enough_to_be_the_artists()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Stolas: Book of Angels, Vol. 12", "Another Quintet"),
             Album("Stolas: Book of Angels, Vol. 12", "John Zorn, Masada Quintet & Joe Lovano")],
            "Book Of Angels, Vol. 12 - Stolas", "Masada Quintet Featuring Joe Lovano");

        Assert.Equal("John Zorn, Masada Quintet & Joe Lovano", ranked[0].Artist);
    }

    // Bandcamp lists a compilation under its label, as "V/A - ...".
    [Fact]
    public void A_compilation_is_found_under_its_label()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Mono No Aware", "Ryan Camus"), Album("V/A - mono no aware", "PAN")],
            "Mono No Aware", "Various Artists");

        Assert.Equal(["PAN", "Ryan Camus"], ranked.Select(c => c.Artist));
    }

    // Many albums share a short name; the tag's year tells them apart, and
    // one whose year is not known is not passed over for one that is wrong.
    [Fact]
    public void The_albums_own_year_comes_before_another_year()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Mono No Aware", "Ryan Camus", year: 2019),
             Album("Mono No Aware", "Martin Roth", year: 2022),
             Album("V/A - mono no aware", "PAN"),
             Album("Mono No Aware", "Someone", year: 2017)],
            "Mono No Aware", "Mono No Aware", year: 2017);

        Assert.Equal(["Someone", "PAN", "Ryan Camus", "Martin Roth"], ranked.Select(c => c.Artist));
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

    // Largest first only among equally good matches: a bigger picture of
    // another album of the same name is not the one looked for.
    [Fact]
    public void A_better_match_is_offered_before_a_larger_cover()
    {
        var ranked = OnlineCoverArt.Rank(
            [Album("Mono No Aware", "Ryan Camus", year: 2019), Album("V/A - mono no aware", "PAN")],
            "Mono No Aware", "Various Artists", year: 2017);

        var largest = OnlineCoverArt.Largest(
        [
            new OnlineCoverArt.Download(ranked.Single(c => c.Artist == "Ryan Camus"), SyntheticPng.Build(40, 40)),
            new OnlineCoverArt.Download(ranked.Single(c => c.Artist == "PAN"), SyntheticPng.Build(10, 10)),
        ]);

        Assert.Equal(["PAN", "Ryan Camus"], largest.Select(d => d.Candidate.Artist));
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
              {"id":"4e765614","title":"Transit of Venus","first-release-date":"2005-03","artist-credit":[
                {"name":"Hangedup","joinphrase":" & "},{"name":"Tony Conrad"}]},
              {"title":"No id"}
            ]}
            """;

        var group = Assert.Single(OnlineCoverArt.ParseMusicBrainz(json));
        Assert.Equal("Transit of Venus", group.Album);
        Assert.Equal("Hangedup & Tony Conrad", group.Artist);
        Assert.Equal("https://coverartarchive.org/release-group/4e765614", group.ArtworkUrl);
        Assert.Equal(2005, group.Year);
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

    // Deezer's search answers with albums only, each with its cover at 1000
    // pixels; one without a cover is not a candidate, and an error answer
    // is no albums.
    [Fact]
    public void Reads_deezer_albums_at_their_large_size()
    {
        const string json = """
            {"data":[
              {"id":1,"title":"Oil & Blood","cover_big":"https://cdn.test/500x500.jpg",
               "cover_xl":"https://cdn-images.dzcdn.net/images/cover/d6af/1000x1000-000000-80-0-0.jpg",
               "artist":{"id":2,"name":"The Unsettlers"},"type":"album"},
              {"id":3,"title":"No cover","artist":{"name":"Someone"}}
            ],"total":2}
            """;

        var album = Assert.Single(OnlineCoverArt.ParseDeezer(json));
        Assert.Equal("Oil & Blood", album.Album);
        Assert.Equal("The Unsettlers", album.Artist);
        Assert.Equal("https://cdn-images.dzcdn.net/images/cover/d6af/1000x1000-000000-80-0-0.jpg", album.ArtworkUrl);
        Assert.Empty(OnlineCoverArt.ParseDeezer("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}"""));
    }

    // One disc of a two-disc album, tagged with the disc's own name, is
    // that album.
    [Fact]
    public void A_disc_named_on_its_own_finds_the_album_it_belongs_to()
    {
        Assert.Single(OnlineCoverArt.Rank([Album("Oil & Blood", "The Unsettlers")], "Oil", "The Unsettlers"));
    }

    // MusicBrainz indexes a credit word by word, so the lead artist goes as
    // a phrase and the rest of a credit stays out of it. The album goes as
    // its words, which find it whatever their order there.
    [Fact]
    public void Asks_musicbrainz_for_the_albums_words_by_its_lead_artist()
    {
        Assert.Equal("releasegroup:(transit of venus) AND artist:\"Hangedup\"",
            OnlineCoverArt.MusicBrainzQuery("Transit Of Venus", "Hangedup and Tony Conrad"));
        Assert.Equal("releasegroup:(69 love songs) AND artist:\"Takashi Kokubo\"",
            OnlineCoverArt.MusicBrainzQuery("69 Love Songs Disc 2", "Takashi Kokubo (小久保隆)"));
        Assert.Equal("releasegroup:(book of angels vol 01 astaroth) AND artist:\"Jamie Saft Trio\"",
            OnlineCoverArt.MusicBrainzQuery("Book of Angels, Vol. 01 - Astaroth", "Jamie Saft Trio"));
        Assert.Equal("releasegroup:(say hi)", OnlineCoverArt.MusicBrainzQuery("Say \"Hi\"", ""));
        Assert.Equal("releasegroup:(mono no aware)", OnlineCoverArt.MusicBrainzQuery("Mono No Aware", "Various Artists"));
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
