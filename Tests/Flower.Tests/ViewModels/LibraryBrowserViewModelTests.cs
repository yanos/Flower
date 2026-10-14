using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// One of the collaborators split out of MainViewModel (docs/ARCHITECTURE-
// REVIEW.md Tier 4.2), constructed directly rather than through that class's
// constructor and the whole service graph behind it.
public class LibraryBrowserViewModelTests
{
    private sealed class Host : ILibraryBrowseHost
    {
        public SidebarItemKind? CurrentKind { get; set; } = SidebarItemKind.Songs;
        public Playlist? CurrentPlaylist { get; set; }
        public Track? CurrentlyPlayingTrack { get; set; }
        public string? PairedServerFingerprint { get; set; }
        public bool IsPairedServerReachable { get; set; }
        public (string Column, bool Ascending)? PersistedSort;
        public bool? PersistedArtistAlbumsByYear;
        public void PersistSort(string column, bool ascending) => PersistedSort = (column, ascending);
        public void PersistSortArtistAlbumsByYear(bool value) => PersistedArtistAlbumsByYear = value;
    }

    private static Track Song(string title, string album, string artist, DateTimeOffset? lastPlayed = null) =>
        new()
        {
            Title = title, Album = album, Artists = artist,
            Path = $"/music/{artist}/{album}/{title}.mp3",
            LastPlayedAt = lastPlayed,
        };

    private static (LibraryBrowserViewModel Vm, Host Host) Make(params Track[] tracks)
    {
        var host = new Host();
        return (new LibraryBrowserViewModel(new Library(tracks.ToList()), host, NullLogger<LibraryBrowserViewModel>.Instance), host);
    }

    [AvaloniaFact]
    public async Task Songs_shows_every_track()
    {
        var (vm, _) = Make(Song("a", "One", "X"), Song("b", "One", "X"), Song("c", "Two", "Y"));

        vm.Repopulate();
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal(3, vm.Rows.Count);
    }

    // "Add N to (X)" on two albums and "Add N To (X)" on one: one artist,
    // as the catalog already counts them, named the way most of the songs
    // spell it - and picking it shows every one of them.
    [AvaloniaFact]
    public async Task An_artist_spelled_in_two_cases_is_one_artist()
    {
        var (vm, host) = Make(
            Song("a", "One", "Add N to (X)"), Song("b", "Two", "Add N to (X)"),
            Song("c", "Three", "Add N To (X)"), Song("d", "Four", "Other"));
        host.CurrentKind = SidebarItemKind.Artists;
        vm.Repopulate();

        Assert.Equal(new[] { "Add N to (X)", "Other" }, vm.SubListItems);

        vm.SetSelectedSubItems(new[] { "Add N to (X)" });
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal(new[] { "a", "b", "c" }, vm.Rows.Select(r => r.Track.Title).Order());
        Assert.Equal(3, vm.GetTracksForSubListItems(new[] { "Add N to (X)" }).Count());
    }

    [AvaloniaFact]
    public async Task Artists_shows_nothing_until_an_artist_is_picked_then_only_that_artists_tracks()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        host.CurrentKind = SidebarItemKind.Artists;
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();
        // An unscoped Artists view is deliberately empty rather than the whole
        // library - see GetBaseTracksForFilter.
        Assert.Empty(vm.Rows);

