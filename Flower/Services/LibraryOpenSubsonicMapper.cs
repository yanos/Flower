using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Maps this device's own real (Path != null) tracks into OpenSubsonic ID3-shaped
// DTOs for SyncHttpServer's embedded host - see SYNC-PLAN.md Phase 3's "one
// client, three interchangeable servers". Never includes placeholder tracks
// (Path == null): a real OpenSubsonic server, and Flower's own embedded one, only
// ever reports tracks it actually has - see the plan's no-multi-hop-provenance
// note (a device wanting the full known universe of tracks queries each peer
// directly rather than trusting any one peer to relay what it heard secondhand).
public static class LibraryOpenSubsonicMapper
{
    public static List<AlbumID3> BuildAlbumList(IReadOnlyList<Track> tracks) =>
        GroupByAlbum(tracks)
            .Select(g => { var list = g.ToList(); return ToAlbumID3(g.Key, list, ComputeAlbumArtHash(list)); })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Flat song list across every album, for the bespoke bulk sync endpoint
    // (GET /api/flower/v1/library - see LibrarySyncContracts/LibrarySyncService)
    // rather than the OpenSubsonic-shaped one-request-per-album pair above.
    // selfFingerprint is this device's own DeviceIdentity.Fingerprint - see
    // ToChild's PlayCounts field.
    public static List<Child> BuildAllSongs(IReadOnlyList<Track> tracks, string selfFingerprint) =>
        GroupByAlbum(tracks)
            .SelectMany(g =>
            {
                var list = g.ToList();
                var artHash = ComputeAlbumArtHash(list);
                return list.Select(t => ToChild(t, g.Key, artHash, selfFingerprint));
            })
            .ToList();

    public static AlbumWithSongsID3? FindAlbum(IReadOnlyList<Track> tracks, string albumId, string selfFingerprint)
    {
        var group = GroupByAlbum(tracks).FirstOrDefault(g => g.Key == albumId);
        if (group == null)
            return null;

        var list = group.ToList();
        var artHash = ComputeAlbumArtHash(list);
        var songs = list.Select(t => ToChild(t, albumId, artHash, selfFingerprint)).ToList();
        var summary = ToAlbumID3(albumId, list, artHash);
        return new AlbumWithSongsID3(
            summary.Id, summary.Name, summary.Artist, summary.ArtistId, summary.CoverArt,
            summary.SongCount, summary.Duration, summary.Year, summary.Genre, songs);
    }

    // Content hash of the album's own art bytes (see AlbumArtLoader.TryGetLocalArtBytes/
    // ComputeArtHash), read off whichever local track in the group actually has a file -
    // stamped onto CoverArt below so a peer receiving this in a sync manifest can tell
    // "art changed since I last cached it" apart from "same art as before" without
    // transferring the bytes themselves every time (see AlbumArtLoader's remote-fetch
    // path, SYNC-PLAN.md Phase 3). Null if no track in the group has any art at all.
    private static string? ComputeAlbumArtHash(List<Track> tracks)
    {
        var track = tracks.FirstOrDefault(t => t.Path != null);
        if (track?.Path == null)
            return null;

        // Memoized on the file's own identity, because the uncached cost is
        // the dominant one on this whole path: every call opens the file with
        // TagLib and SHA-256s the art bytes, once per album, so a full
        // getAlbumList2 over a 16k-track library was ~1,400 file opens and
        // hashes per request (ARCHITECTURE-REVIEW Tier 1.4). Keyed by path +
        // last-write time + length so re-tagging a file invalidates the entry
        // on its own, with no cache-busting call sites to remember. The bulk
        // /library manifest is additionally cached wholesale by
        // SyncHttpServer; this covers the OpenSubsonic browse endpoints, which
        // are per-request by nature.
        FileInfo info;
        try
        {
            info = new FileInfo(track.Path);
            if (!info.Exists)
                return null;
        }
        catch (Exception)
        {
            return null; // Unreadable path - same "no art" answer as a track with none.
        }

        var key = $"{track.Path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        if (ArtHashCache.TryGetValue(key, out var cached))
            return cached;

        var bytes = AlbumArtLoader.TryGetLocalArtBytes(track);
        var hash = bytes != null ? AlbumArtLoader.ComputeArtHash(bytes) : null;

        // Bounded rather than unbounded: one entry per album-leading file, so
        // a library the size of the 16k-track development one settles around
        // 1,400 entries, but nothing structurally stops a long-running server
        // from seeing far more paths than that over its lifetime. Clearing
        // wholesale at the cap beats evicting cleverly - the next few requests
        // just repopulate what they need.
        if (ArtHashCache.Count >= MaxCachedArtHashes)
            ArtHashCache.Clear();
        ArtHashCache[key] = hash;
        return hash;
    }

