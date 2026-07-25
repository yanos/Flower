using System.Collections.Generic;

using Flower.Models;
using Flower.ViewModels;

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

    public static void Apply(IEnumerable<TrackRowViewModel> rows, string? pairedServerFingerprint, bool pairedServerReachable)
    {
        foreach (var row in rows)
            row.IsAvailable = IsAvailable(row.Track, pairedServerFingerprint, pairedServerReachable);
    }
}
