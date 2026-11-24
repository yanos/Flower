using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    string? Title, string? Artists, string? Album, int DurationSeconds, string? Id = null, Guid? EntryId = null)
{
    // Which song this is, as text: the origin's id, else its tags' SyncKey.
    [JsonIgnore]
    public string SongKey => Id is { Length: > 0 } id ? id : Track.BuildSyncKey(Title, Artists, Album, DurationSeconds);
}

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
    bool VibeInstrumental = false,
    // The server's version of this playlist, and the versions it held before
    // (oldest first, the current one not among them) - see PlaylistRevisions.
    // Only the server issues a revision, so a device's clock decides nothing.
    // Sent by the server and ignored in a push: a device says which version
    // its change is based on in PlaylistChangeDto.BaseRevision instead.
    long Revision = 0,
    List<long>? History = null);

// A playlist the server records as deleted, at the revision the deletion was
// given and with the versions it had before. A playlist merely missing from
// the server is not one of these: a server restored from a backup is missing
// everything made since, and that comes back up rather than being deleted from
// every device.
public sealed record PlaylistDeletionDto(Guid Id, long Revision, List<long>? History = null);

// GET /api/flower/v1/playlists: the caller's listener's playlists and recorded
// deletions, each at its revision.
public sealed record PlaylistSyncManifestDto(
    string DeviceFingerprint, List<PlaylistSyncPlaylistDto> Playlists, List<PlaylistDeletionDto>? Deleted = null);

// One change in a POST /api/flower/v1/playlists/changes: this playlist, or its
// deletion, written over the server's version BaseRevision. 0 is a playlist the
// device has no version of: new, or one the server lost.
public sealed record PlaylistChangeDto(Guid Id, long BaseRevision, PlaylistSyncPlaylistDto? Playlist = null, bool Delete = false);

// Every change a session has, in one request - the bulk budget is 60 a minute.
public sealed record PlaylistChangesRequestDto(List<PlaylistChangeDto> Changes);

[JsonConverter(typeof(JsonStringEnumConverter<PlaylistChangeOutcome>))]
public enum PlaylistChangeOutcome
{
    // Written, at Revision.
    Applied,

    // Not written: the server's version is no longer BaseRevision. Current
    // (or CurrentDeletion) is what it holds now, both null when it holds
    // nothing, for the device to merge with and send again.
    Stale,

    // Not written, and never will be: the id is another listener's (see
    // Listeners). The server records it as deleted for this listener over
    // BaseRevision, so the device's next session drops it the ordinary way.
    Refused,
}

// Each change succeeds or fails on its own.
public sealed record PlaylistChangeAnswerDto(
    Guid Id,
    PlaylistChangeOutcome Outcome,
    long Revision = 0,
    PlaylistSyncPlaylistDto? Current = null,
    PlaylistDeletionDto? CurrentDeletion = null);

public sealed record PlaylistChangesResponseDto(List<PlaylistChangeAnswerDto> Answers);
