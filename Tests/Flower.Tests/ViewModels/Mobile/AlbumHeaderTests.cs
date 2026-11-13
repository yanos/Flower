using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Tests.ViewModels.Mobile;

public class AlbumHeaderTests
{
    private static AlbumTileViewModel? HeaderOf(List<Track> tracks) =>
        AlbumHeaderBuilder.Build(tracks, AlbumGroup.KeyOf(tracks[0])!);

    private static string? HeaderArtistOf(List<Track> tracks) => HeaderOf(tracks)?.Artist;

    private static Track Song(string title, string artists, string? albumArtists = null, string? year = null) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Album = "Album", Artists = artists, AlbumArtists = albumArtists,
        Year = year,
    };

    // A reissue's bonus track carries the reissue's year; the album is the
    // original's.
    [Fact]
    public void The_album_header_uses_the_albums_earliest_year()
    {
        Assert.Equal("1975", HeaderOf(
        [
            Song("One", "Mulatu Astatke", year: "1975"),
            Song("Bonus", "Mulatu Astatke", year: "1998"),
        ])?.Year);
    }

    [Fact]
    public void The_header_names_the_songs_artist_rather_than_the_album_artist()
    {
        Assert.Equal("Explosions in the Sky", HeaderArtistOf(
        [
            Song("One", "Explosions in the Sky", albumArtists: "Explosions In The Sky"),
            Song("Two", "Explosions in the Sky", albumArtists: "Explosions In The Sky"),
        ]));
    }

    // One artist in two cases is one artist, named the way most songs do.
    [Fact]
    public void An_artist_spelled_in_two_cases_is_still_the_one_artist()
    {
        Assert.Equal("Add N to (X)", HeaderArtistOf(
        [
            Song("One", "Add N to (X)"),
            Song("Two", "Add N to (X)"),
            Song("Three", "Add N To (X)"),
        ]));
    }

    // A guest on one song: the album artist says whose album it is.
    [Fact]
    public void Songs_by_several_fall_back_to_the_album_artist()
    {
        Assert.Equal("Death From Above 1979", HeaderArtistOf(
        [
            Song("One", "Death From Above 1979", albumArtists: "Death From Above 1979"),
            Song("Two", "Death From Above 1979 feat. Someone", albumArtists: "Death From Above 1979"),
        ]));
    }

    // A compilation tagged by its flag alone, with no album artist.
    [Fact]
    public void A_compilation_is_various_artists()
    {
        var one = Song("One", "Artist A");
        var two = Song("Two", "Artist B");
        one.IsCompilation = two.IsCompilation = true;
        Assert.Equal("Various Artists", HeaderArtistOf([one, two]));
    }

    // The artist line goes to that artist's page: the page the songs are
    // listed under, even when the line names the album artist - and no page
    // at all for an album by several.
    [Fact]
    public void The_artist_line_goes_to_the_page_the_songs_are_listed_under()
    {
        Assert.Equal("Explosions in the Sky", HeaderOf(
        [
            Song("One", "Explosions in the Sky", albumArtists: "Explosions In The Sky"),
        ])?.ArtistPage);
        Assert.Equal("Death From Above 1979", HeaderOf(
        [
            Song("One", "Death From Above 1979", albumArtists: "Death From Above 1979"),
            Song("Two", "Death From Above 1979 feat. Someone", albumArtists: "Death From Above 1979"),
        ])?.ArtistPage);
        Assert.Null(HeaderOf(
        [
            Song("One", "Artist A", albumArtists: "Various Artists"),
            Song("Two", "Artist B", albumArtists: "Various Artists"),
        ])?.ArtistPage);
    }
}
