using System;
using System.Collections.Generic;
using System.Linq;
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

        // The tag's own "part of a compilation" flag (ID3 TCMP / MP4 cpil) - the
        // conventional signal tagging software uses for a various-artists album,
        // independent of whether AlbumArtists was also filled in. See
        // EffectiveAlbumArtist below: many real compilations in the wild have
        // this flag set but AlbumArtists left blank, so the flag is needed as
        // its own fallback rather than trusting AlbumArtists alone.
        public bool IsCompilation { get; set; }

        // The artist to group/display an album by. Prefers, in order: the tag's
        // own AlbumArtists (conventionally consistent across every track on the
        // album, e.g. "Various Artists"); then, if the compilation flag is set
        // but AlbumArtists was left blank, a literal "Various Artists" so every
        // track in the compilation still resolves to the same grouping key; then
        // falls back to the per-track Artists for an ordinary single-artist
        // album with neither tag populated. See RecentlyAddedAlbumsBuilder/
        // AlbumGridBuilder/LibraryOpenSubsonicMapper, which all group or label
        // albums by this rather than by Artists directly - otherwise a various-
        // artists compilation (same Album, differing per-track Artists) would
        // fragment into one tile/entry per distinct track artist.
        [JsonIgnore]
        public string EffectiveAlbumArtist =>
            !string.IsNullOrWhiteSpace(AlbumArtists) ? AlbumArtists
            : IsCompilation ? "Various Artists"
            : Artists ?? "";

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

        // Set only for a placeholder track (Path == null) known via library sync
        // but not yet downloaded (see LibrarySyncService, SYNC-PLAN.md Phase 3) -
        // which peer currently holds the real file, so a later download request
        // goes to that device instead of guessing. Meaningless once Path is set,
        // and never set on a track this device actually imported itself.
        public string? OriginDeviceFingerprint { get; set; }

        // The origin peer's file extension (no leading dot - see
        // LibraryOpenSubsonicMapper.ToChild's Suffix field), needed at download
        // time to give the saved file a real extension since Path is null until
        // then. Same lifetime/meaning as OriginDeviceFingerprint.
        public string? OriginFileExtension { get; set; }

        // SHA256 hash (hex) of the origin peer's album art bytes at last sync -
        // see LibraryOpenSubsonicMapper's CoverArt field and AlbumArtLoader's
        // remote-fetch path. Used as the local disk cache key for synced art, so
        // a changed hash (art replaced on the origin device) naturally produces a
        // cache miss and re-fetch instead of needing separate invalidation logic.
        // Null if the peer's album currently has no art. Same lifetime as
        // OriginDeviceFingerprint.
        public string? OriginAlbumArtHash { get; set; }

        // Stats. PlayCount is Flower's own count, incremented on natural
        // end-of-track (see PlaylistControlViewModel); ImportedPlayCount comes
        // from iTunes/Music.app's library export when that sync is enabled
        // (see ITunesPlayCountImporter) - kept as separate fields so re-running
        // (or disabling) the import can never clobber plays Flower itself
        // recorded. TrackRowViewModel.PlayCountDisplay is what sums the two
        // for display.
        public int PlayCount { get; set; }
        public int ImportedPlayCount { get; set; }

        // Latest known play count reported by each OTHER device, keyed by
        // DeviceIdentity.Fingerprint - see LibraryOpenSubsonicMapper.ToChild's
        // PlayCounts field and Library.MergeSyncedTracks. Never contains this
        // device's own fingerprint: this device's own contribution always lives
        // in PlayCount/ImportedPlayCount above, live-incremented locally, never
        // written here via a sync merge (LibrarySyncMapper.ToPlaceholderTrack
        // strips it out of an incoming report before it gets this far). Merged
        // per-key by max - a device's own reported count only ever grows, so
        // applying the same (or a relayed, multi-hop) report more than once, in
        // any order, converges instead of double-counting or regressing.
        public Dictionary<string, int> RemotePlayCounts { get; set; } = new();

        // The combined total across every device this track's play count is
        // known for. One shared computation so TrackRowViewModel.PlayCountDisplay
        // and TrackListBuilder's PlayCount sort can't independently drift on the
        // formula the way two copies of it eventually would.
        [JsonIgnore]
        public int TotalPlayCount => PlayCount + ImportedPlayCount + RemotePlayCounts.Values.Sum();

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
        //
        // Rounded, not truncated - confirmed against a real Music.app export where
        // TagLib's own parse of a file's duration (172.01s) and iTunes's recorded
        // Total Time for the same file (171.96s) straddled a whole-second boundary;
        // truncating both put them on opposite sides (172 vs 171), so the sync key
        // never matched and that track's "Date Added"/play count silently never
        // synced. Rounding both to the nearest second still isn't foolproof against
        // every possible boundary case, but it fixes the one actually observed and
        // narrows the remaining risk window to values within ~5ms of an exact .5s.
        [JsonIgnore]
        public string SyncKey => BuildSyncKey(Title, Artists, Album, RoundedSeconds(Duration));

        // Shared with PlaylistSyncPlanner, which builds the same key from the wire
        // DTO (PlaylistSyncTrackDto) on the other side of a sync - both must
        // normalize identically or every cross-device track match silently fails.
        public static string BuildSyncKey(string? title, string? artists, string? album, int durationSeconds) =>
            $"{Normalize(title)}|{Normalize(artists)}|{Normalize(album)}|{durationSeconds}";

        // The ONE place "seconds, rounded to the nearest whole one" gets computed -
        // every other spot that needs a duration as a bare int for identity
        // purposes (LibraryOpenSubsonicMapper.ToChild's Duration field,
        // PlaylistSyncMapper.ToDto, ITunesPlayCountImporter/ITunesDateAddedImporter)
        // calls this rather than re-deriving Math.Round(...) inline - a second,
        // independently-written copy of the same rounding rule is exactly how a
        // previous version of ToChild's Duration field ended up truncating
        // instead of rounding, silently mismatching this property for any
        // duration whose fractional part was >= .5s (confirmed on a real device:
        // a 369.888s track advertised as Duration: 369 while this property
        // correctly said 370, so a peer's later stream request carried a SyncKey
        // this device could never match against its own track). The double
        // overload exists because not every caller starts from a TimeSpan -
        // the iTunes importers parse milliseconds straight out of a plist.
        public static int RoundedSeconds(TimeSpan duration) => RoundedSeconds(duration.TotalSeconds);
        public static int RoundedSeconds(double totalSeconds) => (int)Math.Round(totalSeconds);

        // Fallback identity for ITunesDateAddedImporter/ITunesPlayCountImporter,
        // used only when BuildSyncKey finds no match and this resolves to exactly
        // one candidate. Confirmed necessary against a real VBR-encoded MP3 where
        // TagLib's parsed duration (1222.5s, matching file size/bitrate math) and
        // Music.app's own recorded Total Time (631.9s - almost exactly half, a
        // known old-iTunes VBR-header mis-parse) disagreed by ~10 minutes, not a
        // rounding-boundary fraction of a second - no amount of rounding closes a
        // gap that size, so duration has to be droppable entirely as a last
        // resort. Title+Artist+Album alone is already unique for the overwhelming
        // majority of a personal library; duration exists to disambiguate the
        // rare case of two genuinely different same-titled tracks, so this is
        // only safe when there is nothing left to disambiguate between.
        public static string BuildLooseKey(string? title, string? artists, string? album) =>
            $"{Normalize(title)}|{Normalize(artists)}|{Normalize(album)}";

        private static string Normalize(string? value) =>
            value?.Trim().ToLowerInvariant() ?? "";
    }
}
