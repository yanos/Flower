using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.ViewModels.Mobile;

// Which tabs a phone's bar shows, and in what order - chosen in Settings (see
// MobileMainViewModel's "Tab bar" section) and kept in AppSettings.MobileTabs
// by MobileTab name. The bar's order is also the order a tap or a swipe reads
// as left or right, so nothing about direction comes from MobileTab's own
// declaration any more.
public static class MobileTabs
{
    // Every tab there is, in the order Settings lists the ones not shown.
    public static readonly IReadOnlyList<MobileTab> All =
    [
        MobileTab.RecentlyAdded, MobileTab.Albums, MobileTab.Artists, MobileTab.Songs,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.Search,
    ];

    // Recently Added is left out: the Albums grid can already be sorted by
    // date added from its own menu, and the bar has room for six.
    public static readonly IReadOnlyList<MobileTab> Default =
    [
        MobileTab.Albums, MobileTab.Artists, MobileTab.Songs,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.Search,
    ];

    // What the oval holds with every label on one line on a 375px phone -
    // MobileTabBarLayoutTests measures it. A seventh would clip "Playlists".
    public const int MaxShown = 6;

    // A saved choice, cleaned: names that are no longer tabs and repeats are
    // dropped, anything past MaxShown is cut, and nothing left at all is the
    // default rather than a bar with no tabs.
    public static IReadOnlyList<MobileTab> Parse(IEnumerable<string>? saved)
    {
        if (saved == null)
            return Default;

        var tabs = new List<MobileTab>();
        foreach (var name in saved)
        {
            if (Enum.TryParse<MobileTab>(name, out var tab) && All.Contains(tab) && !tabs.Contains(tab))
                tabs.Add(tab);
        }

        return tabs.Count == 0 ? Default : tabs.Take(MaxShown).ToList();
    }

    // The label under the tab's icon, and its name in Settings.
    public static string Label(MobileTab tab) => tab switch
    {
        MobileTab.RecentlyAdded => "Recent",
        _ => tab.ToString(),
    };
}

// One line of Settings' tab list: whether the tab is in the bar, and where.
public sealed record MobileTabSettingRow(
    MobileTab Tab,
    bool IsShown,
    bool CanToggle,
    bool CanMoveUp,
    bool CanMoveDown)
{
    public string Label => MobileTabs.Label(Tab);
}
