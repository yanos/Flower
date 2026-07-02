using System;
using System.Collections.Generic;

namespace Flower.Services;

// Wire shapes for the playlist sync protocol (see PlaylistSyncService / SYNC-PLAN.md
// Phase 2). Deliberately separate from Flower.Models.Playlist/Track: the wire format
// only needs enough of a track to compute Track.SyncKey on the far side (Path is a
// local filesystem path and never means the same thing on two devices), and needs a
// stable Id/UpdatedAt pair that the local Playlist model didn't have before sync.

public sealed record PlaylistSyncTrackDto(string? Title, string? Artists, string? Album, int DurationSeconds);

public sealed record PlaylistSyncPlaylistDto(
    Guid Id,
    string Name,
    DateTimeOffset UpdatedAt,
    List<PlaylistSyncTrackDto> Tracks);

// GET /api/flower/v1/playlists returns one of these describing the responding
// device's current playlists. POST /api/flower/v1/playlists/apply sends one back:
// by the time a POST happens the initiator has already resolved every conflict, so
// the receiving side just replaces its playlist collection to match - no merge logic
// runs on that end, avoiding two independent (and possibly divergent) conflict
// resolutions for the same sync session.
public sealed record PlaylistSyncManifestDto(string DeviceFingerprint, List<PlaylistSyncPlaylistDto> Playlists);
