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

    // A playlist the two devices previously agreed existed (see Plan's
    // baselineFor check) was deleted on one side: gone from this device, or on
    // the server's record of deletions. It is removed from the other side too
    // rather than being treated as "adopt/keep the one side that still has it".
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
    //
    // index is this device's library, for reading the remote entries as the
    // tracks they would become here. Without one, content is compared by
    // SyncKey alone and a copy that lost entries is never recognised as such.
    //
    // deletedRemotely is the server's record of what was deleted there
    // (IPlaylistDeletionLog).
    public static IReadOnlyList<PlaylistSyncDecision> Plan(
        IReadOnlyList<Playlist> local,
        IReadOnlyList<PlaylistSyncPlaylistDto> remote,
        Func<Guid, DateTimeOffset?> baselineFor,
        PlaylistTrackIndex? index = null,
        IReadOnlyCollection<Guid>? deletedRemotely = null)
    {
        var deletedThere = deletedRemotely?.ToHashSet() ?? [];
        var localById  = local.ToDictionary(p => p.Id);
        var remoteById = remote.ToDictionary(p => p.Id);
        var allIds     = localById.Keys.Union(remoteById.Keys);

        var decisions = new List<PlaylistSyncDecision>();
        foreach (var id in allIds)
        {
            localById.TryGetValue(id, out var l);
            remoteById.TryGetValue(id, out var r);
            var baseline = baselineFor(id);

            // Missing from the server and not on its record of deletions: the
            // server lost it rather than anyone deleting it - it was restored
            // from a backup older than the playlist. Kept, and the push puts it
            // back. Read as a deletion, this removed every playlist made since
            // the backup from every device.
            //
            // Otherwise a missing side is only a deletion to propagate if the two devices
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
                    baseline == null || !deletedThere.Contains(id) ? PlaylistSyncDecisionKind.KeepLocal
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
            // So: same query, same name, same folder, nothing to do. Otherwise someone
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
            // A Vibe Playlist is decided on its sentence the same way, and
            // for a stronger version of the same reason: its songs are the
            // server's ranking, which no device can reproduce. So the same
            // sentence is no change at all, whatever the songs - and the
            // session then takes the server's songs as they stand
            // (PlaylistSyncService), the way a smart playlist re-evaluates its
            // own. A newer sentence on either side wins outright.
            if (l.IsVibe || r.VibePrompt != null)
            {
                var vibeKind =
                    l.Name == r.Name && l.ParentId == r.ParentId && l.SortKey == r.SortKey
                    && l.VibePrompt == r.VibePrompt && l.VibeInstrumental == r.VibeInstrumental
                        ? PlaylistSyncDecisionKind.NoChange
                    : l.UpdatedAt >= r.UpdatedAt
                        ? PlaylistSyncDecisionKind.KeepLocal
                        : PlaylistSyncDecisionKind.AdoptRemote;
                decisions.Add(new PlaylistSyncDecision(id, vibeKind, l, r));
                continue;
            }

            if (l.IsSmart || r.Rules != null)
            {
                var smartKind =
                    l.Name == r.Name && l.ParentId == r.ParentId && l.SortKey == r.SortKey && SmartPlaylistRules.Equivalent(l.Rules, r.Rules)
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

            if (ContentEquals(l, r, index))
            {
                decisions.Add(new PlaylistSyncDecision(id, PlaylistSyncDecisionKind.NoChange, l, r));
                continue;
            }

            var localChanged  = baseline == null || l.UpdatedAt > baseline;
            var remoteChanged = baseline == null || r.UpdatedAt > baseline;

            // Different content with neither side moved past the baseline is
            // not two edits: nobody edited anything. It is a copy from before
            // rows were kept without their song, which is shorter at the same
            // timestamp. Reporting that as a conflict asked the user to pick
            // between versions they never made. Local wins, and the server
            // keeps its own copy of an unedited playlist whatever is pushed
            // (PlaylistSyncMapper.ApplyPushedManifest).
            //
            // Unless the remote copy holds every row local has and more, when
            // this is the shorter one, and nothing would ever have moved either
            // timestamp to make it look again.
            var kind = (localChanged, remoteChanged) switch
            {
                (true, false)  => PlaylistSyncDecisionKind.KeepLocal,
                (false, true)  => PlaylistSyncDecisionKind.AdoptRemote,
                (false, false) => index != null && HoldsAllAndMore(r.Tracks, l.Entries, index)
                    ? PlaylistSyncDecisionKind.AdoptRemote
                    : PlaylistSyncDecisionKind.KeepLocal,
                _              => PlaylistSyncDecisionKind.Conflict,
            };
            decisions.Add(new PlaylistSyncDecision(id, kind, l, r));
        }

        return decisions;
    }

    // Row by row, the ones this device cannot play included: a row it lacks is
    // a difference now, since it can hold one without having the song.
    private static bool ContentEquals(Playlist local, PlaylistSyncPlaylistDto remote, PlaylistTrackIndex? index)
    {
        if (local.Name != remote.Name || local.ParentId != remote.ParentId || local.SortKey != remote.SortKey)
            return false;

        if (index != null)
            return SameRows(local.Entries, remote.Tracks, index);
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

    private static bool SameRows(
        IReadOnlyList<PlaylistEntry> entries, IReadOnlyList<PlaylistSyncTrackDto> rows, PlaylistTrackIndex index)
    {
        if (entries.Count != rows.Count)
            return false;

        for (var i = 0; i < rows.Count; i++)
        {
            if (entries[i].EntryId != rows[i].EntryId && SongOf(entries[i]) != SongOf(rows[i], index))
                return false;
        }
        return true;
    }

    // Every local row (counting repeats) is among the remote ones, and there
    // are more of those.
    private static bool HoldsAllAndMore(
        IReadOnlyList<PlaylistSyncTrackDto> rows, IReadOnlyList<PlaylistEntry> entries, PlaylistTrackIndex index)
    {
        if (rows.Count <= entries.Count)
            return false;

        var remaining = rows.GroupBy(row => SongOf(row, index)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var entry in entries)
        {
            var song = SongOf(entry);
            if (remaining.GetValueOrDefault(song) == 0)
                return false;
            remaining[song]--;
        }
        return true;
    }

    private static string SongOf(PlaylistEntry entry) =>
        entry.Track?.Id.ToKey() ?? SongOf(entry.ToWire());

    private static string SongOf(PlaylistSyncTrackDto row, PlaylistTrackIndex index) =>
        index.Find(row)?.Id.ToKey() ?? SongOf(row);

    private static string SongOf(PlaylistSyncTrackDto row) =>
        row.Id is { Length: > 0 } id ? id : Track.BuildSyncKey(row.Title, row.Artists, row.Album, row.DurationSeconds);
}
