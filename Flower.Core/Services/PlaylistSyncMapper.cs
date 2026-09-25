using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

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
    //
    // The id is the origin's: OriginTrackId on a device that learned the song
    // from a server, the track's own on the server that has the file.
    public static PlaylistSyncTrackDto ToDto(Track track) =>
        new(track.Title, track.Artists, track.Album, Track.RoundedSeconds(track.Duration),
            track.OriginTrackId ?? track.Id.ToKey());

    public static PlaylistSyncManifestDto ToManifest(
        string deviceFingerprint, IEnumerable<Playlist> playlists, IEnumerable<Guid>? deleted = null) =>
        new(deviceFingerprint, playlists.Select(ToDto).ToList(), deleted?.ToList() is { Count: > 0 } ids ? ids : null);

    // Matches each wire track against the local library - by its origin id
    // first, then by SyncKey (see Track.BuildSyncKey). Tracks the peer has
    // that this device doesn't are dropped from the resulting playlist: a
    // playlist can only hold tracks the library has.
    public static List<Track> ResolveTracks(
        IEnumerable<PlaylistSyncTrackDto> tracks, IReadOnlyList<Track> localLibrary, out List<PlaylistSyncTrackDto> unmatched) =>
        new PlaylistTrackIndex(localLibrary).Resolve(tracks, out unmatched);

    // A smart playlist arrives carrying both its rules and whatever the peer
    // had materialized from them. Both are kept: the tracks so the playlist is
    // not empty for the moment before it is evaluated here (and permanently,
    // for a LiveUpdating = false one, which no recomputation pass will ever
    // touch), the rules so this device evaluates them against its own library
    // from then on. Nothing has to schedule that evaluation - installing the
    // merged set raises Library.PlaylistsChanged, which SmartPlaylistRefresher
    // is already subscribed to.
    //
    // The drop is logged when a logger is given, because nothing else records
    // it: the playlist keeps its UpdatedAt, so a shorter copy looks like the
    // same version everywhere, and "why does the server's playlist have fewer
    // songs" used to take reading both databases to answer.
    public static Playlist ToPlaylist(PlaylistSyncPlaylistDto dto, IReadOnlyList<Track> localLibrary, ILogger? logger = null) =>
        ToPlaylist(dto, new PlaylistTrackIndex(localLibrary), logger);

    public static Playlist ToPlaylist(PlaylistSyncPlaylistDto dto, PlaylistTrackIndex index, ILogger? logger = null)
    {
        var tracks = index.Resolve(dto.Tracks, out var unmatched);
        if (unmatched.Count > 0)
        {
            logger?.LogInformation(
                "Playlist {Name} ({PlaylistId}): {Dropped} of {Total} track(s) are not in this library and were dropped: {Tracks}",
                dto.Name, dto.Id, unmatched.Count, dto.Tracks.Count,
                string.Join("; ", unmatched.Select(t => $"{t.Artists} - {t.Title} [{t.Album}, {t.DurationSeconds}s]")));
        }

        return new(dto.Id, dto.Name, tracks, dto.UpdatedAt, rules: dto.Rules);
    }

    // What a server does with a manifest a device pushed to /playlists/apply,
    // here rather than in SyncEndpoints so a test can stand a client up
    // against the server's own rule instead of a copy of it. The initiator
    // resolved every conflict before pushing (see PlaylistSyncService), so no
    // second merge runs here - but neither is the push taken wholesale, which
    // is what this used to do, because a pushed set is only as good as what
    // the pushing device could see:
    //
    //   - A playlist the push leaves out is kept unless Deleted names it.
    //     Wholesale replacement read "not in the push" as "deleted", so a
    //     playlist another device added between this device's read and its
    //     write was deleted by a push that had never heard of it.
    //   - A pushed copy no newer than the one held is not taken. The pusher
    //     did not edit it, so any difference is loss, not change: its copy
    //     is what survived matching against a library that lacks some of the
    //     songs - a phone whose first playlist sync ran before its first
    //     catalog pull resolved nothing and pushed every playlist back empty.
    //     The one exception is the rules of a smart playlist at an equal
    //     timestamp, which the planner hands to whichever side has them for
    //     the same reason (see PlaylistSyncPlanner).
    //
    // Returns the set installed.
    public static List<Playlist> ApplyPushedManifest(Library library, PlaylistSyncManifestDto manifest, ILogger? logger = null)
    {
        var index = new PlaylistTrackIndex(library.Tracks);
        var held = library.Playlists.ToDictionary(p => p.Id);
        var deleted = manifest.Deleted?.ToHashSet() ?? [];
        var pushedIds = new HashSet<Guid>();

        var result = new List<Playlist>();
        foreach (var dto in manifest.Playlists)
        {
            if (!pushedIds.Add(dto.Id))
                continue;

            result.Add(held.TryGetValue(dto.Id, out var current) && !Supersedes(dto, current)
                ? current
                : ToPlaylist(dto, index, logger));
        }

        foreach (var playlist in library.Playlists)
        {
            if (!pushedIds.Contains(playlist.Id) && !deleted.Contains(playlist.Id))
                result.Add(playlist);
        }

        library.ReplacePlaylists(result);
        return result;
    }

    private static bool Supersedes(PlaylistSyncPlaylistDto pushed, Playlist held) =>
        pushed.UpdatedAt > held.UpdatedAt
        || (pushed.UpdatedAt == held.UpdatedAt && pushed.Rules != null && !held.IsSmart);
}

// One library, indexed the ways a playlist entry can name a track in it: by
// the id its origin knows it by (the track's own on the server, OriginTrackId
// on a device that pulled it), and by SyncKey. Built once per sync session
// rather than per playlist - a library runs to tens of thousands of tracks,
// and a session resolves every playlist at least once.
public sealed class PlaylistTrackIndex
{
    private readonly Dictionary<string, Track> _byOriginId = new();
    private readonly Dictionary<string, Track> _byKey = new();

    public PlaylistTrackIndex(IReadOnlyList<Track> library)
    {
        foreach (var track in library)
        {
            _byOriginId.TryAdd(track.OriginTrackId ?? track.Id.ToKey(), track);
            _byKey.TryAdd(track.SyncKey, track);
        }
    }

    public Track? Find(PlaylistSyncTrackDto dto) =>
        dto.Id is { Length: > 0 } id && _byOriginId.TryGetValue(id, out var byId)
            ? byId
            : _byKey.GetValueOrDefault(Track.BuildSyncKey(dto.Title, dto.Artists, dto.Album, dto.DurationSeconds));

    public List<Track> Resolve(IEnumerable<PlaylistSyncTrackDto> tracks, out List<PlaylistSyncTrackDto> unmatched)
    {
        var resolved = new List<Track>();
        unmatched = [];
        foreach (var dto in tracks)
        {
            if (Find(dto) is { } track)
                resolved.Add(track);
            else
                unmatched.Add(dto);
        }

        return resolved;
    }
}
