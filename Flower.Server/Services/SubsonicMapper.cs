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
        Duration: (int)Math.Round(t.DurationSeconds),
        BitRate: t.Bitrate > 0 ? t.Bitrate : null,
        CoverArt: t.AlbumId,
        Starred: t.Starred,
        DateAdded: t.DateAdded);

    public static ArtistID3 ToArtistId3(string artistId, string name, int albumCount) => new(
        Id: artistId,
        Name: name,
        CoverArt: null,
        AlbumCount: albumCount);

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
