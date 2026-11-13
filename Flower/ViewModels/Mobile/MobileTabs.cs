using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;

using Material.Icons;

namespace Flower.ViewModels.Mobile;

// Which tabs a phone's bar shows, and in what order - chosen on Settings'
// Navigation Bar page (see MobileMainViewModel's "Tab bar" section) and kept in AppSettings.MobileTabs
// by MobileTab name. The bar's order is also the order a tap or a swipe reads
// as left or right, so nothing about direction comes from MobileTab's own
// declaration any more.
public static class MobileTabs
{
    // Every tab there is, in the order Settings lists the ones not shown -
    // History beside the Queue, as the desktop sidebar has them. One more
    // than the bar holds (MaxShown), so at least one is always left to the
    // Navigation Bar page; out of the box that is History.
    public static readonly IReadOnlyList<MobileTab> All =
    [
        MobileTab.Home, MobileTab.Songs, MobileTab.Albums, MobileTab.Artists,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.History, MobileTab.Search,
    ];

    // The most the bar shows at once. Past seven a tab is narrower than a
    // thumb (Button.tab.compact is already the squeeze for seven), so an
    // eighth is turned on only by turning another off.
    public const int MaxShown = 7;

    // Home first, so it is where the app opens - the first tab in the bar is.
    // Songs, Albums, Artists in the desktop sidebar's order, so the library
    // reads the same way round on both.
    // Seven is one past what is drawn at full size, so the bar starts compact;
    // any tab can be taken out of it in Settings.
    public static readonly IReadOnlyList<MobileTab> Default =
    [
        MobileTab.Home, MobileTab.Songs, MobileTab.Albums, MobileTab.Artists,
        MobileTab.Playlists, MobileTab.Queue, MobileTab.Search,
    ];

    // What the oval holds at full size with every label on one line on a
    // 375px phone. Past it the tabs are drawn smaller (Button.tab.compact in
    // MobileMainView.axaml).
    public const int RoomyShown = 6;

    // A saved choice, cleaned: names that are no longer tabs and repeats are
    // dropped, anything past MaxShown too, and nothing left at all is the
    // default rather than a bar with no tabs.
    public static IReadOnlyList<MobileTab> Parse(IEnumerable<string>? saved)
    {
        if (saved == null)
            return Default;

        var tabs = new List<MobileTab>();
        foreach (var name in saved)
        {
            if (tabs.Count == MaxShown)
                break;
            if (Enum.TryParse<MobileTab>(name, out var tab) && All.Contains(tab) && !tabs.Contains(tab))
                tabs.Add(tab);
        }

        return tabs.Count == 0 ? Default : tabs;
    }

    // Showing appends; hiding keeps at least one tab. Enforce the same limits
    // as the settings switches, including commands invoked with a stale row.
    public static List<MobileTab> Toggle(IReadOnlyList<MobileTab> visible, MobileTab tab)
    {
        var tabs = visible.ToList();
        if (tabs.Contains(tab))
        {
            if (tabs.Count > 1)
                tabs.Remove(tab);
        }
        else if (tabs.Count < MaxShown)
        {
            tabs.Add(tab);
        }
        return tabs;
    }

    // A drop index is the position after lifting the tab out of the bar.
    public static List<MobileTab> Move(IReadOnlyList<MobileTab> visible, MobileTab tab, int index)
    {
        var tabs = visible.ToList();
        if (tabs.Remove(tab))
            tabs.Insert(Math.Clamp(index, 0, tabs.Count), tab);
        return tabs;
    }

    // The label under the tab's icon, and its name in Settings.
    public static string Label(MobileTab tab) => tab.ToString();

    // The glyph the bar draws for the tab. Songs has none of Material's: it is
    // SongNoteGeometry (App.axaml), and a view drawing a tab asks
    // UsesSongNote first.
    public static MaterialIconKind Icon(MobileTab tab) => tab switch
    {
        MobileTab.Home => MaterialIconKind.HomeOutline,
        MobileTab.Albums => MaterialIconKind.Album,
        MobileTab.Artists => MaterialIconKind.AccountOutline,
        MobileTab.Playlists => MaterialIconKind.PlaylistPlay,
        MobileTab.Queue => MaterialIconKind.PlaylistMusicOutline,
        MobileTab.Search => MaterialIconKind.Magnify,
        // The desktop sidebar's History glyph.
        MobileTab.History => MaterialIconKind.ClockTimeEightOutline,
        _ => MaterialIconKind.MusicNote,
    };

    public static bool UsesSongNote(MobileTab tab) => tab == MobileTab.Songs;

    // How much bigger than its box each Material glyph has to be drawn to
    // span as much of it as the Songs note does, which is stretched to fill
    // its own - the same enlargements the bar's icons carry, worked out in
    // MobileMainView.axaml's Button.tab comment (24 over the glyph's extent in
    // units).
    public static double IconScale(MobileTab tab) => tab switch
    {
        MobileTab.Home => 27.0 / 24,
        MobileTab.Albums => 29.0 / 24,
        MobileTab.Artists => 36.0 / 24,
        MobileTab.Playlists => 30.0 / 24,
        MobileTab.Queue => 29.0 / 24,
        MobileTab.Search => 33.0 / 24,
        // A circle about 20 of its 24 units across, like the Queue's glyph.
        MobileTab.History => 29.0 / 24,
        _ => 1,
    };
}

// One line of the Navigation Bar page: a tab, and whether it is in the bar.
// Where it is in the bar is the row's place in VisibleTabRows.
public sealed record MobileTabSettingRow(
    MobileTab Tab,
    bool IsShown,
    bool CanToggle)
{
    // The box each row's icon is laid out in, and how far past it the glyph
    // is drawn to come out the same size as the rest (IconScale).
    private const double IconBox = 22;

    public string Label => MobileTabs.Label(Tab);
    public MaterialIconKind Icon => MobileTabs.Icon(Tab);
    public bool UsesSongNote => MobileTabs.UsesSongNote(Tab);
    public double IconSize => IconBox * MobileTabs.IconScale(Tab);
    public Thickness IconMargin => new(-(IconSize - IconBox) / 2);
}
