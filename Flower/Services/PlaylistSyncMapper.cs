using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Conversions between Flower.Models.Playlist and the sync wire DTOs, shared by
// SyncHttpServer (handling GET/POST) and PlaylistSyncService (driving a sync
// session as the initiator).
public static class PlaylistSyncMapper
{
    public static PlaylistSyncPlaylistDto ToDto(Playlist playlist) =>
        new(playlist.Id, playlist.Name, playlist.UpdatedAt, playlist.Tracks.Select(ToDto).ToList());

    public static PlaylistSyncTrackDto ToDto(Track track) =>
        new(track.Title, track.Artists, track.Album, (int)track.Duration.TotalSeconds);

    public static PlaylistSyncManifestDto ToManifest(string deviceFingerprint, IEnumerable<Playlist> playlists) =>
        new(deviceFingerprint, playlists.Select(ToDto).ToList());

    // Matches each wire track against the local library by SyncKey (see
    // Track.BuildSyncKey). Tracks the peer has that this device doesn't are
    // silently dropped from the resulting playlist - actual file transfer is a
    // later phase (see SYNC-PLAN.md), so a synced playlist can only ever reference
    // tracks already present on both sides.
    public static List<Track> ResolveTracks(IEnumerable<PlaylistSyncTrackDto> tracks, IReadOnlyList<Track> localLibrary)
    {
        var byKey = localLibrary
            .GroupBy(t => t.SyncKey)
            .ToDictionary(g => g.Key, g => g.First());

        return tracks
            .Select(dto => Track.BuildSyncKey(dto.Title, dto.Artists, dto.Album, dto.DurationSeconds))
            .Where(byKey.ContainsKey)
            .Select(key => byKey[key])
            .ToList();
    }

    public static Playlist ToPlaylist(PlaylistSyncPlaylistDto dto, IReadOnlyList<Track> localLibrary) =>
        new(dto.Id, dto.Name, ResolveTracks(dto.Tracks, localLibrary), dto.UpdatedAt);
}
