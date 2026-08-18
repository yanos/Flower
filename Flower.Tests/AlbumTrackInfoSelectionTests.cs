using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md Tier 4.3: the "what does Get Info act on" rules
// for the album grids, lifted out of MainView's code-behind. Non-trivial
// precedence with three inputs (song selection inside the expanded album,
// selected album tiles, the expanded album itself) and two output modes
// (single-track with Prev/Next, or batch).
public class AlbumTrackInfoSelectionTests
{
    private static readonly Track One   = new() { Title = "one",   Album = "A", Path = "/1.mp3" };
    private static readonly Track Two   = new() { Title = "two",   Album = "A", Path = "/2.mp3" };
    private static readonly Track Three = new() { Title = "three", Album = "A", Path = "/3.mp3" };
    private static readonly Track Other = new() { Title = "other", Album = "B", Path = "/4.mp3" };

    private static readonly IReadOnlyList<Track> AlbumA = new[] { One, Two, Three };

    private static IEnumerable<Track> TracksFor(IEnumerable<string> albums) =>
        albums.SelectMany(a => a == "A" ? AlbumA : a == "B" ? new[] { Other } : Array.Empty<Track>());

    private static TrackInfoTarget Resolve(
        IReadOnlyList<Track>? songSelection = null,
        IReadOnlyList<Track>? expandedAlbumTracks = null,
        IReadOnlyCollection<string>? selectedAlbums = null,
        string? expandedAlbumName = null) =>
        AlbumTrackInfoSelection.Resolve(
            songSelection ?? Array.Empty<Track>(),
            expandedAlbumTracks ?? Array.Empty<Track>(),
            selectedAlbums ?? Array.Empty<string>(),
            expandedAlbumName,
            TracksFor);

    [Fact]
    public void One_song_selected_in_the_expanded_album_opens_that_album_focused_on_it()
    {
        var target = Resolve(songSelection: new[] { Two }, expandedAlbumTracks: AlbumA, expandedAlbumName: "A");

        Assert.Equal(AlbumA, target.Tracks);
        Assert.Equal(1, target.FocusIndex);
    }

    [Fact]
    public void A_song_selection_wins_over_selected_album_tiles()
    {
        // This is the case that regressed before: with a tile selection also
        // present, the old code fell back to the whole album(s).
        var target = Resolve(
            songSelection: new[] { Three },
            expandedAlbumTracks: AlbumA,
            selectedAlbums: new[] { "A", "B" },
            expandedAlbumName: "A");

        Assert.Equal(AlbumA, target.Tracks);
        Assert.Equal(2, target.FocusIndex);
    }

    [Fact]
    public void Several_songs_selected_open_as_a_batch_of_exactly_those_songs()
    {
        var target = Resolve(
            songSelection: new[] { One, Three },
            expandedAlbumTracks: AlbumA,
            expandedAlbumName: "A");

        Assert.Equal(new[] { One, Three }, target.Tracks);
        Assert.Null(target.FocusIndex);
    }

    [Fact]
    public void A_selected_song_missing_from_the_expanded_list_opens_alone()
    {
        var target = Resolve(songSelection: new[] { Other }, expandedAlbumTracks: AlbumA);

        Assert.Equal(new[] { Other }, target.Tracks);
        Assert.Equal(0, target.FocusIndex);
    }

    [Fact]
    public void Selected_album_tiles_open_as_one_batch_across_all_of_them()
    {
        var target = Resolve(selectedAlbums: new[] { "A", "B" });

        Assert.Equal(new[] { One, Two, Three, Other }, target.Tracks);
        Assert.Null(target.FocusIndex);
    }

    [Fact]
    public void One_selected_album_tile_is_still_batch_mode()
    {
        // No meaningful single-track Prev/Next context at tile level, even
        // when the batch happens to be one album.
        var target = Resolve(selectedAlbums: new[] { "A" });

        Assert.Equal(AlbumA, target.Tracks);
        Assert.Null(target.FocusIndex);
    }

    [Fact]
    public void With_nothing_selected_it_falls_back_to_the_expanded_album()
    {
        // The common case: a plain click expands a tile without touching
        // SelectedSubItems at all.
        var target = Resolve(expandedAlbumName: "A");

        Assert.Equal(AlbumA, target.Tracks);
        Assert.Null(target.FocusIndex);
    }

    [Fact]
    public void Nothing_selected_and_nothing_expanded_resolves_to_nothing()
    {
        Assert.True(Resolve().IsEmpty);
        Assert.True(TrackInfoTarget.None.IsEmpty);
    }

    [Fact]
    public void An_expanded_album_with_no_tracks_resolves_to_nothing()
    {
        var target = Resolve(expandedAlbumName: "missing");

        Assert.True(target.IsEmpty);
    }
}
