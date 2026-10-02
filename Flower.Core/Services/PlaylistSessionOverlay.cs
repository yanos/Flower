using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// A playlist as a sync session found it when it began planning. Cheap to take:
// a playlist swaps in a new track list on every edit rather than changing the
// one it has (see Playlist.Tracks), so holding the reference holds the version.
public sealed record PlannedPlaylist(string Name, IReadOnlyList<Track> Tracks, DateTimeOffset UpdatedAt)
{
    public static PlannedPlaylist Of(Playlist playlist) => new(playlist.Name, playlist.Tracks, playlist.UpdatedAt);
}

// What a session installs: its merge laid over what the user did while it ran.
//
// HeldBack is the playlists edited here mid-session that the merge had
// replaced with the other side's newer copy, where the two edits could not be
// combined. The local edit is kept, but it is not the agreed version: it must
// not be pushed (the server would take it, being newer, and the other edit
// would be gone without anyone being asked), and the baseline must stay where
// it was, so the next session - which the edit itself scheduled - sees both
// sides changed and asks.
//
// Merged is those that could be combined: installed as a new version newer
// than both, for the push to carry to the other side.
public sealed record PlaylistSessionResult(
    List<Playlist> Installed,
    IReadOnlySet<Guid> HeldBack,
    IReadOnlySet<Guid> Merged);

public static class PlaylistSessionOverlay
{
    // merged is what the session decided; planned, what it decided it from;
    // current, the set as it is now. Pure, and run under Library's lock (see
    // Library.ReplacePlaylists), so nothing can change between the look and
    // the install.
    public static PlaylistSessionResult Apply(
        IReadOnlyList<Playlist> merged,
        IReadOnlyDictionary<Guid, PlannedPlaylist> planned,
        IReadOnlyList<Playlist> current)
    {
        var currentById = current.ToDictionary(p => p.Id);
        var installed = new List<Playlist>();
        var heldBack = new HashSet<Guid>();
        var combined = new HashSet<Guid>();

        foreach (var decided in merged)
        {
            if (!planned.TryGetValue(decided.Id, out var before))
            {
                installed.Add(decided);
                continue;
            }

            // Deleted here while the session ran.
            if (!currentById.TryGetValue(decided.Id, out var now))
                continue;

            // Edited here while the session ran, and the session had settled
            // on another copy of it - the other side's newer one. Kept as the
            // decided copy would have thrown the edit away.
            if (!ReferenceEquals(now, decided) && now.UpdatedAt != before.UpdatedAt)
            {
                if (PlaylistThreeWayMerge.Merge(before, now, decided) is { } both)
                {
                    installed.Add(both);
                    combined.Add(both.Id);
                }
                else
                {
                    installed.Add(now);
                    heldBack.Add(now.Id);
                }
                continue;
            }

            installed.Add(decided);
        }

        var inInstalled = installed.Select(p => p.Id).ToHashSet();
        foreach (var playlist in current)
        {
            if (inInstalled.Contains(playlist.Id))
                continue;

            // Created here while the session ran - it has never been agreed,
            // so it goes up as new, like any other.
            if (!planned.TryGetValue(playlist.Id, out var before))
                installed.Add(playlist);
            // Deleted on the other side, and edited here while the session
            // ran. An edit beats a delete everywhere else (see
            // PlaylistSyncService.ResolveConflictAsync), so it does here too:
            // kept, with no baseline, and the next session sends it back up.
            else if (playlist.UpdatedAt != before.UpdatedAt)
                installed.Add(playlist);
        }

        return new PlaylistSessionResult(installed, heldBack, combined);
    }
}

