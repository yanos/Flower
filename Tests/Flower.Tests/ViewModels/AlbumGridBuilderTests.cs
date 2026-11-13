using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests.ViewModels;

public class AlbumGridBuilderTests
{
    private static Track T(string album, string artist, DateTimeOffset dateAdded, string title = "Song") =>
        new Track { Title = title, Album = album, Artists = artist, DateAdded = dateAdded, Path = "/music/x.mp3" };

    // Crystal Antlers' "EP" and The Fiery Furnaces' "EP", neither with an
    // album artist tag: two albums, not one cover over both bands' songs.
    [Fact]
    public void Two_albums_of_one_name_by_different_artists_are_two_albums()
    {
        var tracks = new List<Track>
        {
            T("EP", "Crystal Antlers", DateTimeOffset.UtcNow, "Vexation"),
            T("EP", "The Fiery Furnaces", DateTimeOffset.UtcNow, "Single Again"),
            T("EP", "The Fiery Furnaces", DateTimeOffset.UtcNow, "Here Comes the Summer"),
        };

        var albums = AlbumGridBuilder.Build(tracks);

        Assert.Equal(
            new[] { ("Crystal Antlers", 1), ("The Fiery Furnaces", 2) },
            albums.Select(a => (a.Artist!, a.Tracks.Count)).OrderBy(a => a.Item1));
        Assert.All(albums, a => Assert.Equal("EP", a.Name));
    }

    // A compilation tagged as one - by its album artist, or the compilation
    // flag - is one album, however many artists its songs are by.
    [Fact]
    public void A_tagged_compilation_is_one_album()
    {
        var byAlbumArtist = new List<Track>
        {
            T("Compilation", "Artist A", DateTimeOffset.UtcNow, "Track 1"),
            T("Compilation", "Artist B", DateTimeOffset.UtcNow, "Track 2"),
        };
        foreach (var track in byAlbumArtist)
            track.AlbumArtists = "Various Artists";
        var flagged = new List<Track>
        {
            T("Compilation", "Artist A", DateTimeOffset.UtcNow, "Track 1"),
            T("Compilation", "Artist B", DateTimeOffset.UtcNow, "Track 2"),
        };
        foreach (var track in flagged)
            track.IsCompilation = true;

        Assert.Equal("Various Artists", Assert.Single(AlbumGridBuilder.Build(byAlbumArtist)).Artist);
        Assert.Equal("Various Artists", Assert.Single(AlbumGridBuilder.Build(flagged)).Artist);
    }

    // No album artist tag, and a guest on one song: still the one album.
    [Fact]
    public void A_guest_on_one_song_does_not_split_the_album()
    {
        var tracks = new List<Track>
        {
            T("Album", "Death From Above 1979", DateTimeOffset.UtcNow, "Track 1"),
            T("Album", "Death From Above 1979 feat. Someone", DateTimeOffset.UtcNow, "Track 2"),
        };

        Assert.Equal(2, Assert.Single(AlbumGridBuilder.Build(tracks)).Tracks.Count);
    }

    [Fact]
    public void Build_labels_a_single_artist_album_with_its_own_artist()
    {
        var tracks = new List<Track>
        {
            T("Album", "Artist A", DateTimeOffset.UtcNow, "Track 1"),
            T("Album", "Artist A", DateTimeOffset.UtcNow, "Track 2"),
        };

        var album = Assert.Single(AlbumGridBuilder.Build(tracks));

        Assert.Equal("Artist A", album.Artist);
    }
}
