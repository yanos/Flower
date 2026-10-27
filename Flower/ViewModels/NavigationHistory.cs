using System;
using System.Collections.Generic;

using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// A page the desktop window has shown: the sidebar row, and on Artists the
// artist picked, on Albums the album open in the grid. By the row's identity
// rather than the row itself, since the sidebar rebuilds its rows - a folder
// opened, the playlists reloaded - and the page is still the same page.
public sealed record NavigationPlace(SidebarItemKind Kind, Guid? PlaylistId, string? DeviceName, string? Artist, AlbumTileKey? Album)
{
    public static NavigationPlace Of(SidebarItem item, string? artist, AlbumTileKey? album) =>
        new(item.Kind, item.Playlist?.Id, item.Device?.InstanceName,
            item.Kind == SidebarItemKind.Artists ? artist : null,
            item.Kind == SidebarItemKind.Albums ? album : null);

    public bool IsRow(SidebarItem item) =>
        item.Kind == Kind && item.Playlist?.Id == PlaylistId && item.Device?.InstanceName == DeviceName;

    // The same page, whichever album is open on it: opening one is not a
    // step of its own to go back through, as it is not on the phone either.
    public bool IsSamePageAs(NavigationPlace other) =>
        Kind == other.Kind && PlaylistId == other.PlaylistId && DeviceName == other.DeviceName && Artist == other.Artist;
}

// Back and Forward through the pages visited, as a browser has them: a new
// page drops whatever was ahead of it. Kept out of MainViewModel so it can be
// tested without one, as SidebarSelection is.
public sealed class NavigationHistory
{
    public const int Capacity = 100;

    private readonly List<NavigationPlace> _places = [];
    private int _index = -1;

    public NavigationPlace? Current => _index >= 0 ? _places[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index < _places.Count - 1;

    // The window now shows `place`. The page already current is updated in
    // place - which album is open on it - rather than visited again.
    public void Visit(NavigationPlace place)
    {
        if (Current is { } current && current.IsSamePageAs(place))
        {
            _places[_index] = place;
            return;
        }

        _places.RemoveRange(_index + 1, _places.Count - _index - 1);
        _places.Add(place);
        if (_places.Count > Capacity)
            _places.RemoveAt(0);
        _index = _places.Count - 1;
    }

    // The page before, or after, the current one, skipping - and forgetting -
    // any that is no longer there to go to (a playlist deleted, a server gone).
    // Null, and nothing moved, when there is none.
    public NavigationPlace? Back(Func<NavigationPlace, bool> isThere)
    {
        while (_index > 0)
        {
            if (isThere(_places[_index - 1]))
                return _places[--_index];
            _places.RemoveAt(_index - 1);
            _index--;
        }
        return null;
    }

    public NavigationPlace? Forward(Func<NavigationPlace, bool> isThere)
    {
        while (_index < _places.Count - 1)
        {
            if (isThere(_places[_index + 1]))
                return _places[++_index];
            _places.RemoveAt(_index + 1);
        }
        return null;
    }
}
