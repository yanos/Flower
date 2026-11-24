using System;

using Flower.Services;

namespace Flower.Models
{
    // One row of a playlist. A song in a playlist twice is two entries, and
    // EntryId is what tells them apart: minted by the device that added the
    // row, kept through moves, relaunches and sync, and never reused.
    //
    // Track is the library's song, or null while this device cannot play it: a
    // drive that is away, a song only another device has, a catalog that has
    // not arrived. Such a row is hidden but stays in its place and goes out in
    // every push, because a row leaves a playlist only when somebody removes
    // it. AwayTrackId and Song are how it finds its track again.
    public sealed record PlaylistEntry(
        Guid EntryId, Track? Track, Guid? AwayTrackId = null, PlaylistSyncTrackDto? Song = null)
    {
        public static PlaylistEntry Of(Track track) => new(Guid.NewGuid(), track);

        public Guid? TrackId => Track?.Id ?? AwayTrackId;

        public PlaylistSyncTrackDto ToWire() =>
            (Track != null ? PlaylistSyncMapper.ToDto(Track)
                : Song ?? new PlaylistSyncTrackDto(null, null, null, 0, AwayTrackId?.ToKey()))
            with { EntryId = EntryId };
    }
}
