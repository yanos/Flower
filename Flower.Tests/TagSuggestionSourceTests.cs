using System.Collections.Generic;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class TagSuggestionSourceTests
{
    private static Track T(string? artists = null, string? albumArtists = null, string? album = null) =>
        new Track { Artists = artists, AlbumArtists = albumArtists, Album = album };

    [Fact]
    public void DistinctArtists_dedupes_across_Artists_and_AlbumArtists()
    {
        var tracks = new List<Track>
        {
            T(artists: "Artist A"),
            T(artists: "Artist A"),
            T(albumArtists: "Artist A"),
            T(artists: "Artist B", albumArtists: "Various Artists"),
        };

        var suggestions = TagSuggestionSource.DistinctArtists(tracks);

        Assert.Equal(new[] { "Artist A", "Artist B", "Various Artists" }, suggestions);
    }

    [Fact]
    public void DistinctArtists_ignores_null_empty_and_whitespace()
    {
        var tracks = new List<Track>
        {
            T(artists: null),
            T(artists: ""),
            T(artists: "   "),
            T(artists: "Artist A"),
        };

        var suggestions = TagSuggestionSource.DistinctArtists(tracks);

        Assert.Equal(new[] { "Artist A" }, suggestions);
    }

    [Fact]
    public void DistinctArtists_orders_case_insensitively()
    {
        var tracks = new List<Track> { T(artists: "beatles"), T(artists: "ABBA") };

        var suggestions = TagSuggestionSource.DistinctArtists(tracks);

        Assert.Equal(new[] { "ABBA", "beatles" }, suggestions);
    }

    [Fact]
    public void DistinctArtists_returns_empty_for_no_tracks()
    {
        Assert.Empty(TagSuggestionSource.DistinctArtists(new List<Track>()));
    }

    [Fact]
    public void DistinctAlbums_ignores_the_Artists_field()
    {
        var tracks = new List<Track>
        {
            T(album: "Abbey Road", artists: "Beatles"),
            T(album: "Abbey Road", artists: "Someone Else"),
            T(album: "Revolver"),
        };

        var suggestions = TagSuggestionSource.DistinctAlbums(tracks);

        Assert.Equal(new[] { "Abbey Road", "Revolver" }, suggestions);
    }

    [Fact]
    public void DistinctAlbums_ignores_null_empty_and_whitespace()
    {
        var tracks = new List<Track> { T(album: null), T(album: ""), T(album: "  "), T(album: "Revolver") };

        var suggestions = TagSuggestionSource.DistinctAlbums(tracks);

        Assert.Equal(new[] { "Revolver" }, suggestions);
    }
}
