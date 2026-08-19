namespace Flower.Server.Data;

// Server-internal row, one per imported file.
//
// This used to be justified as a deliberate seam keeping SQLite/EF Core
// concerns out of the shared client-side model. That held while the client
// was JSON; it no longer does. The client moved to raw SQLite in
// ARCHITECTURE-REVIEW.md Tier 4.1, sharing one schema and one set of
// repositories (Flower.Core/Persistence/Sql/), and this class is what still
// has to be ported onto them - at which point it goes away rather than being
// preserved. Do not add fields here that belong in the shared schema.
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
