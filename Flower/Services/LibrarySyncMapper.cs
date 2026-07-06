using System;

using Flower.Models;

namespace Flower.Services;

// Maps an OpenSubsonic Child (a peer's song, fetched via LibrarySyncService) into
// a Flower placeholder Track - see SYNC-PLAN.md Phase 3's data model. Path stays
// null (this device doesn't have the file yet); OriginDeviceFingerprint records
// which peer answered, so a later download request goes to the right device.
public static class LibrarySyncMapper
{
    public static Track ToPlaceholderTrack(Child song, string originDeviceFingerprint) => new Track
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
    };
}
