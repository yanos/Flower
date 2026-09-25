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
public sealed record PlaylistSyncTrackDto(string? Title, string? Artists, string? Album, int DurationSeconds, string? Id = null);

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
    SmartPlaylistRules? Rules = null);

// GET /api/flower/v1/playlists returns one of these describing the responding
// device's current playlists. POST /api/flower/v1/playlists/apply sends one back:
// by the time a POST happens the initiator has already resolved every conflict, so
// the receiving side runs no conflict resolution of its own. It does not replace its
// collection wholesale, though - see PlaylistSyncMapper.ApplyPushedManifest: a
// playlist missing from the push is kept unless Deleted names it, and a pushed copy
// no newer than the one held is not taken.
public sealed record PlaylistSyncManifestDto(
    string DeviceFingerprint, List<PlaylistSyncPlaylistDto> Playlists, List<Guid>? Deleted = null);
