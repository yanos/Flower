using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Tests.ViewModels.Mobile;

// Screen classification and cache identity require no running dispatcher.
public class MobileNavigationFrameTests
{
    private static MobileNavigationFrame Frame(
        MobileTab tab, bool hasDrilledIn = false, string? artistName = null, bool hasDrilledIntoArtistAlbum = false,
        SidebarItem? sidebarItem = null, string? subItem = null, string? searchQuery = null)
        => new(tab, hasDrilledIn, artistName, hasDrilledIntoArtistAlbum, sidebarItem, subItem, searchQuery, null, null, null);

    // ScreenStackPanel chooses the view to create from this classification.
    [Theory]
    [InlineData(MobileTab.Home, false, false, MobileScreenKind.Home)]
    [InlineData(MobileTab.Home, true, false, MobileScreenKind.TrackList)]
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
    public void Classify_selects_the_screen_for_each_navigation_state(
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
            Frame(MobileTab.Home).ScopeKey,
            Frame(MobileTab.Home).ScopeKey);
        Assert.Equal(
            Frame(MobileTab.Search, searchQuery: "one").ScopeKey,
            Frame(MobileTab.Search, searchQuery: "two").ScopeKey);
    }
}
