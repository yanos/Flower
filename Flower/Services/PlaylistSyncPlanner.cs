using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Persistence.Sql;

namespace Flower.Services;

public enum PlaylistSyncDecisionKind
{
    // Both sides hold the same version. The server's is recorded as agreed.
    NoChange,

    // This device takes the server's copy.
    AdoptRemote,

    // This device's copy goes to the server, over BaseRevision.
    Push,

    // This device's deletion goes to the server, over BaseRevision.
    PushDelete,

    // The server's deletion reaches this device.
    DeleteHere,

    // Gone on both sides: only the agreement is left to drop.
    Forget,

    // Both sides changed it in ways that combine (PlaylistThreeWayMerge):
    // Merged is installed here and goes to the server over BaseRevision.
    Merge,

    // Both sides changed it in ways that do not combine - only a person can
    // say which to keep.
    Conflict,

    // The server records it deleted, and this device cannot tell whether its
    // copy holds an edit: asked once, "Deleted on your server. Keep it?"
    ConfirmDeletion,
}

public sealed record PlaylistSyncDecision(
    Guid PlaylistId,
    PlaylistSyncDecisionKind Kind,
    Playlist? Local,
    PlaylistSyncPlaylistDto? Remote,
    PlaylistDeletionDto? Deletion = null,
    long BaseRevision = 0,
    Playlist? Merged = null);

// Pure merge logic for playlist sync, kept free of I/O (HTTP, disk) so it's unit
// testable on its own. PlaylistSyncService is the shell that feeds it and
// carries out what it decides.
//
// Each playlist is read against the copy this device last agreed with the
// server on (PlaylistAgreement) by the table in docs/PLAYLIST-SYNC-PLAN.md,
// "The protocol": where the server's version stands against the agreed
// revision says who changed it, and "did this device change it" is a
// comparison of content with the agreed copy (PlaylistVersion), never of
// clocks.
public static class PlaylistSyncPlanner
{
    // only limits the plan to these playlists - a session planning again with
    // what a Stale answer said the server holds now.
    public static IReadOnlyList<PlaylistSyncDecision> Plan(
        IReadOnlyList<Playlist> local,
        IReadOnlyList<PlaylistSyncPlaylistDto> remote,
        IReadOnlyList<PlaylistDeletionDto> deletedRemotely,
        IReadOnlyDictionary<Guid, PlaylistAgreement> agreements,
        PlaylistTrackIndex index,
        IReadOnlySet<Guid>? only = null)
    {
        var localById = local.ToDictionary(p => p.Id);
        var remoteById = remote.ToDictionary(p => p.Id);
        var deletedById = deletedRemotely.ToDictionary(d => d.Id);
        var ids = localById.Keys.Union(remoteById.Keys).Union(deletedById.Keys).Union(agreements.Keys)
            .Where(id => only == null || only.Contains(id));

        var decisions = new List<PlaylistSyncDecision>();
        foreach (var id in ids)
        {
            localById.TryGetValue(id, out var l);
            agreements.TryGetValue(id, out var agreed);
            var decision =
                remoteById.TryGetValue(id, out var r) ? AgainstPlaylist(id, l, r, agreed, index)
                : deletedById.TryGetValue(id, out var d) ? AgainstDeletion(id, l, d, agreed)
                : AgainstNothing(id, l, agreed);
            if (decision != null)
                decisions.Add(decision);
        }

        return decisions;
    }

    // The server holds the playlist.
    private static PlaylistSyncDecision AgainstPlaylist(
        Guid id, Playlist? l, PlaylistSyncPlaylistDto r, PlaylistAgreement? agreed, PlaylistTrackIndex index)
    {
        if (agreed == null)
            return NoHistoryInCommon(id, l, r, index);

        // Unchanged on the server since this device agreed.
        if (agreed.Revision == r.Revision)
        {
            if (l == null)
                return new(id, PlaylistSyncDecisionKind.PushDelete, null, r, BaseRevision: r.Revision);
            return Changed(l, agreed)
                ? new(id, PlaylistSyncDecisionKind.Push, l, r, BaseRevision: r.Revision)
                : new(id, PlaylistSyncDecisionKind.NoChange, l, r);
        }

        // Newer than the server's: it was restored from a backup older than
        // what this device agreed, and this device's copy goes back up.
        if (agreed.Revision > r.Revision)
        {
            return l == null
                ? new(id, PlaylistSyncDecisionKind.PushDelete, null, r, BaseRevision: r.Revision)
                : new(id, PlaylistSyncDecisionKind.Push, l, r, BaseRevision: r.Revision);
        }

        // Older than every version the server remembers.
        if (!Contains(r.History, agreed.Revision))
            return NoHistoryInCommon(id, l, r, index);

        // The server moved on. An edit there beats a deletion here.
        if (l == null || !Changed(l, agreed))
            return new(id, PlaylistSyncDecisionKind.AdoptRemote, l, r);
        if (SameContent(l, r, index))
            return new(id, PlaylistSyncDecisionKind.NoChange, l, r);
        return Combined(id, l, r, agreed, index) ?? BothChanged(id, l, r);
    }

