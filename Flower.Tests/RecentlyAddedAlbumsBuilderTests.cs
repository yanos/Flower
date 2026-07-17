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

    private static Track T(string album, string artist, string? albumArtist, DateTimeOffset dateAdded, string title = "Song", bool isCompilation = false) =>
        new Track { Title = title, Album = album, Artists = artist, AlbumArtists = albumArtist, IsCompilation = isCompilation, DateAdded = dateAdded, Path = "/music/x.mp3" };

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
    public void Build_does_not_duplicate_a_various_artists_compilation_flagged_via_the_compilation_tag()
    {
        var tracks = new List<Track>
        {
            T("Now That's What I Call Music", "Artist A", albumArtist: null, DateTimeOffset.UtcNow, "Track 1", isCompilation: true),
            T("Now That's What I Call Music", "Artist B", albumArtist: null, DateTimeOffset.UtcNow, "Track 2", isCompilation: true),
            T("Now That's What I Call Music", "Artist C", albumArtist: null, DateTimeOffset.UtcNow, "Track 3", isCompilation: true),
        };

        var album = Assert.Single(RecentlyAddedAlbumsBuilder.Build(tracks));

        Assert.Equal("Now That's What I Call Music", album.Name);
        Assert.Equal("Various Artists", album.Artist);
    }

    [Fact]
    public void Build_still_separates_same_named_albums_by_different_artists_when_neither_is_a_compilation()
    {
        var tracks = new List<Track>
        {
            T("Now That's What I Call Music", "Artist A", albumArtist: null, DateTimeOffset.UtcNow, "Track 1"),
            T("Now That's What I Call Music", "Artist B", albumArtist: null, DateTimeOffset.UtcNow, "Track 2"),
        };

        var albums = RecentlyAddedAlbumsBuilder.Build(tracks);

        Assert.Equal(2, albums.Count);
    }

    [Fact]
    public void Build_does_not_duplicate_a_various_artists_compilation_with_a_consistent_AlbumArtists_tag()
    {
        var tracks = new List<Track>
        {
            T("Compilation", "Artist A", albumArtist: "Various Artists", DateTimeOffset.UtcNow, "Track 1"),
            T("Compilation", "Artist B", albumArtist: "Various Artists", DateTimeOffset.UtcNow, "Track 2"),
        };

        var album = Assert.Single(RecentlyAddedAlbumsBuilder.Build(tracks));

        Assert.Equal("Various Artists", album.Artist);
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
