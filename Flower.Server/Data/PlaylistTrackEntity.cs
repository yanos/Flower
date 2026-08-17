namespace Flower.Server.Data;

// TrackId is a plain string, not an EF navigation/FK to TrackEntity - a
// rescan can legitimately drop a row (file deleted on disk) without needing
// to cascade through every playlist that referenced it; SubsonicMapper skips
// any entry whose TrackId no longer resolves rather than the DB enforcing it.
public class PlaylistTrackEntity
{
    public int Id { get; set; }
    public required string PlaylistId { get; set; }
    public required string TrackId { get; set; }
    public int Position { get; set; }

    public PlaylistEntity? Playlist { get; set; }
}
