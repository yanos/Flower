using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class AlbumGridBuilderTests
{
    private static Track T(string album, string artist, DateTimeOffset dateAdded, string title = "Song") =>
        new Track { Title = title, Album = album, Artists = artist, DateAdded = dateAdded, Path = "/music/x.mp3" };

    [Fact]
    public void Build_groups_by_album_name_alone_regardless_of_per_track_artist()
    {
        var tracks = new List<Track>
        {
            T("Compilation", "Artist A", DateTimeOffset.UtcNow, "Track 1"),
            T("Compilation", "Artist B", DateTimeOffset.UtcNow, "Track 2"),
        };

        var album = Assert.Single(AlbumGridBuilder.Build(tracks));

        Assert.Equal("Compilation", album.Name);
    }

    [Fact]
    public void Build_labels_a_group_spanning_multiple_artists_as_Various_Artists()
    {
        var tracks = new List<Track>
        {
            T("Compilation", "Artist A", DateTimeOffset.UtcNow, "Track 1"),
            T("Compilation", "Artist B", DateTimeOffset.UtcNow, "Track 2"),
        };

        var album = Assert.Single(AlbumGridBuilder.Build(tracks));

        Assert.Equal("Various Artists", album.Artist);
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
