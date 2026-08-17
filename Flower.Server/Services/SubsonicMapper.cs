using Flower.Models;
using Flower.Server.Data;
using Flower.Services;

namespace Flower.Server.Services;

// TrackEntity/grouped-track -> the OpenSubsonic wire DTOs from Flower.Core's
// OpenSubsonicContracts.cs (see SYNC-PLAN.md's "Reuse boundary": these are the
// same shapes OpenSubsonicClient parses, reused directly rather than defining
// a server-side duplicate of the same fields).
public static class SubsonicMapper
{
    public static Child ToChild(TrackEntity t) => new(
        Id: t.Id,
        Title: t.Title ?? Path.GetFileNameWithoutExtension(t.Path),
        Album: t.Album,
        Artist: t.Artist,
        AlbumId: t.AlbumId,
        ArtistId: t.ArtistId,
        Track: t.TrackNumber > 0 ? t.TrackNumber : null,
        Year: t.Year,
        Genre: t.Genre,
        Size: t.Size,
        ContentType: t.ContentType,
        Suffix: t.Suffix,
        // Track.RoundedSeconds, not an inline Math.Round: an earlier inline
        // version here truncated instead of rounding and silently disagreed
        // with the client's own duration for any track whose fractional part
        // was >= .5s (see Track.RoundedSeconds' own comment).
        Duration: Track.RoundedSeconds(t.DurationSeconds),
        BitRate: t.Bitrate > 0 ? t.Bitrate : null,
        CoverArt: t.AlbumId,
        Starred: t.Starred,
        DateAdded: t.DateAdded);

    public static ArtistID3 ToArtistId3(string artistId, string name, int albumCount) => new(
        Id: artistId,
        Name: name,
        CoverArt: null,
        AlbumCount: albumCount);

    // One album's worth of pre-aggregated columns, as computed by SQL rather
    // than by grouping materialized entities in memory - see
    // SubsonicEndpoints.GetAlbumList2 and GetArtists.
    //
    // Init-only properties rather than a positional record on purpose: EF Core
    // can only translate a grouped aggregate projection written as a member
    // initializer, not as a constructor call - a positional record compiles
    // fine and then throws "could not be translated" at request time.
    public sealed class AlbumSummary
    {
        public required string AlbumId { get; init; }
        public string? Album { get; init; }
        public string? AlbumArtist { get; init; }
        public string? ArtistId { get; init; }
        public int SongCount { get; init; }
        public double TotalDurationSeconds { get; init; }
        public int? Year { get; init; }
        public string? Genre { get; init; }
    }

    public static AlbumID3 ToAlbumId3(AlbumSummary album) => new(
        Id: album.AlbumId,
        Name: album.Album ?? "Unknown Album",
        Artist: album.AlbumArtist,
        ArtistId: album.ArtistId ?? "",
        CoverArt: album.AlbumId,
        SongCount: album.SongCount,
        Duration: (long)album.TotalDurationSeconds,
        Year: album.Year,
        Genre: album.Genre);

    public static AlbumID3 ToAlbumId3(IGrouping<string, TrackEntity> albumTracks)
    {
        var first = albumTracks.First();
        return new AlbumID3(
            Id: first.AlbumId,
            Name: first.Album ?? "Unknown Album",
            Artist: first.AlbumArtist,
            ArtistId: first.ArtistId,
            CoverArt: first.AlbumId,
            SongCount: albumTracks.Count(),
            Duration: (long)albumTracks.Sum(t => t.DurationSeconds),
            Year: first.Year,
            Genre: first.Genre);
    }
}
