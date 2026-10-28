using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The song and album menus, written once for the desktop's context menus and
// the phone's action sheets. They used to be listed separately per head, and
// had drifted: the phone's album menu could shuffle and the desktop's could
// not.
public class ActionMenusTests
{
    private static readonly MenuFacts Nothing = new(Downloadable: 0, IsBulkDownloading: false, LocalFiles: 0, Removable: 0);

    private static MenuAction[] Actions(IReadOnlyList<IReadOnlyList<ActionMenuEntry>> groups) =>
        ActionMenus.Flat(groups).Select(e => e.Action).Order().ToArray();

    [Fact]
    public void A_song_menu_offers_only_what_can_be_done_to_these_songs()
    {
        Assert.Equal(new[] { MenuAction.PlayNext, MenuAction.AddToQueue, MenuAction.AddToPlaylist, MenuAction.GetInfo }.Order(),
            Actions(ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false)));

        var everything = new MenuFacts(Downloadable: 1, IsBulkDownloading: false, LocalFiles: 1, Removable: 1);
        Assert.Equal(
            new[] { MenuAction.PlayNext, MenuAction.AddToQueue, MenuAction.AddToPlaylist, MenuAction.RemoveFromQueue,
                MenuAction.GetInfo, MenuAction.LocateFile, MenuAction.Download,
                MenuAction.DeleteLocalFiles, MenuAction.RemoveFromLibrary }.Order(),
            Actions(ActionMenus.ForSongs(everything, canRemoveFromQueue: true, canLocateFiles: true)));
    }

    // A song streamed from the server has no file here to show, and on a
    // device whose library is all the server's that is nearly every song.
    [Fact]
    public void Locate_File_is_offered_only_over_a_song_with_a_file_here()
    {
        var streamed = new MenuFacts(Downloadable: 1, IsBulkDownloading: false, LocalFiles: 0, Removable: 0);

        var entries = ActionMenus.Flat(ActionMenus.ForSongs(streamed, canRemoveFromQueue: false, canLocateFiles: true));

        Assert.DoesNotContain(entries, e => e.Action == MenuAction.LocateFile);
    }

    [Fact]
    public void Download_greys_while_a_batch_is_already_running()
    {
        var busy = new MenuFacts(Downloadable: 1, IsBulkDownloading: true, LocalFiles: 0, Removable: 0);

        var download = ActionMenus.Flat(ActionMenus.ForAlbum(busy)).Single(e => e.Action == MenuAction.Download);

        Assert.False(download.IsEnabled);
    }

    [Fact]
    public void An_album_can_be_played_in_order_or_shuffled_on_every_head()
    {
        var entries = ActionMenus.Flat(ActionMenus.ForAlbum(Nothing));

        Assert.Contains(entries, e => e.Action == MenuAction.Play);
        Assert.Contains(entries, e => e.Action == MenuAction.Shuffle);
    }

    // The phone's playlist row raises the album menu: no Get Info, since a
    // playlist's songs share nothing a tag says, and no Remove from Library
    // beside Delete Playlist, which is last because it cannot be undone.
    [Fact]
    public void Over_a_playlist_the_album_menu_trades_its_album_entries_for_the_playlist_s_own()
    {
        var facts = new MenuFacts(Downloadable: 0, IsBulkDownloading: false, LocalFiles: 2, Removable: 2);

        var entries = ActionMenus.Flat(ActionMenus.ForAlbum(facts, isPlaylist: true));

        Assert.DoesNotContain(entries, e => e.Action is MenuAction.GetInfo or MenuAction.RemoveFromLibrary);
        Assert.Contains(entries, e => e.Action == MenuAction.RenamePlaylist);
        Assert.Contains(entries, e => e.Action == MenuAction.DeletePlaylist);
    }

    // Both heads build a playlist's menu here, so a smart playlist's rules are
    // reachable from the phone's playlist row as well as the desktop sidebar.
    [Fact]
    public void Only_a_smart_playlist_offers_rule_editing_and_conversion()
    {
        var ordinary = ActionMenus.Flat(ActionMenus.ForAlbum(Nothing, isPlaylist: true)).Select(e => e.Action).ToList();
        var smart = ActionMenus.Flat(ActionMenus.ForAlbum(Nothing, isPlaylist: true, isSmart: true)).Select(e => e.Action).ToList();

        Assert.DoesNotContain(MenuAction.EditRules, ordinary);
        Assert.DoesNotContain(MenuAction.ConvertToOrdinary, ordinary);
        Assert.Contains(MenuAction.EditRules, smart);
        Assert.Contains(MenuAction.ConvertToOrdinary, smart);
    }

    [Fact]
    public void An_album_on_the_Continue_Playing_shelf_can_be_taken_off_it()
    {
        Assert.DoesNotContain(ActionMenus.Flat(ActionMenus.ForAlbum(Nothing)), e => e.Action == MenuAction.ForgetProgress);
        Assert.Contains(ActionMenus.Flat(ActionMenus.ForAlbum(Nothing, isContinuing: true)), e => e.Action == MenuAction.ForgetProgress);
    }

    // Where the songs are from, ahead of everything, as the player bar's
    // menu has it - and only when the caller says there is one place to go.
    [Fact]
    public void A_song_menu_offers_album_and_artist_navigation_when_available()
    {
        var both = ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false,
            canGoToAlbum: true, canGoToArtist: true);

        Assert.Contains(ActionMenus.Flat(both), e => e.Action == MenuAction.GoToAlbum);
        Assert.Contains(ActionMenus.Flat(both), e => e.Action == MenuAction.GoToArtist);

        var artistOnly = ActionMenus.Flat(ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false,
            canGoToArtist: true));
        Assert.DoesNotContain(artistOnly, e => e.Action == MenuAction.GoToAlbum);
        Assert.Contains(artistOnly, e => e.Action == MenuAction.GoToArtist);
    }

    [Fact]
    public void An_album_menu_offers_artist_navigation_when_available()
    {
        var menu = ActionMenus.ForAlbum(Nothing, canGoToArtist: true);

        Assert.Contains(ActionMenus.Flat(menu), e => e.Action == MenuAction.GoToArtist);
        Assert.DoesNotContain(ActionMenus.Flat(ActionMenus.ForAlbum(Nothing)), e => e.Action == MenuAction.GoToArtist);
    }

    [Fact]
    public void A_selection_goes_to_an_album_or_artist_only_when_it_shares_one()
    {
        Track Song(string? album, string? artist) => new() { Album = album, Artists = artist };

        Assert.True(MainViewModel.ShareAlbum([Song("Kid A", "Radiohead"), Song("Kid A", "Radiohead")]));
        Assert.False(MainViewModel.ShareAlbum([Song("Kid A", "Radiohead"), Song("Amnesiac", "Radiohead")]));
        Assert.True(MainViewModel.ShareArtist([Song("Kid A", "Radiohead"), Song("Amnesiac", "Radiohead")]));
        Assert.False(MainViewModel.ShareArtist([Song("Kid A", "Radiohead"), Song("Kid A", "Björk")]));
        Assert.False(MainViewModel.ShareAlbum([Song(null, null)]));
        Assert.False(MainViewModel.ShareArtist([Song("", "")]));
        Assert.False(MainViewModel.ShareAlbum([]));
    }

    [Fact]
    public void A_Recently_Played_song_can_be_taken_off_the_shelf()
    {
        Assert.DoesNotContain(ActionMenus.Flat(ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false)),
            e => e.Action == MenuAction.HideFromRecentlyPlayed);
        Assert.Contains(ActionMenus.Flat(ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false, isRecentlyPlayed: true)),
            e => e.Action == MenuAction.HideFromRecentlyPlayed);
    }
}
