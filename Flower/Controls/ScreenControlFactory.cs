using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;

using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

namespace Flower.Controls;

// Bounded LRU cache of one materialized control per distinct navigation
// scope (see MobileNavigationFrame.ScopeKey) - revisiting the same album/
// artist/playlist reuses its existing control (and any in-flight art
// loading it already started) instead of rebuilding from scratch, without
// growing unboundedly across a long session. Sized past the 3 slots
// ScreenStackPanel actually keeps alive at rest (current + one back + one
// forward) since a screen further away in history than that still holds its
// scope key until evicted, ready to be reused if the user navigates back
// into it before the cache cycles it out.
public sealed class ScreenControlFactory
{
    private const int MaxCached = 6;

    // Ordered oldest-first; the front is the next eviction candidate.
    private readonly List<(string Key, Control Control)> _cache = new();

    public Control GetOrCreate(MobileNavigationFrame frame)
    {
        var existing = _cache.FirstOrDefault(e => e.Key == frame.ScopeKey);
        if (existing.Control != null)
        {
            _cache.Remove(existing);
            _cache.Add(existing);
            return existing.Control;
        }

        var control = Create(frame.ScreenKind);
        _cache.Add((frame.ScopeKey, control));
        if (_cache.Count > MaxCached)
        {
            var evicted = _cache[0];
            _cache.RemoveAt(0);
            if (evicted.Control is TrackListScreenView trackList)
                trackList.Detach();
        }
        return control;
    }

    // A screen instance for a frame that must NOT share the cached control for
    // its scope - the caller already handed that one to another role, and a
    // Control has exactly one visual parent, so reusing it would empty the slot
    // it is really in. Deliberately outside the cache: this is a throwaway
    // preview belonging to one inert slot, and letting it into the cache would
    // let it be handed out later as the live screen for that scope, which is
    // exactly the recycling the cache's one-control-per-scope rule exists to
    // prevent. See ScreenStackPanel.PrepareInert.
    public static Control CreateDetached(MobileScreenKind kind) => Create(kind);

    private static Control Create(MobileScreenKind kind) => kind switch
    {
        MobileScreenKind.RecentlyAdded => new RecentlyAddedScreenView(),
        MobileScreenKind.AlbumGrid => new AlbumGridScreenView(),
        MobileScreenKind.ArtistPicker => new ArtistPickerScreenView(),
        MobileScreenKind.ArtistAlbumGrid => new ArtistAlbumGridScreenView(),
        MobileScreenKind.PlaylistPicker => new PlaylistPickerScreenView(),
        MobileScreenKind.TrackList => new TrackListScreenView(),
        MobileScreenKind.SearchResults => new SearchResultsScreenView(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
