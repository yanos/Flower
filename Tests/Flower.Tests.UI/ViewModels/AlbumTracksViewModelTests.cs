using System;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// Selection survives a rebuild of the same album and resets for another.
public class AlbumTracksViewModelTests
{
    private static AlbumGridRowViewModel Album(int songs) => new()
    {
        Tiles = Array.Empty<Flower.ViewModels.AlbumTileViewModel>(),
        Columns = 1,
        ExpandedTracks = Enumerable.Range(1, songs).Select(i => new Track { Title = $"Song {i}" }).ToList(),
    };

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