    // Both sides changed it since the copy they agreed on: the two edits
    // merged from that copy, when they combine. A merge that comes out as the
    // server's copy - this device's edit was already part of it - is that
    // copy, with nothing to send.
    private static PlaylistSyncDecision? Combined(
        Guid id, Playlist l, PlaylistSyncPlaylistDto r, PlaylistAgreement agreed, PlaylistTrackIndex index)
    {
        if (agreed.Copy == null)
            return null;

        var before = PlaylistSyncMapper.ToPlaylist(agreed.Copy, index);
        if (PlaylistThreeWayMerge.Merge(before, l, PlaylistSyncMapper.ToPlaylist(r, index)) is not { } merged)
            return null;

        return SameContent(merged, r, index)
            ? new(id, PlaylistSyncDecisionKind.AdoptRemote, l, r)
            : new(id, PlaylistSyncDecisionKind.Merge, l, r, BaseRevision: r.Revision, Merged: merged);
    }

    // No agreed copy, or one older than every version the server remembers:
    // nothing says which side changed what.
    private static PlaylistSyncDecision NoHistoryInCommon(Guid id, Playlist? l, PlaylistSyncPlaylistDto r, PlaylistTrackIndex index)
    {
        if (l == null)
            return new(id, PlaylistSyncDecisionKind.AdoptRemote, null, r);
        if (SameContent(l, r, index))
            return new(id, PlaylistSyncDecisionKind.NoChange, l, r);
        return BothChanged(id, l, r);
    }

    // A smart playlist's rules or a Vibe Playlist's sentence changed on both
    // sides: the newer edit wins with no prompt, since no hand-built list is
    // at stake. A tie goes to whichever side has rules - losing them is never
    // an edit, since an edit moves UpdatedAt. Anything else is a person's to
    // decide.
    private static PlaylistSyncDecision BothChanged(Guid id, Playlist l, PlaylistSyncPlaylistDto r)
    {
        if (!l.IsSmart && r.Rules == null && !l.IsVibe && r.VibePrompt == null)
            return new(id, PlaylistSyncDecisionKind.Conflict, l, r);

        var localWins = l.UpdatedAt != r.UpdatedAt ? l.UpdatedAt > r.UpdatedAt : l.IsSmart || l.IsVibe;
        return localWins
            ? new(id, PlaylistSyncDecisionKind.Push, l, r, BaseRevision: r.Revision)
            : new(id, PlaylistSyncDecisionKind.AdoptRemote, l, r);
    }

    // The server records the playlist as deleted.
    private static PlaylistSyncDecision? AgainstDeletion(Guid id, Playlist? l, PlaylistDeletionDto d, PlaylistAgreement? agreed)
    {
        if (l == null)
            return agreed != null ? new(id, PlaylistSyncDecisionKind.Forget, null, null, d) : null;
        if (agreed == null)
            return new(id, PlaylistSyncDecisionKind.ConfirmDeletion, l, null, d);

        // Deleted over a version this device agreed: unchanged here, the
        // deletion stands; changed here, the edit beats it.
        if (agreed.Revision == d.Revision || Contains(d.History, agreed.Revision))
        {
            return Changed(l, agreed)
                ? new(id, PlaylistSyncDecisionKind.Push, l, null, d, d.Revision)
                : new(id, PlaylistSyncDecisionKind.DeleteHere, l, null, d);
        }

        // Restored from a backup older than what this device agreed.
        if (agreed.Revision > d.Revision)
            return new(id, PlaylistSyncDecisionKind.Push, l, null, d, d.Revision);

        return new(id, PlaylistSyncDecisionKind.ConfirmDeletion, l, null, d);
    }

    // The server holds nothing and records no deletion: it lost the playlist
    // (a restored backup older than it), and this device's copy goes back up.
    private static PlaylistSyncDecision? AgainstNothing(Guid id, Playlist? l, PlaylistAgreement? agreed)
    {
        if (l == null)
            return agreed != null ? new(id, PlaylistSyncDecisionKind.Forget, null, null) : null;
        return new(id, PlaylistSyncDecisionKind.Push, l, null, BaseRevision: 0);
    }

    private static bool Contains(List<long>? history, long revision) => history?.Contains(revision) == true;

    // This device's copy is not the one it agreed on - or exists where it
    // agreed to a deletion.
    private static bool Changed(Playlist local, PlaylistAgreement agreed) =>
        agreed.Copy == null || PlaylistVersion.Of(local) != PlaylistVersion.Of(agreed.Copy);

    // The same playlist, read the way each kind is decided: a smart playlist
    // on its rules and a Vibe Playlist on its sentence, never on their songs,
    // which each device works out (or takes from the server) for itself; an
    // ordinary one row by row, a row matching by its id or, for copies made
    // before rows had ids, by its song.
    private static bool SameContent(Playlist local, PlaylistSyncPlaylistDto remote, PlaylistTrackIndex index)
    {
        if (local.Name != remote.Name || local.ParentId != remote.ParentId || local.SortKey != remote.SortKey
            || local.IsFolder != remote.IsFolder)
        {
            return false;
        }

        if (local.IsVibe || remote.VibePrompt != null)
            return local.VibePrompt == remote.VibePrompt && local.VibeInstrumental == remote.VibeInstrumental;
        if (local.IsSmart || remote.Rules != null)
            return SmartPlaylistRules.Equivalent(local.Rules, remote.Rules);
        if (local.IsFolder)
            return true;

        return SameRows(local.Entries, remote.Tracks, index);
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

    private static string SongOf(PlaylistEntry entry) =>
        entry.Track?.Id.ToKey() ?? entry.ToWire().SongKey;

    private static string SongOf(PlaylistSyncTrackDto row, PlaylistTrackIndex index) =>
        index.Find(row)?.Id.ToKey() ?? row.SongKey;
}
