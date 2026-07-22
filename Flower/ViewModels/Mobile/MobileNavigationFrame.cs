using System;
using System.Collections.Generic;

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
    RecentlyAdded,
    AlbumGrid,
    ArtistPicker,
    ArtistAlbumGrid,
    PlaylistPicker,
    TrackList,
    SearchResults,
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
public sealed record MobileNavigationFrame(
    MobileTab Tab,
    bool HasDrilledIn,
    string? SelectedArtistName,
    bool HasDrilledIntoArtistAlbum,
    SidebarItem? SidebarItem,
    string? SubItem,
    string? SearchQuery,
    IReadOnlyList<TrackRowViewModel>? FrozenRows,
    AlbumTileViewModel? FrozenHeader)
{
    public MobileScreenKind ScreenKind => Classify(Tab, HasDrilledIn, HasDrilledIntoArtistAlbum);

    // Whether this frame's TrackList (if any) is showing one album's own
    // tracks vs. a flat Songs/playlist list - mirrors
    // MobileMainViewModel.IsShowingAlbumTrackList's own condition exactly.
    public bool IsAlbumTrackList => SidebarItem?.Kind == SidebarItemKind.Albums && SubItem != null;

    // Whether this frame's TrackList (if any) is one specific playlist's own
    // tracks - mirrors MobileMainViewModel.IsShowingPlaylistTracks/
    // CurrentPlaylist's own condition exactly.
    public bool IsPlaylistTrackList => Tab == MobileTab.Playlists && HasDrilledIn && SidebarItem?.Playlist != null;

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
        if (tab == MobileTab.Albums && !hasDrilledIn)
            return MobileScreenKind.AlbumGrid;
        if (tab == MobileTab.Artists && !hasDrilledIn)
            return MobileScreenKind.ArtistPicker;
        if (tab == MobileTab.Artists && hasDrilledIn && !hasDrilledIntoArtistAlbum)
            return MobileScreenKind.ArtistAlbumGrid;
        if (tab == MobileTab.Playlists && !hasDrilledIn)
            return MobileScreenKind.PlaylistPicker;
        if (tab == MobileTab.RecentlyAdded && !hasDrilledIn)
            return MobileScreenKind.RecentlyAdded;
        return MobileScreenKind.TrackList;
    }
}
