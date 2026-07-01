using System;

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
    }
}
