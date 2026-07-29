using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Server.Configuration;
using Flower.Server.Data;

namespace Flower.Server.Services;

// Flower.Server's scanner reuses the exact same Flower.Core.Importer.Importer
// desktop uses (see SYNC-PLAN.md's "Reuse boundary" note) - only what happens
// to the resulting List<Track> differs: desktop hands them to an in-memory
// Library, this upserts them into SQLite via TrackEntity.
public class LibraryImportService(
    IDbContextFactory<FlowerDbContext> dbFactory,
    IOptions<FlowerServerOptions> options,
    ILogger<Flower.Importer.Importer> importerLogger,
    ILogger<LibraryImportService> logger)
{
    private static readonly Dictionary<string, string> ContentTypesBySuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["alac"] = "audio/mp4",
    };

    public async Task RescanAsync(CancellationToken ct = default)
    {
        var importer = new Flower.Importer.Importer(importerLogger);
        var libraryPaths = options.Value.LibraryPaths;
        var imported = await importer.ImportAsync(libraryPaths);
        logger.LogInformation("Importer found {Count} tracks across {PathCount} configured path(s)", imported.Count, libraryPaths.Count);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existingByPath = await db.Tracks.ToDictionaryAsync(t => t.Path, StringComparer.OrdinalIgnoreCase, ct);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in imported)
        {
            if (track.Path == null)
                continue;

            seenPaths.Add(track.Path);
            ApplyTrack(db, existingByPath, track);
        }

        var removed = existingByPath.Where(kv => !seenPaths.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (removed.Count > 0)
            db.Tracks.RemoveRange(removed);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Library sync complete: {New} new/updated, {Removed} removed", seenPaths.Count, removed.Count);
    }

    private static void ApplyTrack(FlowerDbContext db, Dictionary<string, TrackEntity> existingByPath, Track track)
    {
        var artistName = track.EffectiveAlbumArtist;
        var artistId = SubsonicIdentity.ArtistId(artistName);
        var albumId = SubsonicIdentity.AlbumId(artistName, track.Album);
        var fileInfo = new FileInfo(track.Path!);
        var suffix = Path.GetExtension(track.Path!).TrimStart('.').ToLowerInvariant();

        if (!existingByPath.TryGetValue(track.Path!, out var entity))
        {
            entity = new TrackEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = track.Path!,
                ArtistId = artistId,
                AlbumId = albumId,
                DateAdded = track.DateAdded,
            };
            db.Tracks.Add(entity);
        }
        else
        {
            // Carried forward across rescans - same reasoning as Flower.Core's
            // Library.UpdateTracks matching on Path (see its own remarks).
            entity.ArtistId = artistId;
            entity.AlbumId = albumId;
        }

        entity.Title = track.Title;
        entity.Artist = track.Artists;
        entity.AlbumArtist = artistName;
        entity.Album = track.Album;
        entity.Year = int.TryParse(track.Year, out var year) ? year : null;
        entity.Genre = track.Genre;
        entity.TrackNumber = (int)track.TrackNumber;
        entity.DiscNumber = (int)track.DiscNumber;
        entity.DurationSeconds = track.Duration.TotalSeconds;
        entity.Bitrate = track.Bitrate;
        entity.Size = fileInfo.Exists ? fileInfo.Length : 0;
        entity.Suffix = suffix;
        entity.ContentType = ContentTypesBySuffix.GetValueOrDefault(suffix, "application/octet-stream");
    }
}
