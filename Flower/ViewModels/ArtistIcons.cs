using System;
using System.Collections.Generic;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// The one place an artist's icon comes from: their picture when one is found
// online, and a collage of their covers when not (AlbumCollage.ForArtist).
// The desktop artist page's header, the desktop Artists column, Search's
// artist rows and the phone's Artists tab all ask here, so one artist is
// drawn the same everywhere, from the same songs, rather than each place
// making its own and the four drifting apart.
//
// Two sizes of the one icon: thumbnail for a row, the full size for a page
// header. A row cannot take the header's: a column of hundreds of artists
// would hold a full-size picture for every one scrolled past.
//
// Made on first asking, since the lists are virtualised and only the rows on
// screen ask, and all dropped when the library changes
// (LibraryBrowserViewModel.Repopulate), when an artist's songs may have.
//
// Kept only while something draws them. An icon holds its pictures, and
// keeping every one ever asked for kept the covers of every artist scrolled
// past, which on a phone's Artists tab was hundreds of megabytes after one
// fling. An icon made again is of the same albums, and finds its covers in
// AlbumArtLoader's caches, which are the ones that bound what is kept.
public sealed class ArtistIcons
{
    private IReadOnlyList<Track> _library = [];
    private Dictionary<string, List<Track>>? _tracksByArtist;
    private readonly Dictionary<string, WeakReference<AlbumCollage>> _thumbnails = new(ArtistNames.Comparer);
    private readonly Dictionary<string, WeakReference<AlbumCollage>> _headers = new(ArtistNames.Comparer);

    public void LibraryChanged(IReadOnlyList<Track> library)
    {
        _library = library;
        _tracksByArtist = null;
        _thumbnails.Clear();
        _headers.Clear();
    }

    // Empty for a name with no songs behind it.
    public AlbumCollage For(string artist, bool thumbnail)
    {
        var icons = thumbnail ? _thumbnails : _headers;
        if (icons.TryGetValue(artist, out var weak) && weak.TryGetTarget(out var icon))
            return icon;

        _tracksByArtist ??= BrowseSort.TracksByArtist(_library);
        if (!_tracksByArtist.TryGetValue(artist, out var tracks))
            return AlbumCollage.Empty;

        icon = AlbumCollage.ForArtist(artist, tracks, thumbnail);
        icons[artist] = new WeakReference<AlbumCollage>(icon);
        return icon;
    }
}
