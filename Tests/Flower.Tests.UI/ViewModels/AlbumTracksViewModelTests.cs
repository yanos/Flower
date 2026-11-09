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
        Tiles = Array.Empty<Flower.ViewModels.AlbumTileViewModel>(),
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

    // The album rebuilt under an open row - a cover or tags changed from Get
    // Info, which replaces its songs - keeps the songs picked, or Get Info
    // opened again would be over the whole album rather than them.
    [Fact]
    public void The_songs_picked_stay_picked_when_the_album_is_rebuilt()
    {
        var album = Album(4);
        album.SelectTrack(album.TrackRows[1], toggle: false, rangeSelect: false);
        album.SelectTrack(album.TrackRows[2], toggle: false, rangeSelect: true);
        var picked = album.SelectedTracks.Select(t => t.Id).ToList();

        album.ExpandedTracks = album.ExpandedTracks.Select(t => new Track { Id = t.Id, Title = t.Title + " (edited)" }).ToList();

        Assert.Equal(picked, album.SelectedTracks.Select(t => t.Id));
        Assert.Equal("-- -v ^- --", Joins(album));

        // And the range still grows from where it started.
        album.MoveSelection(+1, extend: true);
        Assert.Equal(3, album.SelectedTracks.Count);
    }

    [Fact]
    public void Another_album_opened_starts_with_nothing_picked()
    {
        var album = Album(3);
        album.SelectTrack(album.TrackRows[0], toggle: false, rangeSelect: false);

        album.ExpandedTracks = Enumerable.Range(1, 3).Select(i => new Track { Title = $"Other {i}" }).ToList();

        Assert.Empty(album.SelectedTracks);
    }
}
