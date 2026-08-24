using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Maps this device's own real (Path != null) tracks into OpenSubsonic ID3-shaped
// DTOs. Written for the app's own embedded host, since removed; what still uses
// it is the album-id and cover-art hashing AlbumArtLoader and
// RecentlyAddedAlbumsBuilder need. Never includes placeholder tracks
// (Path == null): a real OpenSubsonic server only
// ever reports tracks it actually has - see the plan's no-multi-hop-provenance
// note (a device wanting the full known universe of tracks queries each peer
// directly rather than trusting any one peer to relay what it heard secondhand).
public static class LibraryOpenSubsonicMapper
{
    public static List<AlbumID3> BuildAlbumList(LibrarySnapshot snapshot) =>
        GroupByAlbum(snapshot)
            .Select(g => ToAlbumID3(g.Key, g.Value, ComputeAlbumArtHash(g.Value)))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Flat song list across every album, for the bespoke bulk sync endpoint
    // (GET /api/flower/v1/library - see LibrarySyncContracts/LibrarySyncService)
    // rather than the OpenSubsonic-shaped one-request-per-album pair above.
    // selfFingerprint is this device's own DeviceIdentity.Fingerprint - see
    // ToChild's PlayCounts field.
    public static List<Child> BuildAllSongs(LibrarySnapshot snapshot, string selfFingerprint) =>
        GroupByAlbum(snapshot)
            .SelectMany(g =>
            {
                var artHash = ComputeAlbumArtHash(g.Value);
                return g.Value.Select(t => ToChild(t, g.Key, artHash, selfFingerprint));
            })
            .ToList();

    public static AlbumWithSongsID3? FindAlbum(LibrarySnapshot snapshot, string albumId, string selfFingerprint)
    {
        // One dictionary lookup. This used to group the entire library and then
        // take the single matching entry off the front of it, on every request.
        var list = LocalTracks(snapshot.AlbumTracks(albumId));
        if (list.Count == 0)
            return null;

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
        // /library manifest is additionally cached wholesale on the serving
        // side; this covers the OpenSubsonic browse shapes, which are
        // per-request by nature.
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

    // The albums are already grouped - by (Album, EffectiveAlbumArtist), once
    // per library change, in LibrarySnapshot.Build, which the standalone server
    // reads through too. All that is left here is this surface's own rule that
    // placeholder tracks are never served, and dropping an album that has
    // nothing but placeholders left in it.
    private static IEnumerable<KeyValuePair<string, List<Track>>> GroupByAlbum(LibrarySnapshot snapshot) =>
        snapshot.Albums
            .Select(album => new KeyValuePair<string, List<Track>>(album.Id, LocalTracks(album.Tracks)))
            .Where(entry => entry.Value.Count > 0);

    // A real OpenSubsonic server only reports tracks it actually has, and so
    // does this one - see the class comment on placeholders.
    private static List<Track> LocalTracks(IReadOnlyList<Track> tracks) =>
        tracks.Where(t => t.Path != null).ToList();

    public static string AlbumIdFor(Track track) => SubsonicIdentity.AlbumIdFor(track);

    private static AlbumID3 ToAlbumID3(string albumId, List<Track> tracks, string? artHash)
    {
        var first = tracks[0];
        return new AlbumID3(
            Id: albumId,
            Name: first.Album ?? "",
            Artist: first.EffectiveAlbumArtist,
            ArtistId: SubsonicIdentity.ArtistId(first.EffectiveAlbumArtist),
            CoverArt: artHash,
            SongCount: tracks.Count,
            Duration: (long)tracks.Sum(t => t.Duration.TotalSeconds),
            Year: ParseYear(first.Year),
            Genre: first.Genre);
    }

    private static Child ToChild(Track track, string albumId, string? artHash, string selfFingerprint) => new(
        // Track.Id, this device's own stable surrogate identity for the track,
        // not its SyncKey. SyncKey is derived from tags and a rounded duration,
        // so editing a tag here silently invalidated every id a peer was still
        // holding (ARCHITECTURE-REVIEW Tier 2.1); Id survives tag edits, rescans
        // and downloads. Opaque to the receiver either way, which is what the
        // OpenSubsonic spec requires of an id - the peer stores it verbatim as
        // Track.OriginTrackId and hands it straight back to /rest/stream.
        Id: track.Id.ToKey(),
        Title: track.Title ?? "",
        Album: track.Album,
        Artist: track.Artists,
        AlbumId: albumId,
        // The album artist, matching AlbumIdFor above and the standalone
        // server (LibraryImportService) - an artist id derived from the
        // per-track Artists would point at an artist the album listing never
        // mentions for any compilation.
        ArtistId: SubsonicIdentity.ArtistId(track.EffectiveAlbumArtist),
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
        // Track.SyncKey's own rounding exactly. The receiving peer no longer
        // needs this to address the track (it keeps Id above verbatim), but it
        // does still rebuild a placeholder Track from these fields, and
        // Library.MergeSyncedTracks/UpdateTracks match that placeholder against
        // this device's library by SyncKey - so a duration that disagreed by a
        // second would still fragment one track into two.
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
        DateAdded: track.DateAdded,
        LastPlayed: track.LastPlayedAt);

    private static int? ParseYear(string? year) => int.TryParse(year, out var y) ? y : null;
}
