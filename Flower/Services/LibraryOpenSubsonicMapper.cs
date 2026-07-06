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
            .Select(g => ToAlbumID3(g.Key, g.ToList()))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Flat song list across every album, for the bespoke bulk sync endpoint
    // (GET /api/flower/v1/library - see LibrarySyncContracts/LibrarySyncService)
    // rather than the OpenSubsonic-shaped one-request-per-album pair above.
    public static List<Child> BuildAllSongs(IReadOnlyList<Track> tracks) =>
        GroupByAlbum(tracks)
            .SelectMany(g => g.Select(t => ToChild(t, g.Key)))
            .ToList();

    public static AlbumWithSongsID3? FindAlbum(IReadOnlyList<Track> tracks, string albumId)
    {
        var group = GroupByAlbum(tracks).FirstOrDefault(g => g.Key == albumId);
        if (group == null)
            return null;

        var songs = group.Select(t => ToChild(t, albumId)).ToList();
        var summary = ToAlbumID3(albumId, group.ToList());
        return new AlbumWithSongsID3(
            summary.Id, summary.Name, summary.Artist, summary.ArtistId, summary.CoverArt,
            summary.SongCount, summary.Duration, summary.Year, summary.Genre, songs);
    }

    // Grouped by (Album, Artist) rather than Album alone, so two different
    // artists' same-named album ("Greatest Hits") don't collide into one entry.
    private static IEnumerable<IGrouping<string, Track>> GroupByAlbum(IReadOnlyList<Track> tracks) =>
        tracks.Where(t => t.Path != null).GroupBy(t => AlbumId(t.Album, t.Artists));

    public static string AlbumId(string? album, string? artist) => $"al:{Normalize(album)}|{Normalize(artist)}";
    public static string ArtistId(string? artist) => $"ar:{Normalize(artist)}";

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static AlbumID3 ToAlbumID3(string albumId, List<Track> tracks)
    {
        var first = tracks[0];
        return new AlbumID3(
            Id: albumId,
            Name: first.Album ?? "",
            Artist: first.Artists,
            ArtistId: ArtistId(first.Artists),
            CoverArt: null,
            SongCount: tracks.Count,
            Duration: (long)tracks.Sum(t => t.Duration.TotalSeconds),
            Year: ParseYear(first.Year),
            Genre: first.Genre);
    }

    private static Child ToChild(Track track, string albumId) => new(
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
        CoverArt: null);

    private static int? ParseYear(string? year) => int.TryParse(year, out var y) ? y : null;
}
