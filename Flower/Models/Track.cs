using System;
using System.Text.Json.Serialization;

namespace Flower.Models
{
    public record Track
    {
        // Core identity
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? Artists { get; set; }
        public string? AlbumArtists { get; set; }
        public string? Album { get; set; }
        public string? AlbumSort { get; set; }
        public string? Year { get; set; }
        public uint TrackNumber { get; set; }
        public uint TrackCount { get; set; }
        public uint DiscNumber { get; set; }
        public uint DiscCount { get; set; }

        // People
        public string? Composers { get; set; }
        public string? Conductor { get; set; }
        public string? RemixedBy { get; set; }

        // Classification
        public string? Genre { get; set; }
        public uint BeatsPerMinute { get; set; }
        public string? InitialKey { get; set; }
        public string? Grouping { get; set; }
        public string? Publisher { get; set; }
        public string? ISRC { get; set; }

        // Descriptions
        public string? Comment { get; set; }
        public string? Description { get; set; }
        public string? Copyright { get; set; }
        public string? Lyrics { get; set; }

        // Audio technical
        public TimeSpan Duration { get; set; }
        public int Bitrate { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public string? Codec { get; set; }

        // File
        public string? Path { get; set; }

        // Stats
        public int PlayCount { get; set; }

        // When this track first appeared in the library. Defaults to "now" for a
        // freshly-imported Track; Library.UpdateTracks carries the original value
        // forward across rescans by matching Path, so it only reflects the first
        // import, not the most recent one. Drives the "Recently Added" sidebar section.
        public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;

        // Cross-device identity for playlist sync (see PlaylistSyncService): Path is
        // a local filesystem path and never matches between two devices' libraries,
        // so playlist track membership is matched by this fingerprint instead. Not
        // persisted - computed on demand, ignored by both library.json and the sync
        // wire DTOs (which carry the same fields directly).
        [JsonIgnore]
        public string SyncKey => BuildSyncKey(Title, Artists, Album, (int)Duration.TotalSeconds);

        // Shared with PlaylistSyncPlanner, which builds the same key from the wire
        // DTO (PlaylistSyncTrackDto) on the other side of a sync - both must
        // normalize identically or every cross-device track match silently fails.
        public static string BuildSyncKey(string? title, string? artists, string? album, int durationSeconds) =>
            $"{Normalize(title)}|{Normalize(artists)}|{Normalize(album)}|{durationSeconds}";

        private static string Normalize(string? value) =>
            value?.Trim().ToLowerInvariant() ?? "";
    }
}
