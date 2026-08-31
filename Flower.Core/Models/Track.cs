using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Flower.Models
{
    // A plain sealed class, deliberately not a record, even though every
    // property below is a mutable auto-property that would have made `record`
    // look like the natural choice.
    //
    // A record synthesizes value-based Equals/GetHashCode across *every*
    // property, and that synthesized equality is what List<Track>.IndexOf/
    // Contains/Remove and HashSet<Track> actually use - so Playlist.
    // Playlist navigation/RemoveTrack, the Track Info window's
    // prev/next navigation, PlaylistControlViewModel's shuffle re-roll, and
    // Library.MergeSyncedTracks were all silently deciding "same track?" by
    // comparing ~40 fields including a Dictionary (which compares by
    // reference, so two logically identical tracks never matched on it
    // anyway). Two genuinely different tracks that happen to share tag data -
    // untagged files, "Track 01", silence tracks, two rips of one song -
    // were indistinguishable to all of them, and every lookup cost O(n x 40
    // field comparisons) on a 16k-track queue. Records also promise
    // immutability this type has never had: nothing here uses `with` for
    // anything but a deliberate copy, and PlayCount++/tag edits/Path being
    // set after a download all mutate in place, on purpose, because the same
    // instance is shared between Library.Tracks, a Playlist's Tracks, and
    // whatever ViewModel is showing it.
    //
    // Id below is the identity instead - see its own comment.
    public sealed class Track : IEquatable<Track>
    {
        // Stable surrogate identity, minted once when a Track is first
        // constructed (import, sync placeholder, test) and carried forward
        // across rescans by Library.UpdateTracks the same way DateAdded is.
        //
        // Everything else that has ever been used to identify a track is
        // derived from mutable data and only valid within one layer: Path is
        // a local filesystem path that doesn't exist on a not-yet-downloaded
        // track and changes out from under iOS on a reinstall; SyncKey is a
        // cross-device *matching heuristic* built from tags and a rounded
        // duration. Both are still needed for what they do - matching a fresh
        // scan against the previous library, and matching this device's
        // library against a peer's - but neither is an identity, and using
        // them as one is what made a track's identity depend on whether its
        // file happened to be downloaded yet.
        //
        // Minted at construction so every Track has one from the moment it
        // exists, whether it came from a scan, a peer's catalog, or a
        // deserialized library.json; the id in the file wins on load.
        public Guid Id { get; set; } = Guid.NewGuid();

        // Core identity
        // Backed rather than auto-implemented so SyncKey's cache can be
        // invalidated - see SyncKey.
        public string? Title { get => _title; set { _title = value; ClearSyncKey(); } }
        private string? _title;
        public string? Subtitle { get; set; }
        public string? Artists { get => _artists; set { _artists = value; ClearSyncKey(); } }
        private string? _artists;
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

        public string? Album { get => _album; set { _album = value; ClearSyncKey(); } }
        private string? _album;
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
        [JsonConverter(typeof(TimeSpanTicksConverter))]
        public TimeSpan Duration { get => _duration; set { _duration = value; ClearSyncKey(); } }
        private TimeSpan _duration;
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

        // The id the origin peer (or a third-party OpenSubsonic server) gave
        // this track in its own catalog - the Child.Id this track was built
        // from, kept verbatim, because OpenSubsonic ids are opaque to a client
        // by specification: the only correct thing to do with one is hand it
        // back. It is what /rest/stream and /rest/download are asked for (see
        // LibraryDownloadService, MainViewModel.GetStreamUrl).
        //
        // This used to be re-derived on demand as this device's own SyncKey
        // instead, on the assumption that the far side would recompute the
        // same string from the same tags. Two ways that was wrong: a tag edit
        // on the serving device changes its SyncKey, so every reference a peer
        // was still holding silently 404'd; and a standalone Flower.Server
        // (whose ids are database row ids - see SubsonicMapper.ToChild) never
        // computed a SyncKey for anything, so it could not have matched one at
        // all. Storing what the peer actually said removes the guess. Same
        // lifetime/meaning as OriginDeviceFingerprint.
        public string? OriginTrackId { get; set; }

        // The origin peer's file extension (no leading dot - see
        // LibraryOpenSubsonicMapper.ToChild's Suffix field), needed at download
        // time to give the saved file a real extension since Path is null until
        // then. Same lifetime/meaning as OriginDeviceFingerprint.
        public string? OriginFileExtension { get; set; }

        // The origin peer's path for this file, relative to whichever library
        // folder it sits under and separated by '/' - see Child.RelativePath,
        // which is where it comes from and why only that part of the path
        // travels. What a download names the file it saves, so a downloaded
        // track lands at "<downloads>/Angine de Poitrine/Vol.II/01 Fabienk.mp3"
        // rather than under this device's own opaque track id. Null when the
        // origin is a third-party server that sends no such field, which is what
        // OriginFileExtension above still covers. Same lifetime/meaning as
        // OriginDeviceFingerprint.
        public string? OriginRelativePath { get; set; }

        // SHA256 hash (hex) of the origin peer's album art bytes at last sync -
        // see LibraryOpenSubsonicMapper's CoverArt field and AlbumArtLoader's
        // remote-fetch path. Used as the local disk cache key for synced art, so
        // a changed hash (art replaced on the origin device) naturally produces a
        // cache miss and re-fetch instead of needing separate invalidation logic.
        // Null if the peer's album currently has no art. Same lifetime as
        // OriginDeviceFingerprint.
        public string? OriginAlbumArtHash { get; set; }

        // True when *this* device fetched this file from a peer and put it
        // where it now sits (see LibraryDownloadService) - as opposed to a file
        // the importer found by scanning a folder the user configured.
        //
        // The distinction exists for one question, asked once per rescan: a
        // scan that did not produce this track, does that mean the track is
        // gone? For a scanned file, yes - the folder list is the whole of what
        // the user asked Flower to scan, so a file no longer in it is no longer
        // in the library (see Library.UpdateTracks). For a downloaded one, no:
        // it can live in platform-private storage no scan ever looks at
        // (Android's app-private Downloads folder in particular), so the scan
        // was never going to find it and its silence proves nothing.
        //
        // That question used to be answered with OriginDeviceFingerprint != null
        // instead, on the strength of that field's own documented promise just
        // above - "never set on a track this device actually imported itself".
        // MergeSyncedTracks broke the promise deliberately and for a good reason
        // (it stamps the origin onto a local file the paired server also has, so
        // the delete-downloaded-file warning can tell "safe, re-downloadable"
        // from "permanent"), which quietly turned the predicate into "every
        // track" on any paired device - and a library that can never be emptied,
        // no matter what is on disk. This field answers the question directly
        // rather than through a proxy that something else is free to redefine.
        public bool IsLocallyDownloaded { get; set; }

        // Stats. PlayCount is Flower's own count, incremented on natural
        // end-of-track (see PlaylistControlViewModel); ImportedPlayCount comes
        // from iTunes/Music.app's library export when that sync is enabled
        // (see ITunesPlayCountImporter) - kept as separate fields so re-running
        // (or disabling) the import can never clobber plays Flower itself
        // recorded. TrackRowViewModel.PlayCountDisplay is what sums the two
        // for display.
        public int PlayCount { get; set; }
        public int ImportedPlayCount { get; set; }

        // When this track last started playing (see PlaylistControlViewModel.Play,
        // via Library.RecordPlayed) - null if never played. Deliberately stamped at
        // play-start, not on natural end-of-track like PlayCount above: "History"
        // (the sidebar view this drives) means "you played this", not "you sat
        // through the whole thing". Library.UpdateTracks carries it forward across
        // rescans the same way DateAdded/PlayCount are.
        public DateTimeOffset? LastPlayedAt { get; set; }

        // Subsonic's "starred" flag, set through /rest/star and /rest/unstar
        // (see Flower.Server's SubsonicEndpoints) and reported back on every
        // song it serves. On the Track model rather than in a server-only
        // table because it is a property of the track everywhere - the client
        // has no UI for it yet, but a liked-songs view is the obvious next
        // consumer, and CarryForwardMutableState already named Starred as the
        // example of the kind of field a rescan must not reset.
        public bool Starred { get; set; }
        public DateTimeOffset? StarredAt { get; set; }

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
        //
        // Cached, because this is read in tight loops over the whole library -
        // Library.UpdateTracks and MergeSyncedTracks both build SyncKey-keyed
        // dictionaries and HashSets over every track, as do both iTunes
        // importers - and recomputing it allocates four normalized strings plus
        // the joined result every single read: ~100k allocations per rescan for
        // nothing. Invalidated by the setters of the four fields it derives
        // from (see ClearSyncKey), so a tag edit in TrackInfoWindow still takes
        // effect. See docs/ARCHITECTURE-REVIEW.md Tier 1.5.
        [JsonIgnore]
        public string SyncKey => _syncKey ??= BuildSyncKey(Title, Artists, Album, RoundedSeconds(Duration));

        private string? _syncKey;

        private void ClearSyncKey() => _syncKey = null;

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

        // Identity is Id and nothing else. Note what this does NOT fix: adding
        // the same track to a playlist twice puts the same instance (hence the
        // same Id) in the list twice, so IndexOf still resolves to the first
        // occurrence. Telling those two apart needs the queue to track a
        // position rather than a track - see docs/ARCHITECTURE-REVIEW.md.
        public bool Equals(Track? other) => other is not null && Id == other.Id;

        public override bool Equals(object? obj) => Equals(obj as Track);

        public override int GetHashCode() => Id.GetHashCode();

        // Defined so the many existing `trackA == trackB` call sites keep
        // meaning "the same track" rather than silently switching to reference
        // equality the moment this stopped being a record.
        public static bool operator ==(Track? left, Track? right) =>
            left is null ? right is null : left.Equals(right);

        public static bool operator !=(Track? left, Track? right) => !(left == right);

        // Replaces the `with` expressions this type used to support. Keeps Id,
        // so a copy made to hand a peer stream URL to the audio manager (see
        // PlaylistControlViewModel.ResolveForPlayback) is still *the same track*
        // as far as the play queue is concerned - which the record-equality
        // version was not, since the differing Path made it compare unequal and
        // queue navigation then fell back to the front of the queue.
        public Track Clone()
        {
            var copy = (Track)MemberwiseClone();
            // MemberwiseClone is shallow, and this is the one reference-typed
            // field callers mutate (Library.MergeRemotePlayCounts writes into
            // it in place) - sharing it would make a copy's merges land on the
            // original too.
            copy.RemotePlayCounts = new Dictionary<string, int>(RemotePlayCounts);
            return copy;
        }
    }
}
