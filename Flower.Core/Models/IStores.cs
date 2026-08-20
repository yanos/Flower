using System;
using System.Collections.Generic;

namespace Flower.Models
{
    // The durability half of a library mutation, as Library sees it.
    //
    // Library owns what a change *means* - which tracks an album id stars,
    // that a scrobble is a count bump plus a played-at stamp - and this is the
    // one thing it cannot decide for itself: whether that change is written on
    // the spot. Flower.Server hands one in (TrackRepository implements it
    // directly), so a star or a scrobble is durable by the time the request is
    // answered. The desktop client passes none and drives its writes from
    // LibraryStore instead, which wraps the same repository in a write lock and
    // swallows a missing data directory rather than failing a UI action.
    //
    // Every write a mutation on Library can produce, and no more - this is
    // not TrackRepository's full surface (loading is not a mutation, and
    // Library never reads through here). One method per shape of change,
    // because the difference between them is the whole point: a play count is
    // one indexed UPDATE, a finished download is one upsert, and only a rescan
    // or a sync merge is worth rewriting the whole table for. Persisting a
    // single changed track by rewriting all 16k rows is a defect this
    // interface exists to make hard to write - and one the client had in four
    // separate places before Library owned these writes.
    public interface ITrackStore
    {
        void UpdateStats(Track track);

        void SetStarred(StarTarget target, string value, bool starred, DateTimeOffset? starredAt);

        void Upsert(Track track);

        void ReplaceAll(IEnumerable<Track> tracks);
    }

    // The playlist half of the same idea. One method, because a playlist set
    // is small (tens, not thousands) and PlaylistRepository.Save is already an
    // upsert plus a delete-not-in inside one transaction - there is nothing a
    // finer-grained call would save.
    public interface IPlaylistStore
    {
        void Save(IEnumerable<Playlist> playlists);
    }
}
