using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Models
{
    // Decides, for each track in a pulled catalog, which track this library
    // already has for it - the "is this the same song" half of
    // Library.MergeSyncedTracks, split out because it grew three ways of
    // saying yes and an order they have to be tried in.
    //
    // 1. The source's own id for the song (Track.OriginTrackId, from an earlier
    //    pull). The server keeps a file's id across a retag - its rescan
    //    matches by path - so this is what recognises "Teh Song" corrected to
    //    "The Song" as the same song. Matching by SyncKey alone read a retag as
    //    a delete plus an add: a new placeholder with a new Id, the old one
    //    pruned, and everything hung off it - playlist entries, plays not yet
    //    reported, a star - gone with it. On a track this device had
    //    downloaded, the file with the old tags stayed and a second row
    //    appeared beside it.
    // 2. SyncKey, for a song this device has never been told the id of: its
    //    own import of something the server also has, or the first pull
    //    from a server that was reinstalled under a new identity.
    // 3. The same tags with a duration a second or two apart - two encodes of
    //    one song, or two decoders' reading of one file, disagree by that much
    //    and SyncKey's whole-second rounding cannot absorb it. Only when
    //    exactly one track qualifies, and never for an untitled one, whose
    //    tags identify nothing.
    //
    // Each pass runs over the whole catalog before the next starts, and each
    // local track can be claimed once. Both matter for a server with two files
    // that tag identically: tried per catalog entry, a newcomer matched by key
    // could take a placeholder the next entry was about to claim by id, and
    // the ids would shuffle on every pull; claimed twice, the two server songs
    // collapsed onto one placeholder whose id flipped between them.
    public static class SyncedTrackMatcher
    {
        // How far apart two durations can be and still be one song, for the
        // last pass. Whole seconds, on the rounded values SyncKey uses.
        public const int DurationToleranceSeconds = 2;

        public static Track?[] Match(IReadOnlyList<Track> local, string sourceFingerprint, IReadOnlyList<Track> incoming)
        {
            var matches = new Track?[incoming.Count];
            var claimed = new HashSet<Track>(ReferenceEqualityComparer.Instance);

            var byOriginId = new Dictionary<string, Track>();
            foreach (var track in local)
            {
                if (track.OriginDeviceFingerprint == sourceFingerprint && track.OriginTrackId is { Length: > 0 } id)
                    byOriginId.TryAdd(id, track);
            }

            for (var i = 0; i < incoming.Count; i++)
            {
                if (incoming[i].OriginTrackId is { Length: > 0 } id
                    && byOriginId.TryGetValue(id, out var existing)
                    && claimed.Add(existing))
                {
                    matches[i] = existing;
                }
            }

            var byKey = local.ToLookup(t => t.SyncKey);
            for (var i = 0; i < incoming.Count; i++)
            {
                if (matches[i] != null)
                    continue;

                var candidate = byKey[incoming[i].SyncKey].FirstOrDefault(t => !claimed.Contains(t));
                if (candidate != null && claimed.Add(candidate))
                    matches[i] = candidate;
            }

            var byLooseKey = local
                .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                .ToLookup(t => Track.BuildLooseKey(t.Title, t.Artists, t.Album));
            for (var i = 0; i < incoming.Count; i++)
            {
                var remote = incoming[i];
                if (matches[i] != null || string.IsNullOrWhiteSpace(remote.Title))
                    continue;

                var seconds = Track.RoundedSeconds(remote.Duration);
                var candidates = byLooseKey[Track.BuildLooseKey(remote.Title, remote.Artists, remote.Album)]
                    .Where(t => !claimed.Contains(t)
                                && Math.Abs(Track.RoundedSeconds(t.Duration) - seconds) <= DurationToleranceSeconds)
                    .Take(2)
                    .ToList();
                if (candidates.Count == 1 && claimed.Add(candidates[0]))
                    matches[i] = candidates[0];
            }

            return matches;
        }
    }
}
