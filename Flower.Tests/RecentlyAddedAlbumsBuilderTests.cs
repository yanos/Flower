using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class RecentlyAddedAlbumsBuilderTests
{
    private static Track T(string album, string artist, DateTimeOffset dateAdded, string title = "Song", string? path = "/music/x.mp3") =>
        new Track { Title = title, Album = album, Artists = artist, DateAdded = dateAdded, Path = path };

    [Fact]
    public void Build_orders_albums_by_most_recently_added_track_descending()
    {
        var old = T("Old Album", "Artist", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var recent = T("New Album", "Artist", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var albums = RecentlyAddedAlbumsBuilder.Build(new List<Track> { old, recent });

        Assert.Equal(new[] { "New Album", "Old Album" }, albums.Select(a => a.Name));
    }

    [Fact]
    public void Build_uses_the_max_DateAdded_among_an_albums_tracks_as_its_recency()
    {
        var early = T("Album", "Artist", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), "Track 1");
        var late = T("Album", "Artist", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "Track 2");

        var album = Assert.Single(RecentlyAddedAlbumsBuilder.Build(new List<Track> { early, late }));

        Assert.Equal(late.DateAdded, album.MostRecentlyAdded);
    }

    [Fact]
    public void Build_does_not_collide_same_named_albums_by_different_artists()
    {
        var tracks = new List<Track>
        {
            T("Greatest Hits", "Artist A", DateTimeOffset.UtcNow),
            T("Greatest Hits", "Artist B", DateTimeOffset.UtcNow),
        };

        var albums = RecentlyAddedAlbumsBuilder.Build(tracks);

        Assert.Equal(2, albums.Count);
        Assert.Equal(2, albums.Select(a => a.Artist).Distinct().Count());
    }

    [Fact]
    public void Build_excludes_tracks_with_no_album()
    {
        var tracks = new List<Track> { T("", "Artist", DateTimeOffset.UtcNow), T(null!, "Artist", DateTimeOffset.UtcNow) };

        Assert.Empty(RecentlyAddedAlbumsBuilder.Build(tracks));
    }

    [Fact]
    public void Build_includes_placeholder_tracks_not_yet_downloaded()
    {
        var placeholder = T("Album", "Artist", DateTimeOffset.UtcNow, path: null);
        placeholder.OriginDeviceFingerprint = "peer-1";

        var album = Assert.Single(RecentlyAddedAlbumsBuilder.Build(new List<Track> { placeholder }));

        Assert.Equal("Album", album.Name);
        Assert.Same(placeholder, album.RepresentativeTrack);
    }

    [Fact]
    public void Build_RepresentativeTrack_is_the_most_recently_added_track_in_the_album()
    {
        var early = T("Album", "Artist", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), "Track 1");
        var late = T("Album", "Artist", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "Track 2");

        var album = Assert.Single(RecentlyAddedAlbumsBuilder.Build(new List<Track> { early, late }));

        Assert.Same(late, album.RepresentativeTrack);
    }
}
