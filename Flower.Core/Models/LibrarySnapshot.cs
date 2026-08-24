using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Flower.Services;

namespace Flower.Models;

// What a star applies to - the three ids a Subsonic client can star. Lives
// beside the snapshot rather than in the repository because both halves need
// it: Library.SetStarred resolves it against these indexes, and
// TrackRepository.SetStarred maps it to the matching indexed column.
public enum StarTarget
{
    Song,
    Album,
    Artist,
}

// One immutable view of the whole library, plus the indexes the Subsonic
// surface reads through - grouped albums, tracks by id, tracks by album artist.
//
// Owned by Library, which rebuilds it lazily after any change to Tracks, and
// used wherever an OpenSubsonic request has to be answered out of it. It exists
// because the two hosts that then existed were doing the same grouping per
// request - the server as an aggregate SQL query, the client as a LINQ GroupBy
// over the whole library
// (LibraryOpenSubsonicMapper.FindAlbum grouped 16k tracks to pull out one
// album) - which meant the "what is an album" rule had two implementations
// that only tests held to the same answer.
//
// Immutable, never mutated in place, so a request can hold one safely while a
// rescan builds the next; Library swaps the reference. Per-track mutable state
// (Starred, PlayCount) lives on the Track objects, which survive the swap -
// Library.UpdateTracks carries them forward - so a star does not need the
// snapshot rebuilt to be visible.
public sealed class LibrarySnapshot
{
    public static readonly LibrarySnapshot Empty = Build([]);

    public required IReadOnlyList<Track> Tracks { get; init; }
    public required IReadOnlyDictionary<Guid, Track> ById { get; init; }
    public required IReadOnlyList<AlbumEntry> Albums { get; init; }

    // Public only because `required` forbids an initializer less visible than
    // its type; reads go through the accessors below, not these directly.
    public required IReadOnlyDictionary<string, AlbumEntry> AlbumsById { get; init; }
    public required IReadOnlyDictionary<string, ImmutableArray<Track>> TracksByArtistId { get; init; }

    public IReadOnlyList<Track> AlbumTracks(string albumId) =>
        AlbumsById.TryGetValue(albumId, out var album) ? album.Tracks : [];

    public IReadOnlyList<Track> ArtistTracks(string artistId) =>
        TracksByArtistId.TryGetValue(artistId, out var tracks) ? tracks : [];

    public AlbumEntry? Album(string albumId) => AlbumsById.GetValueOrDefault(albumId);

    public static LibrarySnapshot Build(IReadOnlyList<Track> tracks)
    {
        // SubsonicIdentity.AlbumIdFor: the one expression of "an album is
        // (effective album artist, album)". Getting that wrong - grouping on
        // the per-track artist - silently 404'd cover art for every
        // compilation.
        var albums = tracks
            .GroupBy(SubsonicIdentity.AlbumIdFor)
            .Select(AlbumEntry.From)
            .ToList();

        return new LibrarySnapshot
        {
            Tracks = tracks,
            // Last wins on a duplicate id rather than throwing: Library
            // tolerates a duplicate rather than failing a rescan over one
            // (see Library.BuildPathIndex), and a browse request is not the
            // place to discover it.
            ById = tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.Last()),
            Albums = albums,
            AlbumsById = albums.ToDictionary(a => a.Id),
            TracksByArtistId = tracks
                .GroupBy(t => SubsonicIdentity.ArtistId(t.EffectiveAlbumArtist))
                .ToDictionary(g => g.Key, g => g.ToImmutableArray()),
        };
    }
}

// One album's tracks and the pre-computed scalars the wire DTOs need.
public sealed class AlbumEntry
{
    public required string Id { get; init; }
    public required ImmutableArray<Track> Tracks { get; init; }
    public required AlbumSummary Summary { get; init; }

    // getAlbumList2's "newest" orders on this. Precomputed because it is a
    // per-album aggregate over every track, and recomputing it per request is
    // exactly the full scan the SQL version was written to avoid.
    public required DateTimeOffset NewestDateAdded { get; init; }

    public static AlbumEntry From(IGrouping<string, Track> group)
    {
        // Ordered once, here, so every endpoint that lists an album's songs
        // gets the same order without restating it.
        var tracks = group.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToImmutableArray();
        var first = tracks[0];
        var albumArtist = first.EffectiveAlbumArtist;

        return new AlbumEntry
        {
            Id = group.Key,
            Tracks = tracks,
            NewestDateAdded = tracks.Max(t => t.DateAdded),
            Summary = new AlbumSummary(
                AlbumId: group.Key,
                Album: first.Album,
                AlbumArtist: albumArtist,
                ArtistId: SubsonicIdentity.ArtistId(albumArtist),
                SongCount: tracks.Length,
                TotalDuration: TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks)),
                Year: int.TryParse(first.Year, out var year) ? year : null,
                Genre: first.Genre),
        };
    }
}

// One album's worth of scalars, as the wire DTOs want them. Was computed by a
// SQL GROUP BY on the server side; now computed once per rescan in
// AlbumEntry.From, for both hosts.
public sealed record AlbumSummary(
    string AlbumId,
    string? Album,
    string? AlbumArtist,
    string? ArtistId,
    int SongCount,
    TimeSpan TotalDuration,
    int? Year,
    string? Genre);
