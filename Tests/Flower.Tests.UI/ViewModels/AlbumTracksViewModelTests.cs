using System;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// An open album's lit rows - picked or pointed at - join the lit rows they
// touch, as the main track list's do, so a run of them reads as one shape.
public class AlbumTracksViewModelTests
{
    private static AlbumGridRowViewModel Album(int songs) => new()
    {
        Tiles = Array.Empty<Flower.ViewModels.Mobile.AlbumTileViewModel>(),
        Columns = 1,
        ExpandedTracks = Enumerable.Range(1, songs).Select(i => new Track { Title = $"Song {i}" }).ToList(),
    };

    private static string Joins(AlbumTracksViewModel album) =>
        string.Join(" ", album.TrackRows.Select(r => (r.JoinsAbove ? "^" : "-") + (r.JoinsBelow ? "v" : "-")));

    [Fact]
    public void A_range_of_picked_songs_joins_into_one_shape()
    {
        var album = Album(5);
        album.SelectTrack(album.TrackRows[1], toggle: false, rangeSelect: false);
        album.SelectTrack(album.TrackRows[3], toggle: false, rangeSelect: true);

        Assert.Equal("-- -v ^v ^- --", Joins(album));
    }

    [Fact]
    public void Picked_songs_apart_stay_rounded_all_round()
    {
        var album = Album(4);
        album.SelectTrack(album.TrackRows[0], toggle: true, rangeSelect: false);
        album.SelectTrack(album.TrackRows[2], toggle: true, rangeSelect: false);

        Assert.Equal("-- -- -- --", Joins(album));
    }

    [Fact]
    public void The_song_pointed_at_joins_a_picked_neighbour_and_parts_when_left()
    {
        var album = Album(3);
        album.SelectTrack(album.TrackRows[0], toggle: false, rangeSelect: false);

        album.TrackRows[1].IsHovered = true;
        Assert.Equal("-v ^- --", Joins(album));

        album.TrackRows[1].IsHovered = false;
        Assert.Equal("-- -- --", Joins(album));
    }
}
