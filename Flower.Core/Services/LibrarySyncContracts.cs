using System.Collections.Generic;

namespace Flower.Services;

// Wire shape for the bespoke, Flower-to-Flower-only full-library sync endpoint
// (GET /api/flower/v1/library) - see LibrarySyncService. Deliberately NOT the
// OpenSubsonic-shaped getAlbumList2/getAlbum pair this project started with:
// that pair required one request per album, which for a
// library of hundreds/thousands of albums means hundreds/thousands of
// individual connections - observed in practice as heavy iOS nw_connection
// log churn (and, more importantly, the real network/battery cost behind
// it). This single bulk endpoint returns every real track in one response
// instead, the same way /api/flower/v1/playlists already does for playlists.
//
// In Flower.Core, not the app project, because it is the shape a client sends
// and a server answers: Flower.Server's SyncEndpoints serves it, and a paired
// client pulls its whole catalog through exactly this shape.
//
// Removed is the ids of the songs taken out of this library on purpose - by
// its owner, or by one of the owner's devices deleting its own copy - for as
// long as the library keeps a record of them (Library.RemovedTracks, which is
// until a song comes back). It is what lets "removed" travel as a statement
// rather than be inferred from absence: a device drops its own copy of a song
// named here, and keeps its copy of one that is merely missing from Songs,
// because a catalog can be short for reasons that are nobody's decision - a
// music folder that did not mount lists nothing at all. See
// Library.MergeSyncedTracks.
//
// ListeningToken is the server's Library.ListeningToken as of the listening
// state in Songs, for an owner's device to follow GET /listening from. Null in
// a guest's catalog, which carries nobody's listening but its own.
public sealed record LibrarySyncManifestDto(
    string DeviceFingerprint, List<TrackDto> Songs, List<string>? Removed = null, string? ListeningToken = null);

// The listening half of the catalog, for only the songs whose listening moved
// since the token a device last held (GET /api/flower/v1/listening?since=):
// who played each and how often, when last, and where a listen was left - see
// Library.ListeningToken for why plays have a token of their own. Token is
// where this leaves the device, and what it sends as `since` next time.
//
// Owner's devices only. A guest's catalog carries its own count and nobody
// else's listening (SyncEndpoints.ForGuest), and its own count is one it
// reported itself, so a guest has nothing to follow here - and telling it
// which songs somebody else just played is exactly what ForGuest withholds.
public sealed record ListeningChangesDto(string Token, List<TrackListeningDto> Songs);

// One song's listening, in TrackDto's spelling for the same fields. The three
// options are not listening, but ride along because a pull applies them
// together with the resume position, from whichever side played last
// (Library.MergePlaybackOptions) - a delta without them would apply half of
// that rule.
public sealed record TrackListeningDto(
    string Id,
    Dictionary<string, int>? PlayCounts = null,
    int? ImportedPlayCount = null,
    System.DateTimeOffset? LastPlayed = null,
    int? ResumePositionSeconds = null,
    bool RememberPlaybackPosition = false,
    bool IgnoreWhenShuffling = false,
    int VolumeAdjustment = 0);
