using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

public enum PlaylistSyncDecisionKind
{
    // Both sides already agree (or one side simply doesn't have the playlist yet
    // and isn't the source of truth for it) - nothing to ask the user about.
    NoChange,
    AdoptRemote,
    KeepLocal,

    // Both sides changed the same playlist since the last time these two devices
    // agreed on its state (or there's no record of ever agreeing and the content
    // differs) - can't pick a winner automatically, see PlaylistSyncService.
    Conflict,

    // One side no longer has a playlist the two devices previously agreed
    // existed (see Plan's baselineFor check) - it was deleted there, not just
    // never created on this side, so it should be removed from the other side
    // too rather than being treated as "adopt/keep the one side that still has it".
    Delete
}

public sealed record PlaylistSyncDecision(
    Guid PlaylistId,
    PlaylistSyncDecisionKind Kind,
    Playlist? Local,
    PlaylistSyncPlaylistDto? Remote);

// Pure merge logic for playlist sync, kept free of I/O (HTTP, disk) so it's unit
// testable on its own - see Flower.Tests. PlaylistSyncService is the thin shell that
// feeds this real data and carries out its decisions.
public static class PlaylistSyncPlanner
{
    // baselineFor returns the UpdatedAt both sides agreed on the last time this pair
    // of devices synced this playlist, or null if they never have (fresh pairing, or
    // a playlist created since). See PlaylistSyncStateStore.
    public static IReadOnlyList<PlaylistSyncDecision> Plan(
        IReadOnlyList<Playlist> local,
        IReadOnlyList<PlaylistSyncPlaylistDto> remote,
        Func<Guid, DateTimeOffset?> baselineFor)
    {
        var localById  = local.ToDictionary(p => p.Id);
        var remoteById = remote.ToDictionary(p => p.Id);
        var allIds     = localById.Keys.Union(remoteById.Keys);

        var decisions = new List<PlaylistSyncDecision>();
        foreach (var id in allIds)
        {
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);

            if (r == null)
            {
                // A baseline for this id means the two devices previously agreed
                // this playlist existed - if the remote no longer has it, that is
                // a deletion to propagate, not "local is the only side that has
                // ever known about this one".
                var localOnlyKind = baselineFor(id) != null ? PlaylistSyncDecisionKind.Delete : PlaylistSyncDecisionKind.KeepLocal;
                decisions.Add(new PlaylistSyncDecision(id, localOnlyKind, l, null));
                continue;
            }
            if (l == null)
            {
                var remoteOnlyKind = baselineFor(id) != null ? PlaylistSyncDecisionKind.Delete : PlaylistSyncDecisionKind.AdoptRemote;
                decisions.Add(new PlaylistSyncDecision(id, remoteOnlyKind, null, r));
                continue;
            }

            if (ContentEquals(l, r))
            {
                decisions.Add(new PlaylistSyncDecision(id, PlaylistSyncDecisionKind.NoChange, l, r));
                continue;
            }

            var baseline      = baselineFor(id);
            var localChanged  = baseline == null || l.UpdatedAt > baseline;
            var remoteChanged = baseline == null || r.UpdatedAt > baseline;

            var kind = (localChanged, remoteChanged) switch
            {
                (true, false) => PlaylistSyncDecisionKind.KeepLocal,
                (false, true) => PlaylistSyncDecisionKind.AdoptRemote,
                _             => PlaylistSyncDecisionKind.Conflict,
            };
            decisions.Add(new PlaylistSyncDecision(id, kind, l, r));
        }

        return decisions;
    }

    private static bool ContentEquals(Playlist local, PlaylistSyncPlaylistDto remote)
    {
        if (local.Name != remote.Name)
            return false;
        if (local.Tracks.Count != remote.Tracks.Count)
            return false;

        for (var i = 0; i < local.Tracks.Count; i++)
        {
            var remoteKey = Track.BuildSyncKey(remote.Tracks[i].Title, remote.Tracks[i].Artists, remote.Tracks[i].Album, remote.Tracks[i].DurationSeconds);
            if (local.Tracks[i].SyncKey != remoteKey)
                return false;
        }

        return true;
    }
}
