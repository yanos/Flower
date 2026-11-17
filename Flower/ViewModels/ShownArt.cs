using System;

using Avalonia.Media.Imaging;

namespace Flower.ViewModels;

// A tile's or a row's hold on the cover it shows: weak, so the cover lasts
// only while something draws it (the Image a row or tile is bound to holds it
// strongly) or AlbumArtLoader's caches keep it.
//
// The grids keep one tile per album and the lists one row per song for as long
// as the library is unchanged, and each used to keep its cover for as long as
// it lived. One fling down a phone's Albums tab then held a 768px cover, about
// 2.4 MB of pixels, for every album scrolled past: 2.6 GB on an iPhone.
// AlbumArtLoader's caches bound what is kept; a row or tile drawn again after
// its cover was let go loads it again, and usually finds it there.
internal struct ShownArt
{
    private WeakReference<Bitmap>? _art;

    public readonly Bitmap? Bitmap => _art != null && _art.TryGetTarget(out var art) ? art : null;

    // A cover was loaded and has since been let go, so it wants loading again
    // - as opposed to a song with no cover, which has nothing to let go.
    public readonly bool LetGo => _art != null && !_art.TryGetTarget(out _);

    public void Hold(Bitmap? art) => _art = art == null ? null : new WeakReference<Bitmap>(art);
}
