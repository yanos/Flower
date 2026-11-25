namespace Flower.Persistence.Sql
{
    // The one definition of Flower's tables, shared by the client and (once
    // ported off EF Core) Flower.Server - see FlowerDb's remarks and
    // docs/ARCHITECTURE-REVIEW.md Tier 4.1.
    //
    // Conventions, all of which exist for a concrete reason:
    //
    // - Guids are stored as 32-char hex ('N' format), not BLOBs, so a database
    //   opened in any SQLite browser is readable and an id pasted from a log
    //   matches literally.
    // - Timestamps are INTEGER UTC ticks, never a datetime type. SQLite has no
    //   DateTimeOffset, and confirmed the hard way while evaluating EF Core:
    //   asking it to ORDER BY one throws NotSupportedException outright. Ticks
    //   sort correctly as integers, which is exactly what Recently Added
    //   (date_added) and History (last_played_at) need.
    // - Durations are INTEGER ticks, matching Track.Duration's TimeSpan and
    //   TimeSpanTicksConverter's existing choice on the JSON side.
    public static class Schema
    {
        public const string Baseline = """
            CREATE TABLE tracks (
                id                        TEXT    NOT NULL PRIMARY KEY,

                path                      TEXT,

                title                     TEXT,
                subtitle                  TEXT,
                artists                   TEXT,
                album_artists             TEXT,
                is_compilation            INTEGER NOT NULL DEFAULT 0,

                -- Dropped by SqliteMigrations' step 21.
                album_artist              TEXT    NOT NULL DEFAULT '',

                album                     TEXT,
                album_sort                TEXT,
                year                      TEXT,
                track_number              INTEGER NOT NULL DEFAULT 0,
                track_count               INTEGER NOT NULL DEFAULT 0,
                disc_number               INTEGER NOT NULL DEFAULT 0,
                disc_count                INTEGER NOT NULL DEFAULT 0,

                composers                 TEXT,
                conductor                 TEXT,
                remixed_by                TEXT,

                genre                     TEXT,
                beats_per_minute          INTEGER NOT NULL DEFAULT 0,
                initial_key               TEXT,
                grouping                  TEXT,
                publisher                 TEXT,
                isrc                      TEXT,

                comment                   TEXT,
                description               TEXT,
                copyright                 TEXT,
                lyrics                    TEXT,

                duration_ticks            INTEGER NOT NULL DEFAULT 0,
                bitrate                   INTEGER NOT NULL DEFAULT 0,
                sample_rate               INTEGER NOT NULL DEFAULT 0,
                channels                  INTEGER NOT NULL DEFAULT 0,
                bits_per_sample           INTEGER NOT NULL DEFAULT 0,
                codec                     TEXT,

                origin_device_fingerprint TEXT,
                origin_track_id           TEXT,
                origin_file_extension     TEXT,
                origin_album_art_id       TEXT,

                play_count                INTEGER NOT NULL DEFAULT 0,
                imported_play_count       INTEGER NOT NULL DEFAULT 0,
                last_played_at            INTEGER,
                date_added                INTEGER NOT NULL,

                -- Track.SkipCount and Track.ImportedSkipCount; other devices'
                -- skips are in track_remote_skip_counts (SqliteMigrations' step 16).
                skip_count                INTEGER NOT NULL DEFAULT 0,
                imported_skip_count       INTEGER NOT NULL DEFAULT 0,

                -- Both dropped by SqliteMigrations' step 21.
                artist_id                 TEXT    NOT NULL DEFAULT '',
                album_id                  TEXT    NOT NULL DEFAULT '',

                starred                   INTEGER NOT NULL DEFAULT 0,
                -- Dropped by SqliteMigrations' step 13.
                starred_at                INTEGER,

                -- See Track.IsLocallyDownloaded and Track.OriginRelativePath.
                is_locally_downloaded     INTEGER NOT NULL DEFAULT 0,
                origin_relative_path      TEXT,

                -- Sort tags, re-read by every rescan.
                title_sort                TEXT,
                artists_sort              TEXT,
                composers_sort            TEXT,
                encoder_profile           TEXT,

                -- See Track.ArtWidth: re-measured by every rescan.
                art_width                 INTEGER NOT NULL DEFAULT 0,
                art_height                INTEGER NOT NULL DEFAULT 0,
                -- See Track.ArtHash: which picture that is, measured with it.
                art_hash                  TEXT,

                -- Flower's own per-track playback state, carried across
                -- rescans by Library.CarryForwardMutableState.
                remember_playback_position INTEGER NOT NULL DEFAULT 0,
                resume_position_ticks     INTEGER,
                ignore_when_shuffling     INTEGER NOT NULL DEFAULT 0,
                volume_adjustment         INTEGER NOT NULL DEFAULT 0,

                -- What a device remembers about its own files to keep a
                -- server's copies in step with them - see Track.WithdrawnByOrigin,
                -- MovedFromPath and OriginFileStamp.
                withdrawn_by_origin       INTEGER NOT NULL DEFAULT 0,
                moved_from_path           TEXT,
                origin_file_stamp         TEXT,
                tags_edited_at            INTEGER,
                tag_fields_edited         INTEGER NOT NULL DEFAULT 0,
                tag_fields_to_send        INTEGER NOT NULL DEFAULT 0,
                art_edited_at             INTEGER,
                file_replaced_at          INTEGER
            );

            -- All five dropped by SqliteMigrations' step 21: the library is
            -- browsed, grouped and sorted in memory (LibrarySnapshot).
            CREATE INDEX ix_tracks_path ON tracks (path);
            CREATE INDEX ix_tracks_date_added ON tracks (date_added);
            CREATE INDEX ix_tracks_album ON tracks (album);
            CREATE INDEX ix_tracks_artist_id ON tracks (artist_id);
            CREATE INDEX ix_tracks_album_id  ON tracks (album_id);

            -- Track.RemotePlayCounts: the latest play count each OTHER device
            -- has reported for a track, keyed by DeviceIdentity.Fingerprint.
            -- A real child table rather than the serialized dictionary the JSON
            -- store used, so a single peer's report is one row to upsert and
            -- the per-key "merge by max" rule can be expressed in SQL.
            CREATE TABLE track_remote_play_counts (
                track_id          TEXT    NOT NULL,
                device_fingerprint TEXT   NOT NULL,
                play_count        INTEGER NOT NULL,

                PRIMARY KEY (track_id, device_fingerprint),
                FOREIGN KEY (track_id) REFERENCES tracks (id) ON DELETE CASCADE
            );

            CREATE TABLE playlists (
                id         TEXT    NOT NULL PRIMARY KEY,
                name       TEXT    NOT NULL,
                updated_at INTEGER NOT NULL,

                -- Subsonic playlist attributes. The client's Playlist model has
                -- no equivalent, so its own writes leave these alone (the
                -- upsert in PlaylistRepository names only the columns it owns)
                -- rather than resetting a comment or a public flag set through
                -- the server.
                comment    TEXT,
                is_public  INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL DEFAULT 0,

                -- A smart playlist's rules, as SmartPlaylistRulesJson. NULL is
                -- an ordinary, hand-picked playlist. playlist_tracks holds the
                -- materialized rows for a smart one too, so no reader has to
                -- evaluate anything to answer a request.
                rules      TEXT,

                -- Whose playlist this is, on a server shared by an owner and a
                -- few listeners - see Listeners and Playlist.Listener. A
                -- client's own database leaves it at the default for good.
                listener   TEXT    NOT NULL DEFAULT 'owner',

                -- Folders - see Playlist.IsFolder and ParentId. A folder is a
                -- row here with no playlist_tracks of its own, and parent_id
                -- is deliberately not a foreign key: an id naming a folder
                -- this database no longer has means the top level
                -- (PlaylistTree), not a row to cascade or refuse.
                is_folder  INTEGER NOT NULL DEFAULT 0,
                parent_id  TEXT,

                -- Where it stands among its folder's playlists - see
                -- Playlist.SortKey. NULL is a playlist never placed.
                sort_key   REAL,

                -- A Vibe Playlist's sentence and its No vocals switch - see
                -- Playlist.VibePrompt. NULL is every other kind; like a smart
                -- one, playlist_tracks holds its pool.
                vibe_prompt       TEXT,
                vibe_instrumental INTEGER NOT NULL DEFAULT 0
            );

            -- track_id is deliberately NOT a foreign key into tracks: a rescan
            -- can legitimately drop a track whose file was deleted without that
            -- having to cascade through every playlist referencing it. Resolution is done on load, and an entry
            -- that does not resolve is held in its place (PlaylistEntry).
            CREATE TABLE playlist_tracks (
                playlist_id TEXT    NOT NULL,
                position    INTEGER NOT NULL,
                track_id    TEXT    NOT NULL,

                PRIMARY KEY (playlist_id, position),
                FOREIGN KEY (playlist_id) REFERENCES playlists (id) ON DELETE CASCADE
            );

            -- Files the user removed from the library while keeping them on
            -- disk (see Library.RemoveTracks). A rescan finds every file under
            -- the library folders, so without a record of the ones removed on
            -- purpose the next scan would put them straight back. Keyed by
            -- path, the one thing a scan knows about a file before reading it.
            -- Compared exactly: whether two spellings are one file is the
            -- library folder's question (PathRules), not this table's - see
            -- SqliteMigrations' step 12, which takes NOCASE off a database
            -- created before.
            CREATE TABLE excluded_paths (
                path        TEXT    NOT NULL PRIMARY KEY,
                excluded_at INTEGER NOT NULL
            );

            -- Songs that left the library, kept (see Library's RemovedTrack)
            -- so that one coming back is the song it was - same id, Date
            -- Added, plays and star - rather than a new one. The track itself
            -- is JSON rather than the tracks table's columns over again:
            -- nothing queries a removed track by column, they are loaded once,
            -- matched in memory and written back whole.
            CREATE TABLE removed_tracks (
                id             TEXT    NOT NULL PRIMARY KEY,
                removed_at     INTEGER NOT NULL,
                owed_to_origin INTEGER NOT NULL DEFAULT 0,
                track          TEXT    NOT NULL,

                -- Removed because somebody said so, as against a scan no
                -- longer finding the file. A server tells its devices about
                -- the first kind and not the second (see RemovedTrack).
                deliberate     INTEGER NOT NULL DEFAULT 0
            );
            """;
    }
}
