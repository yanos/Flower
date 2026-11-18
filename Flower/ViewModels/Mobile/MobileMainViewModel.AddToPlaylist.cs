using System.Collections.Generic;
using System.Linq;

namespace Flower.ViewModels.Mobile;

// ── Add to Playlist ───────────────────────────────────────────────────
//
// The sheet's Recent section.
public partial class MobileMainViewModel
{
    // How many playlists the sheet's Recent section offers.
    private const int RecentPlaylistCount = 2;

    // The playlists last changed - a song added, a rename, an edit synced in -
    // at the top of the sheet, since the one a song goes into is most often
    // the one the last went into. Only playlists a song can go into, and only
    // when there are more of those than this shows: with two or fewer, every
    // one of them is at hand in the list already. Read as the sheet opens
    // (ActiveSheet raises it), not kept up to date while it is up.
    public IReadOnlyList<SidebarItem> RecentPlaylistTargets
    {
        get
        {
            var playlists = Main.SidebarItems.Where(i => i.Playlist is { IsOrdinary: true }).ToList();
            if (playlists.Count <= RecentPlaylistCount)
                return [];
            return [.. playlists.OrderByDescending(i => i.Playlist!.UpdatedAt).Take(RecentPlaylistCount)];
        }
    }

    public bool HasRecentPlaylistTargets => RecentPlaylistTargets.Count > 0;
}
