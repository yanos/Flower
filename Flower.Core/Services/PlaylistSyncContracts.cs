using System;
using System.Collections.Generic;

using Flower.Models;

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
// the receiving side just replaces its playlist collection to match - no merge logic
// runs on that end, avoiding two independent (and possibly divergent) conflict
// resolutions for the same sync session.
public sealed record PlaylistSyncManifestDto(string DeviceFingerprint, List<PlaylistSyncPlaylistDto> Playlists);
