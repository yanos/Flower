using System;
using System.Collections.Generic;
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
        var bytes = track != null ? AlbumArtLoader.TryGetLocalArtBytes(track) : null;
        return bytes != null ? AlbumArtLoader.ComputeArtHash(bytes) : null;
    }

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
        Duration: (int)track.Duration.TotalSeconds,
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
