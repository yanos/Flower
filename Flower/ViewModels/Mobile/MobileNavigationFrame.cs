using System;
using System.Collections.Generic;

using Avalonia;

using Material.Icons;

using Flower.Models;

namespace Flower.ViewModels.Mobile;

// One instance per distinct "screen" MobileMainViewModel can show - mirrors
// the IsShowingX boolean properties there exactly (see Classify), so the two
// can never drift. Used both for history entries in _navigationHistory
// (replacing the old closure-based Stack<Func<Task>>) and, via
// MobileMainViewModel.CurrentFrame, to describe the live/current screen too -
// the same Classify/ScopeKey logic drives both, so ScreenStackPanel doesn't
// need a second parallel notion of "what screen is this."
public enum MobileScreenKind
{
    Home,
    AlbumGrid,
    ArtistPicker,
    ArtistAlbumGrid,
    PlaylistPicker,
    TrackList,
    SearchResults,
    Queue,
}

// A snapshot of everything MobileMainViewModel.PushHistory used to capture
// in a closure's captured variables - now plain data, so it can be inspected
// (ScreenStackPanel/ScreenControlFactory need to know which screen a history
// entry represents) and unit tested without a running Dispatcher.
//
// FrozenRows/FrozenHeader are only ever non-null for a TrackList-kind frame
// (see MobileMainViewModel.PushHistory) - they exist so a kept-alive "one
// back" TrackListScreenView can render the exact rows/header it had when
// left, without binding to the live, wholesale-replaced Main.Rows the way
// the current screen does. Binding a kept-alive instance straight to
// Main.Rows would reintroduce the exact bug Stage 1 fixed (an ItemsControl's
// container generation isn't gated on IsVisible, so it kept trying to
// realize the whole library's rows in the background) - see
// TrackListScreenView.Freeze/ObserveLive.
//
// ScreenFilter is what the screen's pull-down filter box held when it was
// left (MobileMainViewModel.ScreenFilter), put back by Back/Forward/a tab tap
// and drawn by that screen's own ScreenTitle, so a kept-alive screen revealed
// by a swipe shows its own filter rather than the live one.
//
// No sheet is part of a frame, Now Playing included: it is an overlay raised
// over whatever screen is showing, so Back never brings it back - even after
// its album art pushed an album and took the sheet away in the same step.
public sealed record MobileNavigationFrame(
    MobileTab Tab,
    bool HasDrilledIn,
    string? SelectedArtistName,
    bool HasDrilledIntoArtistAlbum,
    SidebarItem? SidebarItem,
    string? SubItem,
    string? SearchQuery,
    string? ScreenFilter,
    IReadOnlyList<TrackRowViewModel>? FrozenRows,
    AlbumTileViewModel? FrozenHeader)
{
    public MobileScreenKind ScreenKind => Classify(Tab, HasDrilledIn, HasDrilledIntoArtistAlbum);

    // The header title for this frame's screen - mirrors
    // MobileMainViewModel.ScreenTitle's own logic exactly (see that
    // property's history), now derived per-frame so a swiped-in back/forward
    // preview shows its own title rather than the live VM's.
    //
    // Blank over one album's or one playlist's tracks: both draw their own
    // name in large type in the header right below (see
    // AlbumHeaderTemplates.axaml), and the only name this line had to give an
    // album was the sidebar item's, "Albums". That holds for an album reached
    // from an artist too: the artist's name is on the grid it was picked from,
    // and above the album it only repeated what the header already says.
    public string Title =>
        IsSearchScreen || IsTrackList
            ? string.Empty
            : Tab == MobileTab.Artists && SelectedArtistName != null
                ? SelectedArtistName
                : HasDrilledIn
                    ? (SidebarItem?.Name ?? Tab.ToString())
                    : Tab.ToString();

    // A tab's own top screen, whose title is the tab's name - and so is
    // drawn with the tab's icon before it (ScreenTitle), as the tab bar
    // draws it. Not a screen drilled into from one, whose title is an artist
    // or a playlist rather than the tab.
    public bool IsTabRoot => !HasDrilledIn && !IsSearchScreen && !(Tab == MobileTab.Artists && SelectedArtistName != null);

    // The tab's icon, at the title's size: Songs is the bar's own note
    // (TitleUsesSongNote), every other tab a Material glyph enlarged as the
    // bar enlarges it (MobileTabs.IconScale).
    public const double TitleIconBox = 30;
    public MaterialIconKind TitleIcon => MobileTabs.Icon(Tab);
    public bool TitleUsesSongNote => MobileTabs.UsesSongNote(Tab);
    public double TitleIconSize => TitleIconBox * MobileTabs.IconScale(Tab);
    public Thickness TitleIconMargin => new(-(TitleIconSize - TitleIconBox) / 2);

    // The Search tab's own always-visible box replaces the title there - see
    // MobileMainViewModel.IsShowingSearchTabBox's own former doc comment.
    public bool IsSearchScreen => ScreenKind == MobileScreenKind.SearchResults;

    // Whether pulling this screen down opens its filter oval. Not on Search,
    // whose box is already a search, nor on the Queue, which is in the order
    // it plays in and short of the one thing a filter would hide. Home has
    // one: Recently Added is three months of albums by default, which can be
    // a long page.
    public bool CanFilter => ScreenKind is not (MobileScreenKind.SearchResults or MobileScreenKind.Queue);

    // Whether this frame's TrackList (if any) is showing one album's own
    // tracks vs. a flat Songs/playlist list - mirrors
    // MobileMainViewModel.IsShowingAlbumTrackList's own condition exactly.
    public bool IsAlbumTrackList => SidebarItem?.Kind == SidebarItemKind.Albums && SubItem != null;

    // Whether this frame's TrackList (if any) is one specific playlist's own
    // tracks - mirrors MobileMainViewModel.IsShowingPlaylistTracks/
    // CurrentPlaylist's own condition exactly.
    public bool IsPlaylistTrackList => Tab == MobileTab.Playlists && HasDrilledIn && SidebarItem?.Playlist != null;

    // Either of the two above: a frame whose track list is one specific
    // album's or one specific playlist's - the two that get a header of their
    // own, which is also where "download everything on this screen" means
    // something (the header's own download button). Not the flat Songs list,
    // which is the whole library.
    public bool IsTrackList => IsAlbumTrackList || IsPlaylistTrackList;

    // The flat Songs list, the whole library - the one track list that is
    // nobody's one thing, and so has a title of its own (with its Shuffle
    // pill beside it - see ScreenTitle) rather than an album's header.
    public bool IsSongsList => ScreenKind == MobileScreenKind.TrackList && Tab == MobileTab.Songs && !HasDrilledIn;

    // Identifies which materialized control a frame should reuse - two
    // frames with the same ScopeKey are "the same screen" as far as
    // ScreenControlFactory's cache is concerned (e.g. revisiting the same
    // album keeps its art-loading state instead of restarting it). Only
    // TrackList and ArtistAlbumGrid vary by more than just ScreenKind -
    // every other screen kind has exactly one possible instance at a time.
    public string ScopeKey => ScreenKind switch
    {
        MobileScreenKind.TrackList => $"TrackList:{SidebarItem?.Name}:{SubItem}",
        MobileScreenKind.ArtistAlbumGrid => $"ArtistAlbumGrid:{SelectedArtistName}",
        _ => ScreenKind.ToString(),
    };

    public static MobileScreenKind Classify(MobileTab tab, bool hasDrilledIn, bool hasDrilledIntoArtistAlbum)
    {
        if (tab == MobileTab.Search)
            return MobileScreenKind.SearchResults;
        if (tab == MobileTab.Queue)
            return MobileScreenKind.Queue;
        if (tab == MobileTab.Albums && !hasDrilledIn)
            return MobileScreenKind.AlbumGrid;
        if (tab == MobileTab.Artists && !hasDrilledIn)
            return MobileScreenKind.ArtistPicker;
        if (tab == MobileTab.Artists && hasDrilledIn && !hasDrilledIntoArtistAlbum)
            return MobileScreenKind.ArtistAlbumGrid;
        if (tab == MobileTab.Playlists && !hasDrilledIn)
            return MobileScreenKind.PlaylistPicker;
        if (tab == MobileTab.Home && !hasDrilledIn)
            return MobileScreenKind.Home;
        return MobileScreenKind.TrackList;
    }
}
