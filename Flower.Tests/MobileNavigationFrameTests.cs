using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Tests;

// Pure logic only, no MobileMainViewModel/Library/Dispatcher involved - see
// MobileNavigationFrame's own doc comment for why ScreenStackPanel needs
// this classification/scoping to be data rather than closures, and the
// swipe-back navigation plan's own "Unit-testable" section for why these
// specific behaviors (Classify, ScopeKey, and FrozenRows capture-at-push-time)
// are the ones worth pinning down here.
public class MobileNavigationFrameTests
{
    private static MobileNavigationFrame Frame(
        MobileTab tab, bool hasDrilledIn = false, string? artistName = null, bool hasDrilledIntoArtistAlbum = false,
        SidebarItem? sidebarItem = null, string? subItem = null, string? searchQuery = null,
        IReadOnlyList<TrackRowViewModel>? frozenRows = null)
        => new(tab, hasDrilledIn, artistName, hasDrilledIntoArtistAlbum, sidebarItem, subItem, searchQuery, frozenRows, null);

    // Mirrors MobileMainViewModel's IsShowingAlbumGrid/IsShowingArtistPicker/
    // IsShowingArtistAlbumGrid/IsShowingPlaylistPicker/IsShowingRecentlyAddedAlbums/
    // IsShowingTrackList exactly - if these two ever drift, ScreenStackPanel
    // would show the wrong screen for what the live boolean properties say
    // is on screen.
    [Theory]
    [InlineData(MobileTab.RecentlyAdded, false, false, MobileScreenKind.RecentlyAdded)]
    [InlineData(MobileTab.RecentlyAdded, true, false, MobileScreenKind.TrackList)]
    [InlineData(MobileTab.Songs, false, false, MobileScreenKind.TrackList)]
    [InlineData(MobileTab.Albums, false, false, MobileScreenKind.AlbumGrid)]
    [InlineData(MobileTab.Albums, true, false, MobileScreenKind.TrackList)]
    [InlineData(MobileTab.Artists, false, false, MobileScreenKind.ArtistPicker)]
    [InlineData(MobileTab.Artists, true, false, MobileScreenKind.ArtistAlbumGrid)]
    [InlineData(MobileTab.Artists, true, true, MobileScreenKind.TrackList)]
    [InlineData(MobileTab.Playlists, false, false, MobileScreenKind.PlaylistPicker)]
    [InlineData(MobileTab.Playlists, true, false, MobileScreenKind.TrackList)]
    [InlineData(MobileTab.Search, false, false, MobileScreenKind.SearchResults)]
    [InlineData(MobileTab.Search, true, true, MobileScreenKind.SearchResults)]
    public void Classify_matches_MobileMainViewModel_IsShowingX_definitions(
        MobileTab tab, bool hasDrilledIn, bool hasDrilledIntoArtistAlbum, MobileScreenKind expected)
    {
        Assert.Equal(expected, MobileNavigationFrame.Classify(tab, hasDrilledIn, hasDrilledIntoArtistAlbum));
    }

    [Fact]
    public void ScopeKey_distinguishes_different_albums_but_reuses_the_same_one()
    {
        var albumsItem = new SidebarItem(SidebarItemKind.Albums, "Albums");
        var dawn = Frame(MobileTab.Albums, hasDrilledIn: true, sidebarItem: albumsItem, subItem: "Dawn");
        var dawnAgain = Frame(MobileTab.Albums, hasDrilledIn: true, sidebarItem: albumsItem, subItem: "Dawn");
        var dusk = Frame(MobileTab.Albums, hasDrilledIn: true, sidebarItem: albumsItem, subItem: "Dusk");

        Assert.Equal(dawn.ScopeKey, dawnAgain.ScopeKey);
        Assert.NotEqual(dawn.ScopeKey, dusk.ScopeKey);
    }

    [Fact]
    public void ScopeKey_distinguishes_different_artists_own_album_grids()
    {
        var aurora = Frame(MobileTab.Artists, hasDrilledIn: true, artistName: "Aurora");
        var nova = Frame(MobileTab.Artists, hasDrilledIn: true, artistName: "Nova");

        Assert.NotEqual(aurora.ScopeKey, nova.ScopeKey);
    }

    [Fact]
    public void ScopeKey_is_stable_for_screens_with_only_one_possible_instance()
    {
        Assert.Equal(
            Frame(MobileTab.RecentlyAdded).ScopeKey,
            Frame(MobileTab.RecentlyAdded).ScopeKey);
        Assert.Equal(
            Frame(MobileTab.Search, searchQuery: "one").ScopeKey,
            Frame(MobileTab.Search, searchQuery: "two").ScopeKey);
    }

