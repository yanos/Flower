namespace Flower.Server.Data;

// Server-internal row, one per imported file - deliberately not Flower.Core's
// own Track record (see SYNC-PLAN.md's "Reuse boundary" note): this is the
// seam that keeps SQLite/EF Core concerns (a stable row Id, indexed Path,
// denormalized ArtistId/AlbumId) out of the shared client-side model.
//
// ArtistId/AlbumId are deterministic hashes of the normalized artist/album
// name (see SubsonicIdentity), not real foreign keys into separate
// Artist/Album tables - there's nothing about an artist or album beyond a
// name to persist, so a real reconciled table would only exist to hand out
// stable ids, which a deterministic hash already does for free without an
// upsert-matching step on every rescan.
public class TrackEntity
{
    public string Id { get; set; } = "";

    public required string Path { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }

    public required string ArtistId { get; set; }
    public required string AlbumId { get; set; }

    public int? Year { get; set; }
    public string? Genre { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }

    public double DurationSeconds { get; set; }
    public int Bitrate { get; set; }
    public long Size { get; set; }
    public string? Suffix { get; set; }
    public string? ContentType { get; set; }

    public DateTimeOffset DateAdded { get; set; }
    public int PlayCount { get; set; }
    public bool Starred { get; set; }
    public DateTimeOffset? StarredAt { get; set; }
}
