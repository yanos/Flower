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

        // Removing tracks from the library on purpose - see Library.RemoveTracks.
        // A handful of rows, so a delete by id rather than a whole-table rewrite.
        void Delete(IReadOnlyCollection<Guid> ids);
    }

    // The files a user removed from the library while keeping them on disk,
    // which every rescan has to leave out - see Library.RemoveTracks. Its own
    // interface rather than more of ITrackStore because this one is read as
    // well as written: Library loads the set once, when it is built.
    public interface IExcludedPathStore
    {
        IReadOnlyList<ExcludedPath> LoadExcludedPaths();

        void AddExcludedPaths(IReadOnlyCollection<string> paths);

        // Settings' "Removed Songs" Restore - see Library.RestoreExcludedPaths.
        void RemoveExcludedPaths(IReadOnlyCollection<string> paths);
    }

    // A file removed from the library and kept on disk, and when.
    public sealed record ExcludedPath(string Path, DateTimeOffset ExcludedAt);

    // A song that left the library, and what the library knew about it when it
    // did - see Library.RemovedTracks. Track is the song as it was, Path and
    // all, which is what a returning file is recognised by and restored from.
    //
    // OwedToOrigin is true while the paired server still has to be told. Only
    // a file that went missing from under a scan sets it: a removal the user
    // asked for tells the server before anything is removed here (see
    // LibraryRemovalService), so by the time there is a record there is
    // nothing left to say.
    //
    // Deliberate is true for a song somebody removed - here, or on a device
    // whose removal this library was told of - and false for one a scan simply
    // stopped finding. A server tells its devices about the first kind (see
    // LibrarySyncManifestDto.Removed), so the song leaves their libraries too.
    // It says nothing about the second: a music folder that failed to mount is
    // not an instruction to every device to drop its own copies.
    public sealed record RemovedTrack(
        Track Track, DateTimeOffset RemovedAt, bool OwedToOrigin = false, bool Deliberate = false);

    // Where those are kept. Read as well as written, like the exclusions:
    // Library loads the set once, when it is built.
    public interface IRemovedTrackStore
    {
        IReadOnlyList<RemovedTrack> LoadRemovedTracks();

        // Insert or replace, by the track's id.
        void SaveRemovedTracks(IReadOnlyCollection<RemovedTrack> removed);

        void DeleteRemovedTracks(IReadOnlyCollection<Guid> ids);
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
