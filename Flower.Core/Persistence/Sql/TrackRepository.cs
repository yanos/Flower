using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // Reading and writing tracks, shared by the client and Flower.Server - see
    // FlowerDb's remarks.
    public sealed class TrackRepository(FlowerDb db) : ITrackStore, IExcludedPathStore, IRemovedTrackStore
    {
        // Every column in declaration order, reused by both the reader and the
        // upsert so the two cannot drift apart in ordering.
        // Public so Flower.Server can load through the same column list and
        // row mapper instead of keeping a second copy of both.
        private enum Column
        {
            id,
            path,
            title,
            subtitle,
            artists,
            album_artists,
            is_compilation,
            album,
            album_sort,
            year,
            track_number,
            track_count,
            disc_number,
            disc_count,
            composers,
            conductor,
            remixed_by,
            genre,
            beats_per_minute,
            initial_key,
            grouping,
            publisher,
            isrc,
            comment,
            description,
            copyright,
            lyrics,
            duration_ticks,
            bitrate,
            sample_rate,
            channels,
            bits_per_sample,
            codec,
            origin_device_fingerprint,
            origin_track_id,
            origin_file_extension,
            origin_album_art_id,
            play_count,
            imported_play_count,
            last_played_at,
            date_added,
            starred,
            is_locally_downloaded,
            origin_relative_path,
            title_sort,
            artists_sort,
            composers_sort,
            remember_playback_position,
            resume_position_ticks,
            ignore_when_shuffling,
            volume_adjustment,
            encoder_profile,
            withdrawn_by_origin,
            moved_from_path,
            origin_file_stamp,
            tags_edited_at,
            art_edited_at,
            file_replaced_at,
            art_width,
            art_height,
            art_hash,
            skip_count,
            imported_skip_count,
            tag_fields_edited,
            tag_fields_to_send,
        }

        private static readonly string[] ColumnNames = Enum.GetNames<Column>();
        public static readonly string Columns = string.Join(", ", ColumnNames);

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

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT track_id, device_fingerprint, skip_count FROM track_remote_skip_counts;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (byId.TryGetValue(EntityId.FromKey(reader.GetString(0)), out var track))
                        track.RemoteSkipCounts[reader.GetString(1)] = reader.GetInt32(2);
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
            WriteRemoteCounts(connection, transaction, tracks, seen);

            transaction.Commit();
        }

        // The Tier 4.1 payoff: a play-count bump is one UPDATE of one row,
        // rather than re-serializing and rewriting the entire library. See
        // Library.IncrementPlayCount/RecordPlayed, whose TrackChanged
        // event this backs.
        public void UpdateStats(Track track)
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE tracks
                   SET play_count = $play_count,
                       imported_play_count = $imported_play_count,
                       last_played_at = $last_played_at,
                       skip_count = $skip_count,
                       imported_skip_count = $imported_skip_count
                 WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$play_count", track.PlayCount);
            command.Parameters.AddWithValue("$imported_play_count", track.ImportedPlayCount);
            command.Parameters.AddWithValue("$last_played_at", (object?)track.LastPlayedAt?.UtcTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$skip_count", track.SkipCount);
            command.Parameters.AddWithValue("$imported_skip_count", track.ImportedSkipCount);
            command.Parameters.AddWithValue("$id", track.Id.ToKey());
            command.ExecuteNonQuery();
        }

        public void SetStarred(IReadOnlyCollection<Guid> ids, bool starred)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE tracks SET starred = $starred WHERE id = $id;";
            command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
            var idParameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                idParameter.Value = id.ToKey();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // Upsert of a single track, for an in-place mutation that is not a
        // stats bump - a placeholder's Path being set after a download
        // (LibraryDownloadService), or a tag edit in TrackInfoWindow.
        public void Upsert(Track track) => UpsertMany([track]);

        public void UpsertMany(IReadOnlyCollection<Track> tracks)
        {
            if (tracks.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            var written = new HashSet<Guid>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = UpsertSql;
                PrepareUpsertParameters(command);
                foreach (var track in tracks)
                {
                    if (!written.Add(track.Id))
                        continue;

                    BindUpsert(command, track);
                    command.ExecuteNonQuery();
                }
            }

            WriteRemoteCounts(connection, transaction, tracks, written);
            transaction.Commit();
        }

        public void Delete(IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracks WHERE id = $id;";
            var parameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                parameter.Value = id.ToKey();
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public IReadOnlyList<ExcludedPath> LoadExcludedPaths()
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, excluded_at FROM excluded_paths;";
            using var reader = command.ExecuteReader();
            var paths = new List<ExcludedPath>();
            while (reader.Read())
                paths.Add(new ExcludedPath(reader.GetString(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero)));
            return paths;
        }

        public void RemoveExcludedPaths(IReadOnlyCollection<string> paths)
        {
            if (paths.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // COLLATE NOCASE on the column makes this match however the path
            // was cased when it was excluded.
            command.CommandText = "DELETE FROM excluded_paths WHERE path = $path;";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var value in paths)
            {
                path.Value = value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void AddExcludedPaths(IReadOnlyCollection<string> paths)
        {
            if (paths.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO excluded_paths (path, excluded_at) VALUES ($path, $at)
                ON CONFLICT (path) DO UPDATE SET excluded_at = excluded.excluded_at;
                """;
            var path = command.Parameters.Add("$path", SqliteType.Text);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcTicks);
            foreach (var value in paths)
            {
                path.Value = value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public IReadOnlyList<RemovedTrack> LoadRemovedTracks()
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT removed_at, owed_to_origin, track, deliberate FROM removed_tracks;";
            using var reader = command.ExecuteReader();
            var removed = new List<RemovedTrack>();
            while (reader.Read())
            {
                // A row that no longer parses is skipped rather than allowed
                // to fail the load: the worst it costs is one song coming back
                // as new.
                Track? track;
                try
                {
                    track = JsonSerializer.Deserialize(reader.GetString(2), RemovedTrackJsonContext.Default.Track);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (track != null)
                {
                    removed.Add(new RemovedTrack(
                        track, new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                        OwedToOrigin: reader.GetInt64(1) != 0, Deliberate: reader.GetInt64(3) != 0));
                }
            }

            return removed;
        }

        public void SaveRemovedTracks(IReadOnlyCollection<RemovedTrack> removed)
        {
            if (removed.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO removed_tracks (id, removed_at, owed_to_origin, track, deliberate)
                VALUES ($id, $removed_at, $owed_to_origin, $track, $deliberate)
                ON CONFLICT (id) DO UPDATE SET
                    removed_at = excluded.removed_at,
                    owed_to_origin = excluded.owed_to_origin,
                    track = excluded.track,
                    deliberate = excluded.deliberate;
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var removedAt = command.Parameters.Add("$removed_at", SqliteType.Integer);
            var owed = command.Parameters.Add("$owed_to_origin", SqliteType.Integer);
            var track = command.Parameters.Add("$track", SqliteType.Text);
            var deliberate = command.Parameters.Add("$deliberate", SqliteType.Integer);
            foreach (var entry in removed)
            {
                deliberate.Value = entry.Deliberate ? 1 : 0;
                id.Value = entry.Track.Id.ToKey();
                removedAt.Value = entry.RemovedAt.UtcTicks;
                owed.Value = entry.OwedToOrigin ? 1 : 0;
                track.Value = JsonSerializer.Serialize(entry.Track, RemovedTrackJsonContext.Default.Track);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void DeleteRemovedTracks(IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM removed_tracks WHERE id = $id;";
            var parameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids)
            {
                parameter.Value = id.ToKey();
                command.ExecuteNonQuery();
            }

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

        // Track.RemotePlayCounts and Track.RemoteSkipCounts, each into its own
        // child table by the same rule.
        private static void WriteRemoteCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IEnumerable<Track> tracks,
            IReadOnlyCollection<Guid> written)
        {
            if (written.Count == 0)
                return;

            WriteCounts(connection, transaction, tracks, "track_remote_play_counts", "play_count", t => t.RemotePlayCounts);
            WriteCounts(connection, transaction, tracks, "track_remote_skip_counts", "skip_count", t => t.RemoteSkipCounts);
        }

        // table and column are one of the two constant pairs above, never
        // anything a caller passed in - SQLite binds values, not identifiers.
        private static void WriteCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IEnumerable<Track> tracks,
            string table,
            string column,
            Func<Track, Dictionary<string, int>> countsOf)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE track_id = $track_id;";
            var deleteId = delete.Parameters.Add("$track_id", SqliteType.Text);

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {table} (track_id, device_fingerprint, {column})
                VALUES ($track_id, $device_fingerprint, $count);
                """;
            var insertId = insert.Parameters.Add("$track_id", SqliteType.Text);
            var insertFingerprint = insert.Parameters.Add("$device_fingerprint", SqliteType.Text);
            var insertCount = insert.Parameters.Add("$count", SqliteType.Integer);

            foreach (var track in tracks)
            {
                var id = track.Id.ToKey();

                // Replaced wholesale per track rather than merged: the in-memory
                // dictionary is already the merged result (Library.MergeRemotePlayCounts
                // applies the per-key max), so the caller's copy is authoritative.
                deleteId.Value = id;
                delete.ExecuteNonQuery();

                foreach (var (fingerprint, count) in countsOf(track))
                {
                    insertId.Value = id;
                    insertFingerprint.Value = fingerprint;
                    insertCount.Value = count;
                    insert.ExecuteNonQuery();
                }
            }
        }

        private static readonly string UpsertSql =
            $"INSERT INTO tracks ({Columns}) VALUES ({string.Join(", ", ColumnNames.Select(c => "$" + c))}) "
            + $"ON CONFLICT (id) DO UPDATE SET {string.Join(", ", ColumnNames.Skip(1).Select(c => $"{c} = excluded.{c}"))};";

        // Parameters are added once and then only have their Value reassigned
        // per row, so the statement is prepared a single time for the whole
        // transaction rather than re-planned 16,000 times.
        private static void PrepareUpsertParameters(SqliteCommand command)
        {
            foreach (var name in ColumnNames)
                command.Parameters.Add("$" + name, SqliteType.Text);

            command.Prepare();
        }

        private static void BindUpsert(SqliteCommand command, Track track)
        {
            var p = command.Parameters;
            p[(int)Column.id].Value = track.Id.ToKey();
            p[(int)Column.path].Value = Nullable(track.Path);
            p[(int)Column.title].Value = Nullable(track.Title);
            p[(int)Column.subtitle].Value = Nullable(track.Subtitle);
            p[(int)Column.artists].Value = Nullable(track.Artists);
            p[(int)Column.album_artists].Value = Nullable(track.AlbumArtists);
            p[(int)Column.is_compilation].Value = track.IsCompilation ? 1 : 0;
            p[(int)Column.album].Value = Nullable(track.Album);
            p[(int)Column.album_sort].Value = Nullable(track.AlbumSort);
            p[(int)Column.year].Value = Nullable(track.Year);
            p[(int)Column.track_number].Value = track.TrackNumber;
            p[(int)Column.track_count].Value = track.TrackCount;
            p[(int)Column.disc_number].Value = track.DiscNumber;
            p[(int)Column.disc_count].Value = track.DiscCount;
            p[(int)Column.composers].Value = Nullable(track.Composers);
            p[(int)Column.conductor].Value = Nullable(track.Conductor);
            p[(int)Column.remixed_by].Value = Nullable(track.RemixedBy);
            p[(int)Column.genre].Value = Nullable(track.Genre);
            p[(int)Column.beats_per_minute].Value = track.BeatsPerMinute;
            p[(int)Column.initial_key].Value = Nullable(track.InitialKey);
            p[(int)Column.grouping].Value = Nullable(track.Grouping);
            p[(int)Column.publisher].Value = Nullable(track.Publisher);
            p[(int)Column.isrc].Value = Nullable(track.ISRC);
            p[(int)Column.comment].Value = Nullable(track.Comment);
            p[(int)Column.description].Value = Nullable(track.Description);
            p[(int)Column.copyright].Value = Nullable(track.Copyright);
            p[(int)Column.lyrics].Value = Nullable(track.Lyrics);
            p[(int)Column.duration_ticks].Value = track.Duration.Ticks;
            p[(int)Column.bitrate].Value = track.Bitrate;
            p[(int)Column.sample_rate].Value = track.SampleRate;
            p[(int)Column.channels].Value = track.Channels;
            p[(int)Column.bits_per_sample].Value = track.BitsPerSample;
            p[(int)Column.codec].Value = Nullable(track.Codec);
            p[(int)Column.encoder_profile].Value = Nullable(track.EncoderProfile);
            p[(int)Column.origin_device_fingerprint].Value = Nullable(track.OriginDeviceFingerprint);
            p[(int)Column.origin_track_id].Value = Nullable(track.OriginTrackId);
            p[(int)Column.origin_file_extension].Value = Nullable(track.OriginFileExtension);
            p[(int)Column.origin_album_art_id].Value = Nullable(track.OriginAlbumArtId);
            p[(int)Column.play_count].Value = track.PlayCount;
            p[(int)Column.imported_play_count].Value = track.ImportedPlayCount;
            p[(int)Column.last_played_at].Value = (object?)track.LastPlayedAt?.UtcTicks ?? DBNull.Value;
            p[(int)Column.date_added].Value = track.DateAdded.UtcTicks;
            p[(int)Column.starred].Value = track.Starred ? 1 : 0;
            p[(int)Column.is_locally_downloaded].Value = track.IsLocallyDownloaded ? 1 : 0;
            p[(int)Column.origin_relative_path].Value = Nullable(track.OriginRelativePath);
            p[(int)Column.title_sort].Value = Nullable(track.TitleSort);
            p[(int)Column.artists_sort].Value = Nullable(track.ArtistsSort);
            p[(int)Column.composers_sort].Value = Nullable(track.ComposersSort);
            p[(int)Column.remember_playback_position].Value = track.RememberPlaybackPosition ? 1 : 0;
            p[(int)Column.resume_position_ticks].Value = (object?)track.ResumePosition?.Ticks ?? DBNull.Value;
            p[(int)Column.ignore_when_shuffling].Value = track.IgnoreWhenShuffling ? 1 : 0;
            p[(int)Column.volume_adjustment].Value = track.VolumeAdjustment;
            p[(int)Column.withdrawn_by_origin].Value = track.WithdrawnByOrigin ? 1 : 0;
            p[(int)Column.moved_from_path].Value = Nullable(track.MovedFromPath);
            p[(int)Column.origin_file_stamp].Value = Nullable(track.OriginFileStamp);
            p[(int)Column.tags_edited_at].Value = (object?)track.TagsEditedAt?.UtcTicks ?? DBNull.Value;
            p[(int)Column.art_edited_at].Value = (object?)track.ArtEditedAt?.UtcTicks ?? DBNull.Value;
            p[(int)Column.file_replaced_at].Value = (object?)track.FileReplacedAt?.UtcTicks ?? DBNull.Value;
            p[(int)Column.art_width].Value = track.ArtWidth;
            p[(int)Column.art_height].Value = track.ArtHeight;
            p[(int)Column.art_hash].Value = Nullable(track.ArtHash);
            p[(int)Column.skip_count].Value = track.SkipCount;
            p[(int)Column.imported_skip_count].Value = track.ImportedSkipCount;
            p[(int)Column.tag_fields_edited].Value = (long)(uint)track.TagFieldsEdited;
            p[(int)Column.tag_fields_to_send].Value = (long)(uint)track.TagFieldsToSend;
        }

        private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

        public static Track ReadTrack(SqliteDataReader reader) => new()
        {
            Id = EntityId.FromKey(reader.GetString((int)Column.id)),
            Path = Text(reader, (int)Column.path),
            Title = Text(reader, (int)Column.title),
            Subtitle = Text(reader, (int)Column.subtitle),
            Artists = Text(reader, (int)Column.artists),
            AlbumArtists = Text(reader, (int)Column.album_artists),
            IsCompilation = reader.GetInt64((int)Column.is_compilation) != 0,
            Album = Text(reader, (int)Column.album),
            AlbumSort = Text(reader, (int)Column.album_sort),
            Year = Text(reader, (int)Column.year),
            TrackNumber = (uint)reader.GetInt64((int)Column.track_number),
            TrackCount = (uint)reader.GetInt64((int)Column.track_count),
            DiscNumber = (uint)reader.GetInt64((int)Column.disc_number),
            DiscCount = (uint)reader.GetInt64((int)Column.disc_count),
            Composers = Text(reader, (int)Column.composers),
            Conductor = Text(reader, (int)Column.conductor),
            RemixedBy = Text(reader, (int)Column.remixed_by),
            Genre = Text(reader, (int)Column.genre),
            BeatsPerMinute = (uint)reader.GetInt64((int)Column.beats_per_minute),
            InitialKey = Text(reader, (int)Column.initial_key),
            Grouping = Text(reader, (int)Column.grouping),
            Publisher = Text(reader, (int)Column.publisher),
            ISRC = Text(reader, (int)Column.isrc),
            Comment = Text(reader, (int)Column.comment),
            Description = Text(reader, (int)Column.description),
            Copyright = Text(reader, (int)Column.copyright),
            Lyrics = Text(reader, (int)Column.lyrics),
            Duration = TimeSpan.FromTicks(reader.GetInt64((int)Column.duration_ticks)),
            Bitrate = (int)reader.GetInt64((int)Column.bitrate),
            SampleRate = (int)reader.GetInt64((int)Column.sample_rate),
            Channels = (int)reader.GetInt64((int)Column.channels),
            BitsPerSample = (int)reader.GetInt64((int)Column.bits_per_sample),
            Codec = Text(reader, (int)Column.codec),
            OriginDeviceFingerprint = Text(reader, (int)Column.origin_device_fingerprint),
            OriginTrackId = Text(reader, (int)Column.origin_track_id),
            OriginFileExtension = Text(reader, (int)Column.origin_file_extension),
            OriginAlbumArtId = Text(reader, (int)Column.origin_album_art_id),
            PlayCount = (int)reader.GetInt64((int)Column.play_count),
            ImportedPlayCount = (int)reader.GetInt64((int)Column.imported_play_count),
            LastPlayedAt = reader.IsDBNull((int)Column.last_played_at) ? null : new DateTimeOffset(reader.GetInt64((int)Column.last_played_at), TimeSpan.Zero),
            DateAdded = new DateTimeOffset(reader.GetInt64((int)Column.date_added), TimeSpan.Zero),
            Starred = reader.GetInt64((int)Column.starred) != 0,
            IsLocallyDownloaded = reader.GetInt64((int)Column.is_locally_downloaded) != 0,
            OriginRelativePath = Text(reader, (int)Column.origin_relative_path),
            TitleSort = Text(reader, (int)Column.title_sort),
            ArtistsSort = Text(reader, (int)Column.artists_sort),
            ComposersSort = Text(reader, (int)Column.composers_sort),
            RememberPlaybackPosition = reader.GetInt64((int)Column.remember_playback_position) != 0,
            ResumePosition = reader.IsDBNull((int)Column.resume_position_ticks) ? null : TimeSpan.FromTicks(reader.GetInt64((int)Column.resume_position_ticks)),
            IgnoreWhenShuffling = reader.GetInt64((int)Column.ignore_when_shuffling) != 0,
            VolumeAdjustment = (int)reader.GetInt64((int)Column.volume_adjustment),
            EncoderProfile = Text(reader, (int)Column.encoder_profile),
            WithdrawnByOrigin = reader.GetInt64((int)Column.withdrawn_by_origin) != 0,
            MovedFromPath = Text(reader, (int)Column.moved_from_path),
            OriginFileStamp = Text(reader, (int)Column.origin_file_stamp),
            TagsEditedAt = reader.IsDBNull((int)Column.tags_edited_at) ? null : new DateTimeOffset(reader.GetInt64((int)Column.tags_edited_at), TimeSpan.Zero),
            ArtEditedAt = reader.IsDBNull((int)Column.art_edited_at) ? null : new DateTimeOffset(reader.GetInt64((int)Column.art_edited_at), TimeSpan.Zero),
            FileReplacedAt = reader.IsDBNull((int)Column.file_replaced_at) ? null : new DateTimeOffset(reader.GetInt64((int)Column.file_replaced_at), TimeSpan.Zero),
            ArtWidth = (int)reader.GetInt64((int)Column.art_width),
            ArtHeight = (int)reader.GetInt64((int)Column.art_height),
            ArtHash = Text(reader, (int)Column.art_hash),
            SkipCount = (int)reader.GetInt64((int)Column.skip_count),
            ImportedSkipCount = (int)reader.GetInt64((int)Column.imported_skip_count),
            TagFieldsEdited = (Flower.Services.TrackTagFieldSet)(uint)reader.GetInt64((int)Column.tag_fields_edited),
            TagFieldsToSend = (Flower.Services.TrackTagFieldSet)(uint)reader.GetInt64((int)Column.tag_fields_to_send),
        };

        private static string? Text(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // Source-generated, because this runs on a phone (no reflection under AOT)
    // as well as on the server. Its own context rather than a line in
    // FlowerCoreJsonContext, which writes indented: these are rows in a table,
    // not files anyone opens.
    [JsonSerializable(typeof(Track))]
    internal partial class RemovedTrackJsonContext : JsonSerializerContext
    {
    }
}
