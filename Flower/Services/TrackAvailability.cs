using System;
using System.Collections.Generic;

using Flower.Models;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Services;

// Pure decision logic for whether a placeholder Track can be streamed/
// downloaded right now - mirrors SyncRolePolicy's own separation of pure
// logic from the services that trigger/own it, so this is unit-testable
// without a NetworkDiscoveryService/MainViewModel in play.
//
// Deliberately a single flag rather than the old IsPeerReachable +
// IsFromPairedServer split: every real operation on a placeholder (stream,
// download, art) is already gated to the paired Server only, via
// PeerTrackResolver/SyncRolePolicy.MayRequestFrom - a placeholder from some
// other reachable-but-not-paired peer was never actually actionable, so
// that split was itself a latent correctness gap, not just duplicated code.
public static class TrackAvailability
{
    public static bool IsAvailable(Track track, string? pairedServerFingerprint, bool pairedServerReachable) =>
        track.Path == null &&
        !string.IsNullOrEmpty(pairedServerFingerprint) &&
        track.OriginDeviceFingerprint == pairedServerFingerprint &&
        pairedServerReachable;

    // "Can this track be listened to at all right now" - the union of the two
    // ways that can be true: the file is already on this device, or it is a
    // placeholder the paired server can currently serve. IsAvailable above is
    // deliberately about placeholders *only* (it is what gates the download
    // button), so it is the wrong question to ask of a mixed album, where a
    // single downloaded track is enough to keep the album playable.
    public static bool IsPlayable(Track track, string? pairedServerFingerprint, bool pairedServerReachable) =>
        track.Path != null || IsAvailable(track, pairedServerFingerprint, pairedServerReachable);

    // Whether a whole album should read as unavailable. Only when *nothing* in
    // it can be played: one downloaded track (or, while the server is up, one
    // streamable placeholder) is enough to keep the album's tile at full
    // strength, since tapping it still lands on something that plays. An album
    // with no tracks at all is not "unavailable" - there is nothing there to
    // be greyed out.
    public static bool IsAlbumUnavailable(IEnumerable<Track> tracks, string? pairedServerFingerprint, bool pairedServerReachable)
    {
        var any = false;
        foreach (var track in tracks)
        {
            if (IsPlayable(track, pairedServerFingerprint, pairedServerReachable))
                return false;
            any = true;
        }

        return any;
    }

    // Whether anything in this album is still worth fetching - drives the
    // album-level download icons (a grid tile, an expanded album's header).
    public static bool AnyDownloadable(IEnumerable<Track> tracks, string? pairedServerFingerprint, bool pairedServerReachable)
    {
        foreach (var track in tracks)
        {
            if (IsAvailable(track, pairedServerFingerprint, pairedServerReachable))
                return true;
        }

        return false;
    }

    // Re-marks an existing row list in place when the paired server comes or
    // goes. Takes a list rather than a sequence because the album-art flag is a
    // property of a *run* of adjacent rows (see TrackListBuilder.PlanRows),
    // which needs the rows either side of each one to answer - the runs are
    // walked here off IsFirstInAlbumGroup/AlbumGroupSize rather than recomputed
    // from the tracks, so this stays consistent with whatever grouping the last
    // rebuild actually produced.
    public static void Apply(IReadOnlyList<TrackRowViewModel> rows, string? pairedServerFingerprint, bool pairedServerReachable)
    {
        foreach (var row in rows)
            row.IsAvailable = IsAvailable(row.Track, pairedServerFingerprint, pairedServerReachable);

        var i = 0;
        while (i < rows.Count)
        {
            // Clamped against rows.Count: AlbumGroupSize is only trustworthy
            // as far as the list actually goes (a search-result list can be
            // truncated mid-run - see MobileMainViewModel's search caps).
            var groupSize = Math.Max(1, Math.Min(rows[i].AlbumGroupSize, rows.Count - i));
            var unavailable = true;
            for (var k = i; k < i + groupSize && unavailable; k++)
                unavailable = !IsPlayable(rows[k].Track, pairedServerFingerprint, pairedServerReachable);

            for (var k = i; k < i + groupSize; k++)
                rows[k].IsAlbumGroupUnavailable = unavailable;

            i += groupSize;
        }
    }

    // The album-grid counterpart of Apply above - the tiles carry their own
    // album's tracks (AlbumTileViewModel.Tracks) precisely so this can be
    // recomputed in place when the server comes and goes, without rebuilding
    // (and re-loading the art of) every tile in the grid.
    public static void Apply(IEnumerable<AlbumTileViewModel> tiles, string? pairedServerFingerprint, bool pairedServerReachable)
    {
        foreach (var tile in tiles)
        {
            tile.IsUnavailable = IsAlbumUnavailable(tile.Tracks, pairedServerFingerprint, pairedServerReachable);
            // The tile's own download icon: shown as soon as *any* of the
            // album's tracks can be fetched, since that is exactly what
            // clicking it would then do (see AlbumTileControl / MainView's
            // "Download Album" menu item, which uses the same rule). Not the
            // negation of IsUnavailable above - a fully downloaded album is
            // perfectly available and has nothing left to fetch.
            tile.IsDownloadable = AnyDownloadable(tile.Tracks, pairedServerFingerprint, pairedServerReachable);
        }
    }
}

// The two values every question above is asked against, bundled so a view can
// be handed "what counts as available right now" as one thing rather than two
// parallel properties it has to keep in step. See AlbumGridView.Availability,
// which pushes this down into an expanded album's own track rows.
public readonly record struct TrackAvailabilityContext(string? PairedServerFingerprint, bool PairedServerReachable)
{
    public bool IsPlayable(Track track) => TrackAvailability.IsPlayable(track, PairedServerFingerprint, PairedServerReachable);

    // "Is there a copy of this to fetch right now" - the same question
    // TrackRowViewModel.IsDownloadable answers for a row, asked of a bare
    // Track by the desktop right-click menus, which act on tracks (an album
    // tile's, a selection's) rather than on rows.
    public bool IsDownloadable(Track track) => TrackAvailability.IsAvailable(track, PairedServerFingerprint, PairedServerReachable);
}
