using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Maps an OpenSubsonic Child (a peer's song, fetched via LibrarySyncService) into
// a Flower placeholder Track - see SYNC-PLAN.md Phase 3's data model. Path stays
// null (this device doesn't have the file yet); OriginDeviceFingerprint records
// which peer answered, so a later download request goes to the right device.
public static class LibrarySyncMapper
{
    // ownFingerprint is this device's own DeviceIdentity.Fingerprint - excluded
    // from the incoming song.PlayCounts before it becomes RemotePlayCounts, since
    // an entry under our own fingerprint would just be a peer echoing back what
    // it previously learned about us; our own play count is always authoritative
    // locally (Track.PlayCount, live-incremented) and must never be overwritten
    // by something arriving over sync.
    public static Track ToPlaceholderTrack(Child song, string originDeviceFingerprint, string ownFingerprint) => new Track
    {
        Title = song.Title,
        Artists = song.Artist,
        Album = song.Album,
        Duration = TimeSpan.FromSeconds(song.Duration ?? 0),
        Genre = song.Genre,
        TrackNumber = (uint)(song.Track is > 0 ? song.Track.Value : 0),
        Year = song.Year?.ToString(),
        Path = null,
        OriginDeviceFingerprint = originDeviceFingerprint,
        OriginFileExtension = song.Suffix,
        OriginAlbumArtHash = song.CoverArt,
        RemotePlayCounts = (song.PlayCounts ?? new Dictionary<string, int>())
            .Where(kv => kv.Key != ownFingerprint)
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        // Falls back to the Track record's own "now" default (see Track.DateAdded)
        // when talking to a third-party server that doesn't send this - see
        // Child.DateAdded's own doc comment for why this matters for Recently
        // Added parity between a Client and its paired Server.
        DateAdded = song.DateAdded ?? DateTimeOffset.UtcNow,
    };
}
