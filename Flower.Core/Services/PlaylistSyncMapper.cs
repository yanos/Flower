using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Conversions between Flower.Models.Playlist and the sync wire DTOs, shared by
// the server's side of playlist sync (PlaylistRevisions) and PlaylistSyncService,
// which drives a sync session from the client side.
public static class PlaylistSyncMapper
{
    public static PlaylistSyncPlaylistDto ToDto(Playlist playlist) =>
        new(playlist.Id, playlist.Name, playlist.UpdatedAt, playlist.Entries.Select(e => e.ToWire()).ToList(), playlist.Rules,
            playlist.IsFolder, playlist.ParentId, playlist.SortKey, playlist.VibePrompt, playlist.VibeInstrumental);

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

    // A smart playlist arrives carrying both its rules and whatever the peer
    // had materialized from them. Both are kept: the tracks so the playlist is
    // not empty for the moment before it is evaluated here (and permanently,
    // for a LiveUpdating = false one, which no recomputation pass will ever
    // touch), the rules so this device evaluates them against its own library
    // from then on. Nothing has to schedule that evaluation - installing the
    // merged set raises Library.PlaylistsChanged, which SmartPlaylistRefresher
    // is already subscribed to.
    //
    // An ordinary playlist keeps every row, including those this library has
    // no song for: they are hidden here and go on in the next push, because a
    // row leaves a playlist only when somebody removes it. Dropping them is how
    // one edit on a device missing the songs emptied a playlist everywhere.
    public static Playlist ToPlaylist(PlaylistSyncPlaylistDto dto, IReadOnlyList<Track> localLibrary, ILogger? logger = null) =>
        ToPlaylist(dto, new PlaylistTrackIndex(localLibrary), logger);

    public static Playlist ToPlaylist(
        PlaylistSyncPlaylistDto dto, PlaylistTrackIndex index, ILogger? logger = null, string listener = Listeners.Owner)
    {
        var keepsRows = dto.Rules == null && dto.VibePrompt == null && !dto.IsFolder;
        var entries = new List<PlaylistEntry>(dto.Tracks.Count);
        var unmatched = new List<PlaylistSyncTrackDto>();
        foreach (var row in dto.Tracks)
        {
            var id = row.EntryId ?? Guid.NewGuid();
            if (index.Find(row) is { } track)
            {
                entries.Add(new PlaylistEntry(id, track));
                continue;
            }

            unmatched.Add(row);
            if (keepsRows)
                entries.Add(new PlaylistEntry(id, null, Song: row with { EntryId = null }));
        }

        if (unmatched.Count > 0)
        {
            logger?.LogInformation(
                "Playlist {Name} ({PlaylistId}): {Unmatched} of {Total} track(s) are not in this library: {Tracks}",
                dto.Name, dto.Id, unmatched.Count, dto.Tracks.Count,
                string.Join("; ", unmatched.Select(t => $"{t.Artists} - {t.Title} [{t.Album}, {t.DurationSeconds}s]")));
        }

        return new(dto.Id, dto.Name, [], dto.UpdatedAt, rules: dto.Rules, listener: listener,
            isFolder: dto.IsFolder, parentId: dto.ParentId, sortKey: dto.SortKey,
            vibePrompt: dto.VibePrompt, vibeInstrumental: dto.VibeInstrumental, entries: entries);
    }

    // What a GET /playlists answers with, and what a client's /info poll is
    // told about: one listener's playlists, never the server's whole set.
    public static List<Playlist> For(IEnumerable<Playlist> playlists, string listener) =>
        playlists.Where(p => p.Listener == listener).ToList();

    // A token that moves whenever one listener's playlists change, for /info's
    // playlistsToken. Derived from the playlists rather than counted, because
    // the counter Library keeps (PlaylistsToken) moves for every listener's
    // edits, and a guest renaming its own playlist must not send the owner's
    // devices off to re-sync theirs. UpdatedAt moves on every edit a sync
    // carries (see Playlist.Touch), and the id list covers a delete.
    //
    // A Vibe Playlist's pool counts too. The server ranks it (VibePlaylistPools)
    // without moving UpdatedAt, the way a smart playlist is recomputed, so
    // without its songs in the token a device that saved a new sentence would
    // never come back for the songs the server found for it.
    public static string TokenOf(IEnumerable<Playlist> playlists)
    {
        var text = string.Join("\n", playlists
            .Select(p => p.IsVibe
                ? $"{p.Id:N}:{p.UpdatedAt.UtcTicks}:{string.Join(",", p.Tracks.Select(t => t.Id.ToKey()))}"
                : $"{p.Id:N}:{p.UpdatedAt.UtcTicks}")
            .Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
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

    // A row with no title has no tags to match on, and must not land on
    // whichever untagged song the library happens to hold.
    public Track? Find(PlaylistSyncTrackDto dto) =>
        dto.Id is { Length: > 0 } id && _byOriginId.TryGetValue(id, out var byId)
            ? byId
            : string.IsNullOrWhiteSpace(dto.Title)
                ? null
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
