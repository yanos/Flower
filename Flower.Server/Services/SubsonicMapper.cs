using Flower.Models;
using Flower.Services;

namespace Flower.Server.Services;

// Track / pre-aggregated album row -> the OpenSubsonic wire DTOs from
// Flower.Core's OpenSubsonicContracts.cs (see SYNC-PLAN.md's "Reuse boundary":
// these are the same shapes OpenSubsonicClient parses, reused directly rather
// than defining a server-side duplicate of the same fields).
//
// The input is Flower.Core's Track now, not a server-private TrackEntity - see
// Library.Snapshot for why that seam went away.
public static class SubsonicMapper
{
    private static readonly Dictionary<string, string> ContentTypesBySuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["alac"] = "audio/mp4",
    };

    public static string SuffixOf(Track track) =>
        track.Path is null ? "" : Path.GetExtension(track.Path).TrimStart('.').ToLowerInvariant();

    public static string ContentTypeOf(Track track) =>
        ContentTypesBySuffix.GetValueOrDefault(SuffixOf(track), "application/octet-stream");

    public static Child ToChild(Track track)
    {
        var albumArtist = track.EffectiveAlbumArtist;
        var suffix = SuffixOf(track);

        return new Child(
            Id: track.Id.ToKey(),
            Title: track.Title ?? (track.Path is null ? "" : Path.GetFileNameWithoutExtension(track.Path)),
            Album: track.Album,
            Artist: track.Artists,
            AlbumId: SubsonicIdentity.AlbumId(albumArtist, track.Album),
            ArtistId: SubsonicIdentity.ArtistId(albumArtist),
            Track: track.TrackNumber > 0 ? (int)track.TrackNumber : null,
            Year: ParseYear(track.Year),
            Genre: track.Genre,
            // Size, Suffix and ContentType are derived from the file and its
            // path here rather than stored as columns. They used to be three
            // TrackEntity fields stamped at import time, which meant they
            // described the file as it was when last scanned; a stat at map
            // time describes it as it is now, and keeps three columns the
            // client would never fill out of the shared schema. Bounded work:
            // the endpoints that map a Child return one album, one playlist or
            // one page of search hits, never the whole library.
            Size: SizeOf(track),
            ContentType: ContentTypeOf(track),
            Suffix: suffix.Length == 0 ? null : suffix,
            // Track.RoundedSeconds, not an inline Math.Round: an earlier
            // version here truncated instead of rounding and silently
            // disagreed with the client's own duration for any track whose
            // fractional part was >= .5s (see Track.RoundedSeconds' comment).
            Duration: Track.RoundedSeconds(track.Duration),
            BitRate: track.Bitrate > 0 ? track.Bitrate : null,
            CoverArt: SubsonicIdentity.AlbumId(albumArtist, track.Album),
            Starred: track.Starred,
            DateAdded: track.DateAdded);
    }

    private static long SizeOf(Track track)
    {
        if (track.Path is null)
            return 0;

        try
        {
            var info = new FileInfo(track.Path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            // An unreadable path is not worth failing a browse response over -
            // the same "no answer" a missing file gets.
            return 0;
        }
    }

    public static ArtistID3 ToArtistId3(string artistId, string name, int albumCount) => new(
        Id: artistId,
        Name: name,
        CoverArt: null,
        AlbumCount: albumCount);

    public static AlbumID3 ToAlbumId3(AlbumSummary album) => new(
        Id: album.AlbumId,
        Name: album.Album ?? "Unknown Album",
        Artist: album.AlbumArtist,
        ArtistId: album.ArtistId ?? "",
        CoverArt: album.AlbumId,
        SongCount: album.SongCount,
        Duration: (long)album.TotalDuration.TotalSeconds,
        Year: album.Year,
        Genre: album.Genre);

    // The in-memory equivalent, for the one caller that already holds an
    // album's tracks (getArtist, which reads one artist's rows in full anyway
    // and would otherwise issue a second aggregate query per album).
    public static AlbumID3 ToAlbumId3(IGrouping<string, Track> albumTracks)
    {
        var first = albumTracks.First();
        var albumArtist = first.EffectiveAlbumArtist;
        return new AlbumID3(
            Id: albumTracks.Key,
            Name: first.Album ?? "Unknown Album",
            Artist: albumArtist,
            ArtistId: SubsonicIdentity.ArtistId(albumArtist),
            CoverArt: albumTracks.Key,
            SongCount: albumTracks.Count(),
            Duration: (long)albumTracks.Sum(t => t.Duration.TotalSeconds),
            Year: ParseYear(first.Year),
            Genre: first.Genre);
    }

    private static int? ParseYear(string? year) => int.TryParse(year, out var parsed) ? parsed : null;
}
