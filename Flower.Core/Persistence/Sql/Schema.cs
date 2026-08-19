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
        public const string V1 = """
            CREATE TABLE tracks (
                id                        TEXT    NOT NULL PRIMARY KEY,

                path                      TEXT,

                title                     TEXT,
                subtitle                  TEXT,
                artists                   TEXT,
                album_artists             TEXT,
                is_compilation            INTEGER NOT NULL DEFAULT 0,
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
                origin_album_art_hash     TEXT,

                play_count                INTEGER NOT NULL DEFAULT 0,
                imported_play_count       INTEGER NOT NULL DEFAULT 0,
                last_played_at            INTEGER,
                date_added                INTEGER NOT NULL
            );

            -- Not unique: two library entries for one path is not supposed to
            -- happen, but Library.BuildPathIndex already documents tolerating
            -- it (first match wins) rather than throwing, and a UNIQUE
            -- constraint here would turn that into a failed rescan.
            CREATE INDEX ix_tracks_path ON tracks (path);

            -- Recently Added sorts on this, over the whole library, every time
            -- that view is opened.
            CREATE INDEX ix_tracks_date_added ON tracks (date_added);

            -- Album grouping (see TrackListBuilder / AlbumGridBuilder).
            CREATE INDEX ix_tracks_album ON tracks (album);

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
                updated_at INTEGER NOT NULL
            );

            -- track_id is deliberately NOT a foreign key into tracks, matching
            -- the reasoning already recorded on Flower.Server's
            -- PlaylistTrackEntity: a rescan can legitimately drop a track whose
            -- file was deleted without that having to cascade through every
            -- playlist referencing it. Resolution is done on load, and an entry
            -- that no longer resolves is skipped.
            CREATE TABLE playlist_tracks (
                playlist_id TEXT    NOT NULL,
                position    INTEGER NOT NULL,
                track_id    TEXT    NOT NULL,

                PRIMARY KEY (playlist_id, position),
                FOREIGN KEY (playlist_id) REFERENCES playlists (id) ON DELETE CASCADE
            );
            """;
    }
}
