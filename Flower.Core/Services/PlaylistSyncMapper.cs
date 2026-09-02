using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Conversions between Flower.Models.Playlist and the sync wire DTOs, shared by
// Flower.Server's SyncEndpoints (which answers GET /playlists and
// POST /playlists/apply) and PlaylistSyncService (which drives a sync session
// from the client side).
public static class PlaylistSyncMapper
{
    public static PlaylistSyncPlaylistDto ToDto(Playlist playlist) =>
        new(playlist.Id, playlist.Name, playlist.UpdatedAt, playlist.Tracks.Select(ToDto).ToList(), playlist.Rules);

    // Track.RoundedSeconds, not a separate inline Math.Round - must agree with
    // Track.SyncKey's own rounding (see its doc comment) or a duration near a
    // whole-second boundary can match locally but silently fail to match once
    // round-tripped through this DTO to a peer.
    public static PlaylistSyncTrackDto ToDto(Track track) =>
        new(track.Title, track.Artists, track.Album, Track.RoundedSeconds(track.Duration));

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

    // A smart playlist arrives carrying both its rules and whatever the peer
    // had materialized from them. Both are kept: the tracks so the playlist is
    // not empty for the moment before it is evaluated here (and permanently,
    // for a LiveUpdating = false one, which no recomputation pass will ever
    // touch), the rules so this device evaluates them against its own library
    // from then on. Nothing has to schedule that evaluation - installing the
    // merged set raises Library.PlaylistsChanged, which SmartPlaylistRefresher
    // is already subscribed to.
    public static Playlist ToPlaylist(PlaylistSyncPlaylistDto dto, IReadOnlyList<Track> localLibrary) =>
        new(dto.Id, dto.Name, ResolveTracks(dto.Tracks, localLibrary), dto.UpdatedAt, rules: dto.Rules);
}
