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
public sealed record LibrarySyncManifestDto(string DeviceFingerprint, List<TrackDto> Songs, List<string>? Removed = null);
