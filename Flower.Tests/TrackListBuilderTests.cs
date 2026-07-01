using System;
using System.Collections.Generic;
using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class TrackListBuilderTests
{
    private static Track T(
        string title, string artist = "X", string album = "Y", uint trackNumber = 1,
        uint discNumber = 0, string? genre = null, string? path = null)
        => new Track
        {
            Title       = title,
            Artists     = artist,
            Album       = album,
            TrackNumber = trackNumber,
            DiscNumber  = discNumber,
            Genre       = genre,
            Path        = path ?? $"/music/{artist}/{album}/{title}.mp3",
        };

    [Fact]
    public void Filter_matches_title_artist_album_and_genre_case_insensitively()
    {
        var tracks = new List<Track>
        {
            T("Sunrise",   artist: "Aurora", album: "Dawn", genre: "Ambient"),
            T("Nightfall", artist: "Nova",   album: "Dusk", genre: "Electronic"),
        };

        var rows = TrackListBuilder.Build(tracks, "aurora", "Title", true);
        Assert.Single(rows);
        Assert.Equal("Sunrise", rows[0].Track.Title);

        rows = TrackListBuilder.Build(tracks, "ELECTRONIC", "Title", true);
        Assert.Single(rows);
        Assert.Equal("Nightfall", rows[0].Track.Title);
    }

    [Fact]
    public void Filter_with_blank_text_returns_all_tracks()
    {
        var tracks = new List<Track> { T("A"), T("B") };
        var rows = TrackListBuilder.Build(tracks, "   ", "Title", true);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Sort_by_title_ascending_and_descending()
    {
        var tracks = new List<Track> { T("Bravo"), T("Alpha"), T("Charlie") };

        var asc = TrackListBuilder.Build(tracks, null, "Title", true);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, asc.ConvertAll(r => r.Track.Title));

        var desc = TrackListBuilder.Build(tracks, null, "Title", false);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, desc.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_tracknumber_orders_by_album_then_disc_then_track_number()
    {
        var tracks = new List<Track>
        {
            T("B-Disc2-Track1", album: "Album B", discNumber: 2, trackNumber: 1),
            T("A-Track2",       album: "Album A", discNumber: 1, trackNumber: 2),
            T("A-Track1",       album: "Album A", discNumber: 1, trackNumber: 1),
            T("B-Disc1-Track1", album: "Album B", discNumber: 1, trackNumber: 1),
        };

        var rows = TrackListBuilder.Build(tracks, null, "TrackNumber", true);

        Assert.Equal(
            new[] { "A-Track1", "A-Track2", "B-Disc1-Track1", "B-Disc2-Track1" },
            rows.ConvertAll(r => r.Track.Title));
    }

    [Fact]
    public void Sort_by_album_groups_consecutive_same_album_tracks()
    {
        var tracks = new List<Track>
        {
            T("A1", album: "Alpha Album", trackNumber: 1),
            T("B1", album: "Beta Album",  trackNumber: 1),
            T("A2", album: "Alpha Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Album", true);

        Assert.Equal(new[] { "A1", "A2", "B1" }, rows.ConvertAll(r => r.Track.Title));

        Assert.True(rows[0].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[0].AlbumGroupSize);
        Assert.False(rows[1].IsFirstInAlbumGroup);
        Assert.Equal(2, rows[1].AlbumGroupSize);
        Assert.True(rows[2].IsFirstInAlbumGroup);
        Assert.Equal(1, rows[2].AlbumGroupSize);
    }

    [Fact]
    public void Sort_by_column_other_than_album_or_tracknumber_does_not_group()
    {
        var tracks = new List<Track>
        {
            T("A1", album: "Same Album", trackNumber: 1),
            T("A2", album: "Same Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true);

        Assert.All(rows, r => Assert.True(r.IsFirstInAlbumGroup));
        Assert.All(rows, r => Assert.Equal(1, r.AlbumGroupSize));
    }

    [Fact]
    public void PlaylistOrder_preserves_input_order_and_does_not_group()
    {
        var tracks = new List<Track>
        {
            T("Third",  album: "Album", trackNumber: 3),
            T("First",  album: "Album", trackNumber: 1),
            T("Second", album: "Album", trackNumber: 2),
        };

        var rows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true);

        Assert.Equal(new[] { "Third", "First", "Second" }, rows.ConvertAll(r => r.Track.Title));
        Assert.All(rows, r => Assert.True(r.IsFirstInAlbumGroup));
    }

    [Fact]
    public void IsCurrentlyPlaying_is_set_by_matching_track_path()
    {
        var playing = T("Now Playing", path: "/music/playing.mp3");
        var tracks  = new List<Track> { playing, T("Other", path: "/music/other.mp3") };

        var rows = TrackListBuilder.Build(tracks, null, "Title", true, playing);

        Assert.True(rows.Find(r => r.Track.Path == "/music/playing.mp3")!.IsCurrentlyPlaying);
        Assert.False(rows.Find(r => r.Track.Path == "/music/other.mp3")!.IsCurrentlyPlaying);
    }
}
