using System.Collections.Generic;

using Flower.ViewModels.Mobile;

namespace Flower.Services;

// Reconciles a freshly built set of album tiles against the ones already on
// screen, reusing the AlbumTileViewModel showing a given album rather than
// replacing it - TrackRowMerge's job, for the grids.
//
// The track list got this treatment (see TrackRowMerge) and the tile grids did
// not, which left the album tile's own download button unable to finish an
// animation: clicking it starts a batch, the first track to arrive fires
// Library.TracksUpdated, and the rebuild that follows swapped in a brand-new
// tile with IsDownloading false while the download carried on against the
// discarded one. On screen that read as the spinner appearing for a moment,
// reverting to the download icon, and then vanishing when the batch finished
// and the album stopped being downloadable at all.
//
// Matched on AlbumTileKey (name + artist), which is what the builders group by
// - not on the Track references behind it, since a rescan replaces those
// wholesale.
public static class AlbumTileMerge
{
    // retired: the previous tiles that no longer appear, and are therefore the
    // caller's to Dispose - a reused tile must not be, since it is still bound
    // and may still own a running spinner subscription.
    public static List<AlbumTileViewModel> Apply(
        IReadOnlyList<AlbumTileViewModel>? previous,
        IReadOnlyList<AlbumTileViewModel> built,
        out List<AlbumTileViewModel> retired)
    {
        retired = new List<AlbumTileViewModel>();
        var result = new List<AlbumTileViewModel>(built.Count);

        Dictionary<AlbumTileKey, AlbumTileViewModel>? reusable = null;
        if (previous is { Count: > 0 })
        {
            reusable = new Dictionary<AlbumTileKey, AlbumTileViewModel>(previous.Count);
            foreach (var tile in previous)
            {
                // A duplicate key cannot come from either builder, both of
                // which group by exactly this. Retire the extra rather than
                // dropping it on the floor undisposed if one ever does.
                if (!reusable.TryAdd(tile.Key, tile))
                    retired.Add(tile);
            }
        }

        foreach (var tile in built)
        {
            if (reusable != null && reusable.Remove(tile.Key, out var reused))
            {
                reused.ApplyBuilt(tile);
                result.Add(reused);
            }
            else
            {
                result.Add(tile);
            }
        }

        if (reusable != null)
        {
            foreach (var tile in reusable.Values)
                retired.Add(tile);
        }

        return result;
    }
}
