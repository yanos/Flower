namespace Flower.Server.Data;

public class PlaylistEntity
{
    public string Id { get; set; } = "";
    public required string Name { get; set; }
    public string? Comment { get; set; }
    public bool Public { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<PlaylistTrackEntity> Tracks { get; set; } = [];
}
