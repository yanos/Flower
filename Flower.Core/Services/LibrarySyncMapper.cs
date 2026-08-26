using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Maps an OpenSubsonic Child (a peer's song, fetched by RemoteLibraryImporter)
// into a Flower placeholder Track - see SYNC-PLAN.md Phase 3's data model. Path
// stays null (this device doesn't have the file yet); OriginDeviceFingerprint
// records which peer answered, so a later download request goes to the right
// device.
//
// In Flower.Core rather than the app project because its one caller is: the
// browser head reaches the same importer without ever loading Flower.
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
        // Restores the sender's own EffectiveAlbumArtist, which this side would
        // otherwise recompute from two fields that never crossed the wire and so
        // always fell through to the per-track Artists - shattering every
        // various-artists compilation into one album tile per contributor. See
        // Child.DisplayAlbumArtist.
        //
        // Only stored when it actually differs from this song's own artist. The
        // sender's fallback ends at Artists for an ordinary single-artist album,
        // so copying it unconditionally would stamp a redundant AlbumArtists tag
        // identical to Artists onto the overwhelming majority of a library, for
        // no change in grouping. Assigning only the differing case reproduces the
        // sender's EffectiveAlbumArtist exactly in every branch: a real
        // AlbumArtists tag comes back verbatim, a blank-tagged compilation comes
        // back as the "Various Artists" its flag stands for, and an ordinary
        // album falls through to Artists here the same way it did there.
        AlbumArtists = string.IsNullOrWhiteSpace(song.DisplayAlbumArtist) || song.DisplayAlbumArtist == song.Artist
            ? null
            : song.DisplayAlbumArtist,
        IsCompilation = song.IsCompilation,
        Album = song.Album,
        Duration = TimeSpan.FromSeconds(song.Duration ?? 0),
        Genre = song.Genre,
        TrackNumber = (uint)(song.Track is > 0 ? song.Track.Value : 0),
        Year = song.Year?.ToString(),
        Path = null,
        OriginDeviceFingerprint = originDeviceFingerprint,
        // Kept verbatim - an OpenSubsonic id is opaque to a client, and this is
        // what a later stream/download request is addressed with. See
        // Track.OriginTrackId for what re-deriving it instead used to break.
        OriginTrackId = song.Id,
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
        // Null from a third-party server, and null for a track nobody has
        // played - both simply mean "not in History", which is what an unset
        // LastPlayedAt already means.
        LastPlayedAt = song.LastPlayed,
    };
}
