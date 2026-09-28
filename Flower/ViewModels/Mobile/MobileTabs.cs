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
        MobileTab.Home, MobileTab.Albums, MobileTab.Artists, MobileTab.Songs,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.Search,
    ];

    // Home first, so it is where the app opens - the first tab in the bar is.
    // Seven is one past what is drawn at full size, so the bar starts compact;
    // any tab can be taken out of it in Settings.
    public static readonly IReadOnlyList<MobileTab> Default =
    [
        MobileTab.Home, MobileTab.Albums, MobileTab.Artists, MobileTab.Songs,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.Search,
    ];

    // What the oval holds at full size with every label on one line on a
    // 375px phone. Past it the tabs are drawn smaller (Button.tab.compact in
    // MobileMainView.axaml) - MobileTabBarLayoutTests measures both.
    public const int RoomyShown = 6;

    // A saved choice, cleaned: names that are no longer tabs and repeats are
    // dropped, and nothing left at all is the default rather than a bar with
    // no tabs.
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

        return tabs.Count == 0 ? Default : tabs;
    }

    // The label under the tab's icon, and its name in Settings.
    public static string Label(MobileTab tab) => tab.ToString();
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
