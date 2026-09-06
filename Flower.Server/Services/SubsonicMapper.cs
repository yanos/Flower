using Flower.Models;
using Flower.Services;

namespace Flower.Server.Services;

// The album and artist rows of the OpenSubsonic browse endpoints, and only
// those: the ID3 shapes in SubsonicContracts.cs, built from Flower's own
// LibrarySnapshot.
//
// A song is not here. That is LibraryDtoMapper.ToTrackDto in Flower.Core,
// because a TrackDto is the catalog's own shape and /rest is one publisher of
// it rather than its owner - see that class. This file is adapter, and goes
// when the adapter does.
public static class SubsonicMapper
{
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
            ArtistId: CatalogIdentity.ArtistId(albumArtist),
            CoverArt: albumTracks.Key,
            SongCount: albumTracks.Count(),
            Duration: (long)albumTracks.Sum(t => t.Duration.TotalSeconds),
            Year: ParseYear(first.Year),
            Genre: first.Genre);
    }

    private static int? ParseYear(string? year) => int.TryParse(year, out var parsed) ? parsed : null;
}
