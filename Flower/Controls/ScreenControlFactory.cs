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
// growing unboundedly across a long session. Sized past the 2 slots
// ScreenStackPanel actually keeps alive at rest (current + one back) since
// a screen further back in history than that still holds its scope key
// until evicted, ready to be reused if the user drills back into it before
// the cache cycles it out.
public sealed class ScreenControlFactory
{
    private const int MaxCached = 4;

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
