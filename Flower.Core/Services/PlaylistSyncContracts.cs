using System;
using System.Collections.Generic;

using Flower.Models;

namespace Flower.Services;

// Wire shapes for the playlist sync protocol (see PlaylistSyncService / SYNC-PLAN.md
// Phase 2). Deliberately separate from Flower.Models.Playlist/Track: the wire format
// only needs enough of a track to compute Track.SyncKey on the far side (Path is a
// local filesystem path and never means the same thing on two devices), and needs a
// stable Id/UpdatedAt pair that the local Playlist model didn't have before sync.

// Id is the song's id in its origin's catalog - the same value TrackDto.Id
// carries, and Track.OriginTrackId holds on a device that pulled it. Tried
// before the tags, because the tags are not always something both ends read
// the same way: a file with no title tag goes out in the catalog under its
// file name and in a playlist under nothing, and the two never matched. Null
// for a song only the sender has, which the far side then looks for by tags
// alone, the way every entry used to be.
//
// EntryId is the playlist row this is (PlaylistEntry). Null where a song is sent
// without being a row of anything, as Vibe's ranked songs are.
public sealed record PlaylistSyncTrackDto(
    string? Title, string? Artists, string? Album, int DurationSeconds, string? Id = null, Guid? EntryId = null);

public sealed record PlaylistSyncPlaylistDto(
    Guid Id,
    string Name,
    DateTimeOffset UpdatedAt,
    List<PlaylistSyncTrackDto> Tracks,
    // The query, for a smart playlist - null for an ordinary one. This, not
    // Tracks, is what actually syncs about a smart playlist: each device
    // evaluates it against its own library, which is the wanted behaviour
    // rather than a compromise (on a phone holding a subset, "Recently Added"
    // should mean recently added there). Tracks still travels, and is still
    // what a peer holding no rules of its own ends up with.
    SmartPlaylistRules? Rules = null,
    // A folder (Playlist.IsFolder), which carries no tracks, and the folder
    // this playlist sits in. Moving a playlist is an edit of the playlist, so
    // it travels the way a rename does: a newer UpdatedAt with a new ParentId.
    // Its place among its folder's playlists (Playlist.SortKey) travels the
    // same way.
    bool IsFolder = false,
    Guid? ParentId = null,
    double? SortKey = null,
    // A Vibe Playlist's sentence and No vocals switch (Playlist.VibePrompt),
    // null and false for every other kind. Like Rules, this is what a
    // device's edit carries; unlike Rules, Tracks is the answer to it when it
    // comes from the server, which ranked the pool - a paired device never
    // ranks anything (docs/VIBE-PLAYLIST-PLAN.md, "Model of a playlist").
    string? VibePrompt = null,
    bool VibeInstrumental = false);

// GET /api/flower/v1/playlists returns one of these describing the responding
// device's current playlists. POST /api/flower/v1/playlists/apply sends one back:
// by the time a POST happens the initiator has already resolved every conflict, so
// the receiving side runs no conflict resolution of its own. It does not replace its
// collection wholesale, though - see PlaylistSyncMapper.ApplyPushedManifest: a
// playlist missing from the push is kept unless Deleted names it, and a pushed copy
// no newer than the one held is not taken.
//
// On a GET, Deleted is the server's record of what was deleted for this
// listener (IPlaylistDeletionLog). It is the only thing that makes a playlist
// missing from the answer a deletion: a server restored from a backup is
// missing everything made since, and that must come back up rather than be
// deleted from every device.
public sealed record PlaylistSyncManifestDto(
    string DeviceFingerprint, List<PlaylistSyncPlaylistDto> Playlists, List<Guid>? Deleted = null);

// The answer to POST /api/flower/v1/playlists/apply. Refused names the pushed
// or deleted ids the server did not apply because they belong to another
// listener (see Listeners) - the owner's playlists, pushed by a guest device
// that kept copies from before playlists were kept apart. Empty, never null,
// from an honest push.
//
// Said rather than silently dropped so the refusal shows in the pushing
// device's log. The device deletes nothing on the strength of it: the server
// records those ids as deleted for this listener, which they are as far as it
// is concerned, and the device's next session reads that record and drops them
// the ordinary way (see PlaylistSyncPlanner) - so a forged answer cannot remove
// a playlist that the server still has for this device.
public sealed record PlaylistApplyResponseDto(List<Guid> Refused);

// A server's record of the playlists deleted from it, per listener. Kept
// because a playlist's absence proves nothing: see PlaylistSyncManifestDto.
public interface IPlaylistDeletionLog
{
    IReadOnlySet<Guid> DeletedFor(string listener);
    void Record(string listener, IReadOnlyCollection<Guid> playlistIds);

    // These exist again: pushed back by a device that kept an edit of them.
    void Forget(string listener, IReadOnlyCollection<Guid> playlistIds);
}

// What ApplyPushedManifest did: the pushing listener's playlists as now
// installed, and what it refused.
public sealed record PlaylistApplyResult(List<Playlist> Installed, List<Guid> Refused);
