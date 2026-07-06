using System.Collections.Generic;

namespace Flower.Services;

// Wire shape for the bespoke, Flower-to-Flower-only full-library sync endpoint
// (GET /api/flower/v1/library) - see LibrarySyncService. Deliberately NOT the
// OpenSubsonic-shaped getAlbumList2/getAlbum pair: those stay in place for
// real OpenSubsonic-protocol interop (a third-party client browsing a
// Flower-hosted library), but require one request per album, which for a
// library of hundreds/thousands of albums means hundreds/thousands of
// individual connections - observed in practice as heavy iOS nw_connection
// log churn (and, more importantly, the real network/battery cost behind
// it). This single bulk endpoint returns every real track in one response
// instead, the same way /api/flower/v1/playlists already does for playlists.
public sealed record LibrarySyncManifestDto(string DeviceFingerprint, List<Child> Songs);