    [Fact]
    public void IsAlbumTrackList_true_only_for_an_albums_sidebar_item_with_a_sub_item()
    {
        var albumsItem = new SidebarItem(SidebarItemKind.Albums, "Albums");
        var songsItem = new SidebarItem(SidebarItemKind.Songs, "Songs");

        Assert.True(Frame(MobileTab.Albums, sidebarItem: albumsItem, subItem: "Dawn").IsAlbumTrackList);
        Assert.False(Frame(MobileTab.Albums, sidebarItem: albumsItem, subItem: null).IsAlbumTrackList);
        Assert.False(Frame(MobileTab.Songs, sidebarItem: songsItem, subItem: null).IsAlbumTrackList);
    }

    [Fact]
    public void Stack_push_pop_and_CanGoBack_transitions()
    {
        var history = new Stack<MobileNavigationFrame>();
        Assert.Empty(history);

        history.Push(Frame(MobileTab.RecentlyAdded));
        Assert.Single(history);

        history.Push(Frame(MobileTab.Albums, hasDrilledIn: true, subItem: "Dawn"));
        Assert.Equal(2, history.Count);

        var top = history.Pop();
        Assert.Equal(MobileScreenKind.TrackList, top.ScreenKind);
        Assert.Single(history);

        var last = history.Pop();
        Assert.Equal(MobileScreenKind.RecentlyAdded, last.ScreenKind);
        Assert.Empty(history);
    }

    // MobileMainViewModel.GoBack/GoForward share the same shape: popping one
    // stack always pushes what's being left onto the OTHER one, so a swipe
    // back can always be immediately undone by swiping forward and vice
    // versa - a real back/forward pair, not just a one-way undo. This
    // exercises that shape directly against bare stacks, independent of the
    // ViewModel (PushHistory's own "a new navigation clears the forward
    // stack" behavior needs the real ViewModel to exercise, since it isn't
    // pure stack mechanics).
    [Fact]
    public void Back_and_forward_stacks_mirror_each_other()
    {
        var back = new Stack<MobileNavigationFrame>();
        var forward = new Stack<MobileNavigationFrame>();

        var songs = Frame(MobileTab.Songs);
        var album = Frame(MobileTab.Albums, hasDrilledIn: true, subItem: "Dawn");

        // Drilling from Songs into an album pushes Songs onto the back stack.
        back.Push(songs);

        // Going back: pop Songs off the back stack, push what's being left (the album) onto forward.
        var poppedBack = back.Pop();
        Assert.Equal(songs, poppedBack);
        forward.Push(album);
        Assert.Empty(back);
        Assert.Single(forward);

        // Going forward: pop the album off the forward stack, push what's being left (Songs) onto back.
        var poppedForward = forward.Pop();
        Assert.Equal(album, poppedForward);
        back.Push(songs);
        Assert.Empty(forward);
        Assert.Single(back);
    }

    private static Track T(string title) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Path = $"/music/{title}.mp3",
    };

    // Directly regression-tests the GoToCurrentlyPlayingAlbumCommand scenario
    // from the swipe-back plan's Context section, without any UI: a frame
    // pushed while leaving a track-list screen must keep showing what that
    // screen actually held at that moment, even after the live row
    // collection it was copied from is later mutated into something else -
    // otherwise a kept-alive "one back" TrackListScreenView would reveal
    // whatever is currently live instead of what the user actually left.
    [Fact]
    public void FrozenRows_reflects_state_at_push_time_not_afterward()
    {
        var songsTracks = new List<Track> { T("Sunrise"), T("Nightfall") };
        var liveRows = TrackListBuilder.Build(songsTracks, null, "Title", true).ToList();

        var history = new Stack<MobileNavigationFrame>();
        history.Push(Frame(MobileTab.Songs, frozenRows: liveRows.ToList()));

        // Main.Rows gets wholesale-replaced by a later navigation/rescan -
        // the live list mutates in place here to simulate that.
        liveRows.Clear();
        liveRows.AddRange(TrackListBuilder.Build(new List<Track> { T("Different Album Track") }, null, "Title", true));

        var frame = history.Pop();
        Assert.Equal(2, frame.FrozenRows!.Count);
        Assert.Equal("Nightfall", frame.FrozenRows[0].Track.Title);
        Assert.Equal("Sunrise", frame.FrozenRows[1].Track.Title);
    }
}