    private const int MaxCachedArtHashes = 5000;
    private static readonly ConcurrentDictionary<string, string?> ArtHashCache = new();

    // Grouped by (Album, EffectiveAlbumArtist) rather than Album alone, so two
    // different artists' same-named album ("Greatest Hits") don't collide into
    // one entry. EffectiveAlbumArtist rather than raw per-track Artists keeps a
    // various-artists compilation - same Album, differing per-track Artists,
    // but a consistent (or absent) AlbumArtists tag - as one entry instead of
    // fragmenting into one per distinct track artist (see Track.EffectiveAlbumArtist).
    private static IEnumerable<IGrouping<string, Track>> GroupByAlbum(IReadOnlyList<Track> tracks) =>
        tracks.Where(t => t.Path != null).GroupBy(t => AlbumId(t.Album, t.EffectiveAlbumArtist));

    public static string AlbumId(string? album, string? artist) => $"al:{Normalize(album)}|{Normalize(artist)}";
    public static string ArtistId(string? artist) => $"ar:{Normalize(artist)}";

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static AlbumID3 ToAlbumID3(string albumId, List<Track> tracks, string? artHash)
    {
        var first = tracks[0];
        return new AlbumID3(
            Id: albumId,
            Name: first.Album ?? "",
            Artist: first.EffectiveAlbumArtist,
            ArtistId: ArtistId(first.EffectiveAlbumArtist),
            CoverArt: artHash,
            SongCount: tracks.Count,
            Duration: (long)tracks.Sum(t => t.Duration.TotalSeconds),
            Year: ParseYear(first.Year),
            Genre: first.Genre);
    }

    private static Child ToChild(Track track, string albumId, string? artHash, string selfFingerprint) => new(
        Id: track.SyncKey,
        Title: track.Title ?? "",
        Album: track.Album,
        Artist: track.Artists,
        AlbumId: albumId,
        ArtistId: ArtistId(track.Artists),
        Track: track.TrackNumber == 0 ? null : (int)track.TrackNumber,
        Year: ParseYear(track.Year),
        Genre: track.Genre,
        Size: null,
        ContentType: null,
        // The downloading side needs a real file extension to save with (Path is
        // null until then - see LibrarySyncMapper/LibraryDownloadService); Path
        // itself never crosses the wire (SYNC-PLAN.md's Path-can't-cross-the-wire
        // rule), but its extension alone leaks nothing about this device's layout.
        Suffix: track.Path != null ? System.IO.Path.GetExtension(track.Path).TrimStart('.') : null,
        // Track.RoundedSeconds, not a separate inline Math.Round - must match
        // Track.SyncKey's own rounding exactly. A receiving peer never trusts
        // this Child's own Id field as authoritative
        // (LibrarySyncMapper.ToPlaceholderTrack doesn't even store it) - it
        // independently recomputes its own SyncKey later from this Duration
        // field alone (via TimeSpan.FromSeconds(song.Duration) -> SyncKey's
        // own rounding) to ask this same device to stream/download the track.
        Duration: Track.RoundedSeconds(track.Duration),
        BitRate: track.Bitrate > 0 ? track.Bitrate : null,
        // See AlbumId's own CoverArt above - a content hash of the album's art
        // bytes, not an opaque id, so a peer syncing this manifest can tell
        // whether it needs to (re-)fetch art without a round trip just to ask.
        CoverArt: artHash,
        // This device's own tally (PlayCount + ImportedPlayCount - see
        // Track.TotalPlayCount's doc comment on why the two are combined for
        // anything leaving this device) plus every other device's count already
        // learned via a previous sync (RemotePlayCounts) - a snapshot of
        // everything this device currently knows, so a receiving peer converges
        // even for a device it never discovers directly, as long as some other
        // device it does talk to has synced with that one at some point.
        PlayCounts: new Dictionary<string, int>(track.RemotePlayCounts)
        {
            [selfFingerprint] = track.PlayCount + track.ImportedPlayCount,
        },
        DateAdded: track.DateAdded);

    private static int? ParseYear(string? year) => int.TryParse(year, out var y) ? y : null;
}