        vm.SetSelectedSubItems(new[] { "X" });
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("a", Assert.Single(vm.Rows).Track.Title);
    }

    [AvaloniaFact]
    public async Task History_excludes_tracks_that_have_never_been_played()
    {
        var (vm, host) = Make(
            Song("played", "One", "X", DateTimeOffset.UtcNow),
            Song("never", "One", "X"));
        host.CurrentKind = SidebarItemKind.History;
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("played", Assert.Single(vm.Rows).Track.Title);
    }

    [AvaloniaFact]
    public async Task The_search_filter_narrows_the_rows()
    {
        var (vm, _) = Make(Song("hello", "One", "X"), Song("goodbye", "One", "X"));
        vm.Repopulate();

        vm.FilterText = "hello";
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("hello", Assert.Single(vm.Rows).Track.Title);
    }

    // Songs finds a song by its title; an album's or an artist's name is
    // what the other two pages find.
    [AvaloniaFact]
    public async Task Songs_are_found_by_title_alone()
    {
        var (vm, _) = Make(Song("Heroes", "Heroes", "Bowie"), Song("Sons of the Silent Age", "Heroes", "Bowie"));
        vm.Repopulate();

        vm.FilterText = "heroes";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal("Heroes", Assert.Single(vm.Rows).Track.Title);

        vm.FilterText = "bowie";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Empty(vm.Rows);
    }

    // Any other list of songs - here a playlist - still finds one by anything.
    [AvaloniaFact]
    public async Task A_playlist_still_finds_a_song_by_its_artist()
    {
        var song = Song("Heroes", "Heroes", "Bowie");
        var (vm, host) = Make(song);
        host.CurrentKind = SidebarItemKind.Playlist;
        host.CurrentPlaylist = new Playlist("Mix", [song]);
        vm.Repopulate();

        vm.FilterText = "bowie";
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Single(vm.Rows);
    }

    [AvaloniaFact]
    public async Task The_album_grid_finds_an_album_by_its_name_alone()
    {
        var (vm, host) = Make(Song("Karma Police", "OK Computer", "Radiohead"), Song("Heroes", "Heroes", "Bowie"));
        host.CurrentKind = SidebarItemKind.Albums;
        vm.Repopulate();

        vm.FilterText = "computer";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal("OK Computer", Assert.Single(vm.AlbumGridTiles).Name);

        vm.FilterText = "karma";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Empty(vm.AlbumGridTiles);

        vm.FilterText = "radiohead";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Empty(vm.AlbumGridTiles);
    }

    [AvaloniaFact]
    public void History_keeps_sort_state_independent_of_songs()
    {
        var (vm, host) = Make(Song("a", "One", "X"));

        Assert.Equal("TrackNumber", vm.SortColumn);

        host.CurrentKind = SidebarItemKind.History;
        Assert.Equal("LastPlayed", vm.SortColumn);
        Assert.False(vm.SortAscending);

        vm.SortByColumn("Title");
        Assert.Equal("Title", vm.SortColumn);
        // Only Songs' sort is persisted - History's is per-session.
        Assert.Null(host.PersistedSort);

        host.CurrentKind = SidebarItemKind.Songs;
        Assert.Equal("TrackNumber", vm.SortColumn);

        vm.SortByColumn("Album");
        Assert.Equal(("Album", true), host.PersistedSort);

        host.CurrentKind = SidebarItemKind.History;
        Assert.Equal("Title", vm.SortColumn);
    }

    [AvaloniaFact]
    public void Clicking_the_same_column_twice_reverses_it()
    {
        var (vm, _) = Make(Song("a", "One", "X"));

        vm.SortByColumn("Title");
        Assert.True(vm.SortAscending);

        vm.SortByColumn("Title");
        Assert.False(vm.SortAscending);

        // A different column always starts ascending again.
        vm.SortByColumn("Album");
        Assert.True(vm.SortAscending);
    }

    [AvaloniaFact]
    public void Expanding_an_album_is_an_accordion_and_orders_by_disc_then_track()
    {
        var (vm, _) = Make(
            new Track { Title = "second", Album = "One", DiscNumber = 1, TrackNumber = 2, Path = "/2.mp3" },
            new Track { Title = "first",  Album = "One", DiscNumber = 1, TrackNumber = 1, Path = "/1.mp3" },
            new Track { Title = "disc2",  Album = "One", DiscNumber = 2, TrackNumber = 1, Path = "/3.mp3" },
            new Track { Title = "other",  Album = "Two", DiscNumber = 1, TrackNumber = 1, Path = "/4.mp3" });
        vm.Repopulate();

        var one = vm.AlbumGridTiles.Single(t => t.Name == "One");
        var two = vm.AlbumGridTiles.Single(t => t.Name == "Two");

        vm.ToggleAlbumExpanded(one);
        Assert.Equal("One", vm.ExpandedAlbumName);
        Assert.Equal(new[] { "first", "second", "disc2" }, vm.ExpandedAlbumTracks.Select(t => t.Title));

        // Clicking the already-expanded album collapses it.
        vm.ToggleAlbumExpanded(one);
        Assert.Null(vm.ExpandedAlbumName);
        Assert.Empty(vm.ExpandedAlbumTracks);

        // A different one switches straight to it.
        vm.ToggleAlbumExpanded(one);
        vm.ToggleAlbumExpanded(two);
        Assert.Equal("Two", vm.ExpandedAlbumName);
    }

    [AvaloniaFact]
    public void Dragging_off_the_album_grid_resolves_tracks_by_album()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        vm.Repopulate();

        host.CurrentKind = SidebarItemKind.Albums;
        Assert.Equal(new[] { "a" }, vm.GetTracksForSubListItems(new[] { "One" }).Select(t => t.Title));

        host.CurrentKind = SidebarItemKind.Artists;
        Assert.Equal(new[] { "b" }, vm.GetTracksForSubListItems(new[] { "Y" }).Select(t => t.Title));

        host.CurrentKind = SidebarItemKind.Songs;
        Assert.Empty(vm.GetTracksForSubListItems(new[] { "One" }));
    }

    [AvaloniaFact]
    public async Task Tile_grids_are_built_only_for_the_views_that_paint_them()
    {
        var (vm, host) = Make(Song("a", "One", "X"));
        host.CurrentKind = SidebarItemKind.Songs;
        vm.Repopulate();

        vm.FilterText = "nothing matches";
        await vm.RebuildRowsImmediatelyAsync();
        // Repopulate seeded the grids from the unfiltered library; a Songs-view
        // rebuild must not spend a full pass re-deriving them. See Tier 1.5.
        Assert.Single(vm.AlbumGridTiles);

        host.CurrentKind = SidebarItemKind.Albums;
        await vm.RebuildRowsImmediatelyAsync();
        // On Albums the filter does reach the grid.
        Assert.Empty(vm.AlbumGridTiles);
    }

    [AvaloniaFact]
    public async Task A_playlist_view_shows_the_playlists_own_order()
    {
        var b = Song("b", "One", "X");
        var a = Song("a", "One", "X");
        var (vm, host) = Make(a, b);
        host.CurrentKind = SidebarItemKind.Playlist;
        host.CurrentPlaylist = new Playlist("mine", new List<Track> { b, a });
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();

        // Playlists have a user-defined, drag-reorderable order, so the column
        // sort is ignored while viewing one.
        Assert.Equal(new[] { "b", "a" }, vm.Rows.Select(r => r.Track.Title));
    }
}