// Combines two edits of one playlist made from the same version of it - the
// one a session planned from, edited here while the session ran, and edited
// on the other side before it. Null when they cannot both be kept.
//
// Entries are told apart by track and by which occurrence of that track they
// are, so a song in a playlist twice is two entries, and removing one of them
// on one side is not mistaken for removing both.
//
// What combines: each side adding songs, each side removing songs, and one
// side reordering - the other side's additions and removals are applied to
// the reordered list, each added song landing at the end if that is where it
// was added, and otherwise after the song it followed on its own side. What
// does not: both sides reordering differently, and both
// renaming to different names. Smart playlists never combine - their songs
// are what their rules say, and the rules are one query, not a list.
public static class PlaylistThreeWayMerge
{
    public static Playlist? Merge(PlannedPlaylist before, Playlist ours, Playlist theirs)
    {
        if (ours.IsSmart || theirs.IsSmart)
            return null;

        var name = ours.Name == before.Name ? theirs.Name
            : theirs.Name == before.Name || theirs.Name == ours.Name ? ours.Name
            : null;
        if (name == null)
            return null;

        var tracks = MergeTracks(before.Tracks, ours.Tracks, theirs.Tracks);
        if (tracks == null)
            return null;

        var result = new Playlist(ours.Id, name, tracks, ours.UpdatedAt, ours.Comment, ours.IsPublic, ours.CreatedAt, listener: ours.Listener);
        // Newer than both, so the other side takes it from the push.
        result.MarkEditedAfter(ours.UpdatedAt > theirs.UpdatedAt ? ours.UpdatedAt : theirs.UpdatedAt);
        return result;
    }

    private readonly record struct Entry(Guid Track, int Occurrence);

    private static List<Entry> Entries(IReadOnlyList<Track> tracks)
    {
        var seen = new Dictionary<Guid, int>();
        var entries = new List<Entry>(tracks.Count);
        foreach (var track in tracks)
        {
            var n = seen.GetValueOrDefault(track.Id);
            seen[track.Id] = n + 1;
            entries.Add(new Entry(track.Id, n));
        }
        return entries;
    }

    private static List<Track>? MergeTracks(IReadOnlyList<Track> before, IReadOnlyList<Track> ours, IReadOnlyList<Track> theirs)
    {
        var baseEntries = Entries(before);
        var ourEntries = Entries(ours);
        var theirEntries = Entries(theirs);

        if (ourEntries.SequenceEqual(baseEntries) || ourEntries.SequenceEqual(theirEntries))
            return theirs.ToList();
        if (theirEntries.SequenceEqual(baseEntries))
            return ours.ToList();

        // The songs both sides kept, in each side's order: a side whose order
        // differs from the original's reordered them.
        var baseSet = baseEntries.ToHashSet();
        var ourSet = ourEntries.ToHashSet();
        var theirSet = theirEntries.ToHashSet();
        var kept = baseEntries.Where(e => ourSet.Contains(e) && theirSet.Contains(e)).ToHashSet();
        var baseOrder = baseEntries.Where(kept.Contains).ToList();
        var ourOrder = ourEntries.Where(kept.Contains).ToList();
        var theirOrder = theirEntries.Where(kept.Contains).ToList();
        var weReordered = !ourOrder.SequenceEqual(baseOrder);
        var theyReordered = !theirOrder.SequenceEqual(baseOrder);
        if (weReordered && theyReordered && !ourOrder.SequenceEqual(theirOrder))
            return null;

        // Start from the side that reordered (theirs, when neither did), and
        // bring the other side's removals and additions over to it.
        var (skeleton, other, otherSet) = weReordered
            ? (ourEntries, theirEntries, theirSet)
            : (theirEntries, ourEntries, ourSet);

        var result = skeleton.Where(e => !baseSet.Contains(e) || otherSet.Contains(e)).ToList();
        var inResult = result.ToHashSet();
        for (var i = 0; i < other.Count; i++)
        {
            var entry = other[i];
            if (baseSet.Contains(entry) || inResult.Contains(entry))
                continue;

            // Added at the end of its own side - the way a song is almost
            // always added - stays at the end. Otherwise after the nearest
            // entry before it on its own side that the result has; at the top
            // when there is none.
            if (other.Skip(i + 1).All(e => !baseSet.Contains(e)))
            {
                result.Add(entry);
                inResult.Add(entry);
                continue;
            }

            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var anchor = result.IndexOf(other[j]);
                if (anchor >= 0)
                {
                    at = anchor + 1;
                    break;
                }
            }
            result.Insert(at, entry);
            inResult.Add(entry);
        }

        var byId = new Dictionary<Guid, Track>();
        foreach (var track in before.Concat(ours).Concat(theirs))
            byId.TryAdd(track.Id, track);
        return result.Select(e => byId[e.Track]).ToList();
    }
}
