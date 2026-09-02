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
            var baseline = baselineFor(id);

            // A missing side is only a deletion to propagate if the two devices
            // previously agreed the playlist existed (a baseline) AND the side
            // that still has it hasn't changed it since. Without that second
            // check this was an unconditional Delete, so a playlist edited here
            // while offline lost those edits outright the moment the peer
            // happened to delete it - a real delete-vs-edit conflict silently
            // resolved in favour of the deletion, and the only merge case in
            // this method that didn't ask the user. The edit-vs-edit branch
            // below has always made exactly this comparison.
            if (r == null)
            {
                var localOnlyKind =
                    baseline == null                ? PlaylistSyncDecisionKind.KeepLocal
                    : l != null && l.UpdatedAt > baseline ? PlaylistSyncDecisionKind.Conflict
                    : PlaylistSyncDecisionKind.Delete;
                decisions.Add(new PlaylistSyncDecision(id, localOnlyKind, l, null));
                continue;
            }
            if (l == null)
            {
                var remoteOnlyKind =
                    baseline == null              ? PlaylistSyncDecisionKind.AdoptRemote
                    : r.UpdatedAt > baseline      ? PlaylistSyncDecisionKind.Conflict
                    : PlaylistSyncDecisionKind.Delete;
                decisions.Add(new PlaylistSyncDecision(id, remoteOnlyKind, null, r));
                continue;
            }

            // A smart playlist is decided on its query, never on its contents.
            // Two devices holding different music legitimately materialize the
            // same rules into different track lists, so ContentEquals below
            // would report a difference on every sync, and - since
            // materialization deliberately does not bump UpdatedAt - neither
            // side would look changed against the baseline, landing every smart
            // playlist in Conflict forever.
            //
            // So: same query, same name, nothing to do. Otherwise someone
            // really did edit the query (the only thing about a smart playlist
            // that moves UpdatedAt) and the newer edit wins outright. No
            // conflict window, because there is nothing of the user's to lose -
            // a query replaced by a newer query, not a hand-built track list.
            //
            // The same branch covers smart on one side and manual on the other,
            // for the same reason and with the same answer: newest wins,
            // including the change of kind, and a manual playlist that wins
            // brings its track list with it.
            //
            // A tie in UpdatedAt goes to whichever side actually has rules,
            // rather than to local. Losing rules is never something a user did:
            // the only way a playlist stops being smart is an edit, and an edit
            // moves UpdatedAt. So equal timestamps with rules on one side only
            // means the other side is a lossy copy of this same playlist - made
            // by a peer that predates rules travelling at all, or that dropped
            // them in transit. Handing the tie to local would let that copy win
            // wherever it happens to be the local one, and then be pushed back
            // over the good one at the end of the session: the query dies on
            // every device rather than healing on the next sync.
            if (l.IsSmart || r.Rules != null)
            {
                var smartKind =
                    l.Name == r.Name && SmartPlaylistRules.Equivalent(l.Rules, r.Rules)
                        ? PlaylistSyncDecisionKind.NoChange
                    : l.UpdatedAt != r.UpdatedAt
                        ? l.UpdatedAt > r.UpdatedAt
                            ? PlaylistSyncDecisionKind.KeepLocal
                            : PlaylistSyncDecisionKind.AdoptRemote
                    : l.IsSmart
                        ? PlaylistSyncDecisionKind.KeepLocal
                        : PlaylistSyncDecisionKind.AdoptRemote;
                decisions.Add(new PlaylistSyncDecision(id, smartKind, l, r));
                continue;
            }

            if (ContentEquals(l, r))
            {
                decisions.Add(new PlaylistSyncDecision(id, PlaylistSyncDecisionKind.NoChange, l, r));
                continue;
            }

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
