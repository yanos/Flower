using Microsoft.EntityFrameworkCore;

namespace Flower.Server.Data;

public class FlowerDbContext(DbContextOptions<FlowerDbContext> options) : DbContext(options)
{
    public DbSet<TrackEntity> Tracks => Set<TrackEntity>();
    public DbSet<PlaylistEntity> Playlists => Set<PlaylistEntity>();
    public DbSet<PlaylistTrackEntity> PlaylistTracks => Set<PlaylistTrackEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrackEntity>(track =>
        {
            track.HasIndex(t => t.Path).IsUnique();
            track.HasIndex(t => t.ArtistId);
            track.HasIndex(t => t.AlbumId);
        });

        modelBuilder.Entity<PlaylistTrackEntity>(playlistTrack =>
        {
            playlistTrack.HasIndex(pt => new { pt.PlaylistId, pt.Position });
            playlistTrack.HasOne(pt => pt.Playlist)
                .WithMany(p => p.Tracks)
                .HasForeignKey(pt => pt.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
