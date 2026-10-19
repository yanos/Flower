using System.Collections.Generic;
using System.Linq;

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

    private static List<string> Titles(IReadOnlyList<IReadOnlyList<ActionMenuEntry>> groups) =>
        ActionMenus.Flat(groups).Select(e => e.Title).ToList();

    [Fact]
    public void A_song_menu_offers_only_what_can_be_done_to_these_songs()
    {
        Assert.Equal(["Play Next", "Add to Queue", "Add to Playlist", "Get Info"],
            Titles(ActionMenus.ForSongs(Nothing, canRemoveFromQueue: false, canLocateFiles: false)));

        var everything = new MenuFacts(Downloadable: 1, IsBulkDownloading: false, LocalFiles: 1, Removable: 1);
        Assert.Equal(
            ["Play Next", "Add to Queue", "Add to Playlist", "Remove from Queue",
             "Get Info", "Locate File", "Download",
             "Delete Local File", "Remove from Library"],
            Titles(ActionMenus.ForSongs(everything, canRemoveFromQueue: true, canLocateFiles: true)));
    }

    // These menus act on a whole selection, so the count is in the words.
    [Fact]
    public void A_selection_is_counted_in_what_the_entries_say()
    {
        var several = new MenuFacts(Downloadable: 3, IsBulkDownloading: false, LocalFiles: 2, Removable: 4);

        var titles = Titles(ActionMenus.ForSongs(several, canRemoveFromQueue: false, canLocateFiles: false));

        Assert.Contains("Download 3 Songs", titles);
        Assert.Contains("Delete 2 Local Files", titles);
        Assert.Contains("Remove 4 Songs from Library", titles);
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

        Assert.Equal([MenuAction.Play, MenuAction.Shuffle], entries.Take(2).Select(e => e.Action));
        Assert.True(entries[0].IsPrimary);
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
        Assert.Equal(MenuAction.DeletePlaylist, entries[^1].Action);
        Assert.True(entries[^1].IsDestructive);
    }

    [Fact]
    public void An_album_on_the_Continue_Playing_shelf_can_be_taken_off_it()
    {
        Assert.DoesNotContain(ActionMenus.Flat(ActionMenus.ForAlbum(Nothing)), e => e.Action == MenuAction.ForgetProgress);
        Assert.Contains(ActionMenus.Flat(ActionMenus.ForAlbum(Nothing, isContinuing: true)), e => e.Action == MenuAction.ForgetProgress);
    }
}
