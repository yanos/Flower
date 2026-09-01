using System;
using System.Collections.Generic;
using System.Data;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // Reading and writing tracks, shared by the client and Flower.Server - see
    // FlowerDb's remarks.
    public sealed class TrackRepository(FlowerDb db) : ITrackStore
    {
        // Every column in declaration order, reused by both the reader and the
        // upsert so the two cannot drift apart in ordering.
        // Public so Flower.Server can load through the same column list and
        // row mapper instead of keeping a second copy of both.
        public const string Columns = """
            id, path, title, subtitle, artists, album_artists, is_compilation,
            album, album_sort, year, track_number, track_count, disc_number, disc_count,
            composers, conductor, remixed_by,
            genre, beats_per_minute, initial_key, grouping, publisher, isrc,
            comment, description, copyright, lyrics,
            duration_ticks, bitrate, sample_rate, channels, bits_per_sample, codec,
            origin_device_fingerprint, origin_track_id, origin_file_extension, origin_album_art_hash,
            play_count, imported_play_count, last_played_at, date_added,
            album_artist, artist_id, album_id, starred, starred_at,
            is_locally_downloaded, origin_relative_path,
            title_sort, artists_sort, composers_sort,
            remember_playback_position, resume_position_ticks, ignore_when_shuffling, volume_adjustment,
            encoder_profile
            """;

        public List<Track> LoadAll()
        {
            using var connection = db.Open();
            return LoadAll(connection);
        }

        public static List<Track> LoadAll(SqliteConnection connection)
        {
            var tracks = new List<Track>();
            var byId = new Dictionary<Guid, Track>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT {Columns} FROM tracks;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var track = ReadTrack(reader);
                    tracks.Add(track);
                    byId[track.Id] = track;
                }
            }

            // Second pass rather than a JOIN: a join would repeat every one of
            // a track's ~40 columns per remote-play-count row, and most tracks
            // have none at all.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT track_id, device_fingerprint, play_count FROM track_remote_play_counts;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (byId.TryGetValue(EntityId.FromKey(reader.GetString(0)), out var track))
                        track.RemotePlayCounts[reader.GetString(1)] = reader.GetInt32(2);
                }
            }

            return tracks;
        }

        // The whole-library write, for a rescan or a sync merge: upserts every
        // track given and deletes any row no longer present. One transaction,
        // one prepared statement reused across rows - which is what makes this
        // affordable at the 16k-track scale the JSON store was rewriting in
        // full (~18 MB) on every save.
        public void ReplaceAll(IEnumerable<Track> tracks)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            var seen = new HashSet<Guid>();

            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = UpsertSql;
                PrepareUpsertParameters(upsert);

                foreach (var track in tracks)
                {
                    if (!seen.Add(track.Id))
                        continue;

                    BindUpsert(upsert, track);
                    upsert.ExecuteNonQuery();
                }
            }

            DeleteTracksNotIn(connection, transaction, seen);
            WriteRemotePlayCounts(connection, transaction, tracks, seen);

            transaction.Commit();
        }

        // The Tier 4.1 payoff: a play-count bump is one UPDATE of one row,
        // rather than re-serializing and rewriting the entire library. See
        // Library.IncrementPlayCount/RecordPlayed, whose TrackStatsChanged
        // event this backs.
        public void UpdateStats(Track track)
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE tracks
                   SET play_count = $play_count,
                       imported_play_count = $imported_play_count,
                       last_played_at = $last_played_at
                 WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$play_count", track.PlayCount);
            command.Parameters.AddWithValue("$imported_play_count", track.ImportedPlayCount);
            command.Parameters.AddWithValue("$last_played_at", (object?)track.LastPlayedAt?.UtcTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", track.Id.ToKey());
            command.ExecuteNonQuery();
        }

        // Star or unstar every track matching one Subsonic id - a song, or
        // every track on an album or by an album artist. One indexed UPDATE
        // rather than a loop of single-row writes, which is why it lives here
        // and not in UpdateStats' shape.
        //
        // The caller is expected to have already applied the same change to
        // the in-memory Track objects; this is the durability half, reached
        // through ITrackStore from Library.SetStarred.
        public void SetStarred(StarTarget target, string value, bool starred, DateTimeOffset? starredAt)
        {
            // The column is chosen from the enum, never interpolated from user
            // input - SQLite cannot bind an identifier. All three are indexed.
            var column = target switch
            {
                StarTarget.Song => "id",
                StarTarget.Album => "album_id",
                _ => "artist_id",
            };

            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE tracks SET starred = $starred, starred_at = $starred_at WHERE {column} = $value;";
            command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
            command.Parameters.AddWithValue("$starred_at", (object?)starredAt?.UtcTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        // Upsert of a single track, for an in-place mutation that is not a
        // stats bump - a placeholder's Path being set after a download
        // (LibraryDownloadService), or a tag edit in TrackInfoWindow.
        public void Upsert(Track track)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = UpsertSql;
                PrepareUpsertParameters(command);
                BindUpsert(command, track);
                command.ExecuteNonQuery();
            }

            WriteRemotePlayCounts(connection, transaction, [track], [track.Id]);
            transaction.Commit();
        }

        private static void DeleteTracksNotIn(SqliteConnection connection, SqliteTransaction transaction, HashSet<Guid> keep)
        {
            // Collected first, then deleted by id: SQLite has no way to bind a
            // set, and building an IN clause with 16k literals would be both
            // enormous and past SQLITE_MAX_VARIABLE_NUMBER.
            var doomed = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id FROM tracks;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!keep.Contains(EntityId.FromKey(id)))
                        doomed.Add(id);
                }
            }

            if (doomed.Count == 0)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM tracks WHERE id = $id;";
                var parameter = command.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in doomed)
                {
                    parameter.Value = id;
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void WriteRemotePlayCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IEnumerable<Track> tracks,
            IReadOnlyCollection<Guid> written)
        {
            if (written.Count == 0)
                return;

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM track_remote_play_counts WHERE track_id = $track_id;";
            var deleteId = delete.Parameters.Add("$track_id", SqliteType.Text);

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO track_remote_play_counts (track_id, device_fingerprint, play_count)
                VALUES ($track_id, $device_fingerprint, $play_count);
                """;
            var insertId = insert.Parameters.Add("$track_id", SqliteType.Text);
            var insertFingerprint = insert.Parameters.Add("$device_fingerprint", SqliteType.Text);
            var insertCount = insert.Parameters.Add("$play_count", SqliteType.Integer);

            foreach (var track in tracks)
            {
                var id = track.Id.ToKey();

                // Replaced wholesale per track rather than merged: the in-memory
                // dictionary is already the merged result (Library.MergeRemotePlayCounts
                // applies the per-key max), so the caller's copy is authoritative.
                deleteId.Value = id;
                delete.ExecuteNonQuery();

                foreach (var (fingerprint, count) in track.RemotePlayCounts)
                {
                    insertId.Value = id;
                    insertFingerprint.Value = fingerprint;
                    insertCount.Value = count;
                    insert.ExecuteNonQuery();
                }
            }
        }

        private const string UpsertSql = """
            INSERT INTO tracks (
                id, path, title, subtitle, artists, album_artists, is_compilation,
                album, album_sort, year, track_number, track_count, disc_number, disc_count,
                composers, conductor, remixed_by,
                genre, beats_per_minute, initial_key, grouping, publisher, isrc,
                comment, description, copyright, lyrics,
                duration_ticks, bitrate, sample_rate, channels, bits_per_sample, codec,
                origin_device_fingerprint, origin_track_id, origin_file_extension, origin_album_art_hash,
                play_count, imported_play_count, last_played_at, date_added,
                album_artist, artist_id, album_id, starred, starred_at,
                is_locally_downloaded, origin_relative_path,
                title_sort, artists_sort, composers_sort,
                remember_playback_position, resume_position_ticks, ignore_when_shuffling, volume_adjustment,
                encoder_profile
            ) VALUES (
                $id, $path, $title, $subtitle, $artists, $album_artists, $is_compilation,
                $album, $album_sort, $year, $track_number, $track_count, $disc_number, $disc_count,
                $composers, $conductor, $remixed_by,
                $genre, $beats_per_minute, $initial_key, $grouping, $publisher, $isrc,
                $comment, $description, $copyright, $lyrics,
                $duration_ticks, $bitrate, $sample_rate, $channels, $bits_per_sample, $codec,
                $origin_device_fingerprint, $origin_track_id, $origin_file_extension, $origin_album_art_hash,
                $play_count, $imported_play_count, $last_played_at, $date_added,
                $album_artist, $artist_id, $album_id, $starred, $starred_at,
                $is_locally_downloaded, $origin_relative_path,
                $title_sort, $artists_sort, $composers_sort,
                $remember_playback_position, $resume_position_ticks, $ignore_when_shuffling, $volume_adjustment,
                $encoder_profile
            )
            ON CONFLICT (id) DO UPDATE SET
                path = excluded.path,
                title = excluded.title,
                subtitle = excluded.subtitle,
                artists = excluded.artists,
                album_artists = excluded.album_artists,
                is_compilation = excluded.is_compilation,
                album = excluded.album,
                album_sort = excluded.album_sort,
                year = excluded.year,
                track_number = excluded.track_number,
                track_count = excluded.track_count,
                disc_number = excluded.disc_number,
                disc_count = excluded.disc_count,
                composers = excluded.composers,
                conductor = excluded.conductor,
                remixed_by = excluded.remixed_by,
                genre = excluded.genre,
                beats_per_minute = excluded.beats_per_minute,
                initial_key = excluded.initial_key,
                grouping = excluded.grouping,
                publisher = excluded.publisher,
                isrc = excluded.isrc,
                comment = excluded.comment,
                description = excluded.description,
                copyright = excluded.copyright,
                lyrics = excluded.lyrics,
                duration_ticks = excluded.duration_ticks,
                bitrate = excluded.bitrate,
                sample_rate = excluded.sample_rate,
                channels = excluded.channels,
                bits_per_sample = excluded.bits_per_sample,
                codec = excluded.codec,
                origin_device_fingerprint = excluded.origin_device_fingerprint,
                origin_track_id = excluded.origin_track_id,
                origin_file_extension = excluded.origin_file_extension,
                origin_album_art_hash = excluded.origin_album_art_hash,
                play_count = excluded.play_count,
                imported_play_count = excluded.imported_play_count,
                last_played_at = excluded.last_played_at,
                date_added = excluded.date_added,
                album_artist = excluded.album_artist,
                artist_id = excluded.artist_id,
                album_id = excluded.album_id,
                starred = excluded.starred,
                starred_at = excluded.starred_at,
                is_locally_downloaded = excluded.is_locally_downloaded,
                origin_relative_path = excluded.origin_relative_path,
                title_sort = excluded.title_sort,
                artists_sort = excluded.artists_sort,
                composers_sort = excluded.composers_sort,
                remember_playback_position = excluded.remember_playback_position,
                resume_position_ticks = excluded.resume_position_ticks,
                ignore_when_shuffling = excluded.ignore_when_shuffling,
                volume_adjustment = excluded.volume_adjustment,
                encoder_profile = excluded.encoder_profile;
            """;

        private static readonly string[] UpsertParameterNames =
        [
            "$id", "$path", "$title", "$subtitle", "$artists", "$album_artists", "$is_compilation",
            "$album", "$album_sort", "$year", "$track_number", "$track_count", "$disc_number", "$disc_count",
            "$composers", "$conductor", "$remixed_by",
            "$genre", "$beats_per_minute", "$initial_key", "$grouping", "$publisher", "$isrc",
            "$comment", "$description", "$copyright", "$lyrics",
            "$duration_ticks", "$bitrate", "$sample_rate", "$channels", "$bits_per_sample", "$codec",
            "$origin_device_fingerprint", "$origin_track_id", "$origin_file_extension", "$origin_album_art_hash",
            "$play_count", "$imported_play_count", "$last_played_at", "$date_added",
            "$album_artist", "$artist_id", "$album_id", "$starred", "$starred_at",
            "$is_locally_downloaded", "$origin_relative_path",
            "$title_sort", "$artists_sort", "$composers_sort",
            "$remember_playback_position", "$resume_position_ticks", "$ignore_when_shuffling", "$volume_adjustment",
            "$encoder_profile",
        ];

        // Parameters are added once and then only have their Value reassigned
        // per row, so the statement is prepared a single time for the whole
        // transaction rather than re-planned 16,000 times.
        private static void PrepareUpsertParameters(SqliteCommand command)
        {
            foreach (var name in UpsertParameterNames)
                command.Parameters.Add(name, SqliteType.Text);

            command.Prepare();
        }

        private static void BindUpsert(SqliteCommand command, Track track)
        {
            var p = command.Parameters;
            p["$id"].Value = track.Id.ToKey();
            p["$path"].Value = Nullable(track.Path);
            p["$title"].Value = Nullable(track.Title);
            p["$subtitle"].Value = Nullable(track.Subtitle);
            p["$artists"].Value = Nullable(track.Artists);
            p["$album_artists"].Value = Nullable(track.AlbumArtists);
            p["$is_compilation"].Value = track.IsCompilation ? 1 : 0;
            p["$album"].Value = Nullable(track.Album);
            p["$album_sort"].Value = Nullable(track.AlbumSort);
            p["$year"].Value = Nullable(track.Year);
            p["$track_number"].Value = track.TrackNumber;
            p["$track_count"].Value = track.TrackCount;
            p["$disc_number"].Value = track.DiscNumber;
            p["$disc_count"].Value = track.DiscCount;
            p["$composers"].Value = Nullable(track.Composers);
            p["$conductor"].Value = Nullable(track.Conductor);
            p["$remixed_by"].Value = Nullable(track.RemixedBy);
            p["$genre"].Value = Nullable(track.Genre);
            p["$beats_per_minute"].Value = track.BeatsPerMinute;
            p["$initial_key"].Value = Nullable(track.InitialKey);
            p["$grouping"].Value = Nullable(track.Grouping);
            p["$publisher"].Value = Nullable(track.Publisher);
            p["$isrc"].Value = Nullable(track.ISRC);
            p["$comment"].Value = Nullable(track.Comment);
            p["$description"].Value = Nullable(track.Description);
            p["$copyright"].Value = Nullable(track.Copyright);
            p["$lyrics"].Value = Nullable(track.Lyrics);
            p["$duration_ticks"].Value = track.Duration.Ticks;
            p["$bitrate"].Value = track.Bitrate;
            p["$sample_rate"].Value = track.SampleRate;
            p["$channels"].Value = track.Channels;
            p["$bits_per_sample"].Value = track.BitsPerSample;
            p["$codec"].Value = Nullable(track.Codec);
            p["$encoder_profile"].Value = Nullable(track.EncoderProfile);
            p["$origin_device_fingerprint"].Value = Nullable(track.OriginDeviceFingerprint);
            p["$origin_track_id"].Value = Nullable(track.OriginTrackId);
            p["$origin_file_extension"].Value = Nullable(track.OriginFileExtension);
            p["$origin_album_art_hash"].Value = Nullable(track.OriginAlbumArtHash);
            p["$play_count"].Value = track.PlayCount;
            p["$imported_play_count"].Value = track.ImportedPlayCount;
            p["$last_played_at"].Value = (object?)track.LastPlayedAt?.UtcTicks ?? DBNull.Value;
            p["$date_added"].Value = track.DateAdded.UtcTicks;
            // Derived on write, never read back into a Track: these exist so
            // Flower.Server can filter and index on them (see Schema.V1).
            // Recomputing here rather than storing whatever a caller passed
            // means they cannot go stale against a retagged album.
            var albumArtist = track.EffectiveAlbumArtist;
            p["$album_artist"].Value = albumArtist;
            p["$artist_id"].Value = SubsonicIdentity.ArtistId(albumArtist);
            p["$album_id"].Value = SubsonicIdentity.AlbumId(albumArtist, track.Album);
            p["$starred"].Value = track.Starred ? 1 : 0;
            p["$starred_at"].Value = (object?)track.StarredAt?.UtcTicks ?? DBNull.Value;
            p["$is_locally_downloaded"].Value = track.IsLocallyDownloaded ? 1 : 0;
            p["$origin_relative_path"].Value = Nullable(track.OriginRelativePath);
            p["$title_sort"].Value = Nullable(track.TitleSort);
            p["$artists_sort"].Value = Nullable(track.ArtistsSort);
            p["$composers_sort"].Value = Nullable(track.ComposersSort);
            p["$remember_playback_position"].Value = track.RememberPlaybackPosition ? 1 : 0;
            p["$resume_position_ticks"].Value = (object?)track.ResumePosition?.Ticks ?? DBNull.Value;
            p["$ignore_when_shuffling"].Value = track.IgnoreWhenShuffling ? 1 : 0;
            p["$volume_adjustment"].Value = track.VolumeAdjustment;
        }

        private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

        public static Track ReadTrack(SqliteDataReader reader) => new()
        {
            Id = EntityId.FromKey(reader.GetString(0)),
            Path = Text(reader, 1),
            Title = Text(reader, 2),
            Subtitle = Text(reader, 3),
            Artists = Text(reader, 4),
            AlbumArtists = Text(reader, 5),
            IsCompilation = reader.GetInt64(6) != 0,
            Album = Text(reader, 7),
            AlbumSort = Text(reader, 8),
            Year = Text(reader, 9),
            TrackNumber = (uint)reader.GetInt64(10),
            TrackCount = (uint)reader.GetInt64(11),
            DiscNumber = (uint)reader.GetInt64(12),
            DiscCount = (uint)reader.GetInt64(13),
            Composers = Text(reader, 14),
            Conductor = Text(reader, 15),
            RemixedBy = Text(reader, 16),
            Genre = Text(reader, 17),
            BeatsPerMinute = (uint)reader.GetInt64(18),
            InitialKey = Text(reader, 19),
            Grouping = Text(reader, 20),
            Publisher = Text(reader, 21),
            ISRC = Text(reader, 22),
            Comment = Text(reader, 23),
            Description = Text(reader, 24),
            Copyright = Text(reader, 25),
            Lyrics = Text(reader, 26),
            Duration = TimeSpan.FromTicks(reader.GetInt64(27)),
            Bitrate = (int)reader.GetInt64(28),
            SampleRate = (int)reader.GetInt64(29),
            Channels = (int)reader.GetInt64(30),
            BitsPerSample = (int)reader.GetInt64(31),
            Codec = Text(reader, 32),
            OriginDeviceFingerprint = Text(reader, 33),
            OriginTrackId = Text(reader, 34),
            OriginFileExtension = Text(reader, 35),
            OriginAlbumArtHash = Text(reader, 36),
            PlayCount = (int)reader.GetInt64(37),
            ImportedPlayCount = (int)reader.GetInt64(38),
            LastPlayedAt = reader.IsDBNull(39) ? null : new DateTimeOffset(reader.GetInt64(39), TimeSpan.Zero),
            DateAdded = new DateTimeOffset(reader.GetInt64(40), TimeSpan.Zero),
            // 41 (album_artist), 42 (artist_id) and 43 (album_id) are
            // deliberately not read back: all three are derived from the tag
            // columns on write (Track.EffectiveAlbumArtist and SubsonicIdentity)
            // and exist only so the server can group, index and filter on them.
            // See Schema.V1.
            Starred = reader.GetInt64(44) != 0,
            StarredAt = reader.IsDBNull(45) ? null : new DateTimeOffset(reader.GetInt64(45), TimeSpan.Zero),
            IsLocallyDownloaded = reader.GetInt64(46) != 0,
            OriginRelativePath = Text(reader, 47),
            TitleSort = Text(reader, 48),
            ArtistsSort = Text(reader, 49),
            ComposersSort = Text(reader, 50),
            RememberPlaybackPosition = reader.GetInt64(51) != 0,
            ResumePosition = reader.IsDBNull(52) ? null : TimeSpan.FromTicks(reader.GetInt64(52)),
            IgnoreWhenShuffling = reader.GetInt64(53) != 0,
            VolumeAdjustment = (int)reader.GetInt64(54),
            EncoderProfile = Text(reader, 55),
        };

        private static string? Text(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
