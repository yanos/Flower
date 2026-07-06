using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class LibraryOpenSubsonicMapperTests
{
    private static Track RealTrack(string title, string artist, string album, int trackNumber = 1, int durationSeconds = 200) =>
        new Track
        {
            Title = title, Artists = artist, Album = album, TrackNumber = (uint)trackNumber,
            Duration = TimeSpan.FromSeconds(durationSeconds), Year = "1999", Genre = "Rock",
            Path = $"/music/{title}.mp3",
        };

    [Fact]
    public void BuildAlbumList_excludes_placeholder_tracks()
    {
        var real = RealTrack("A", "Artist", "Album");
        var placeholder = new Track { Title = "B", Artists = "Artist", Album = "Other Album", Path = null, OriginDeviceFingerprint = "peer-1" };

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(new List<Track> { real, placeholder });

        var album = Assert.Single(albums);
        Assert.Equal("Album", album.Name);
    }

    [Fact]
    public void BuildAlbumList_groups_by_album_and_artist_and_counts_songs()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Beatles", "Abbey Road", trackNumber: 1),
            RealTrack("Track2", "Beatles", "Abbey Road", trackNumber: 2),
        };

        var album = Assert.Single(LibraryOpenSubsonicMapper.BuildAlbumList(tracks));

        Assert.Equal("Abbey Road", album.Name);
        Assert.Equal("Beatles", album.Artist);
        Assert.Equal(2, album.SongCount);
        Assert.Equal(400, album.Duration);
    }

    [Fact]
    public void BuildAlbumList_does_not_collide_same_named_albums_by_different_artists()
    {
        var tracks = new List<Track>
        {
            RealTrack("Track1", "Artist A", "Greatest Hits"),
            RealTrack("Track2", "Artist B", "Greatest Hits"),
        };

        var albums = LibraryOpenSubsonicMapper.BuildAlbumList(tracks);

        Assert.Equal(2, albums.Count);
        Assert.Equal(2, albums.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void FindAlbum_returns_null_for_an_unknown_id()
    {
        Assert.Null(LibraryOpenSubsonicMapper.FindAlbum(new List<Track>(), "al:nope|nope"));
    }

    [Fact]
    public void FindAlbum_returns_the_full_song_list_for_a_known_album()
    {
        var tracks = new List<Track>
        {
            RealTrack("Come Together", "Beatles", "Abbey Road", trackNumber: 1, durationSeconds: 259),
            RealTrack("Something", "Beatles", "Abbey Road", trackNumber: 2, durationSeconds: 183),
        };
        var albumId = LibraryOpenSubsonicMapper.AlbumId("Abbey Road", "Beatles");

        var album = LibraryOpenSubsonicMapper.FindAlbum(tracks, albumId);

        Assert.NotNull(album);
        Assert.Equal(2, album!.Song?.Count);
        Assert.Equal("Come Together", album.Song![0].Title);
        Assert.Equal(259, album.Song[0].Duration);
        Assert.Equal(albumId, album.Song[0].AlbumId);
    }

    [Fact]
    public void ToChild_song_id_matches_the_track_SyncKey_so_the_client_can_independently_recompute_it()
    {
        var track = RealTrack("Come Together", "Beatles", "Abbey Road", durationSeconds: 259);

        var album = LibraryOpenSubsonicMapper.FindAlbum(new List<Track> { track }, LibraryOpenSubsonicMapper.AlbumId("Abbey Road", "Beatles"));

        Assert.Equal(track.SyncKey, album!.Song!.Single().Id);
    }
}
