using System.Text.Json.Serialization;

using Flower.Services;

namespace Flower.Server.Services;

// Wire shapes for the OpenSubsonic/Subsonic REST API, and nothing else: every
// type here exists because the spec says so, and every one of them is reachable
// only from /rest.
//
// This is the adapter's half of the protocol split. Flower's own catalog shape
// is TrackDto in Flower.Core (see LibraryContracts), which the songs below are
// lists of - the adapter wraps the catalog rather than defining a second one.
// Deleting OpenSubsonic support means deleting this file and the endpoints that
// build these, and nothing in Flower.Core or the app moves.
//
// Only the fields Flower fills are modeled - this is not a complete mirror of
// the spec (no bookmarks, internet radio, shares, chat, podcasts, etc).

public sealed class SubsonicEnvelope
{
    [JsonPropertyName("subsonic-response")]
    public SubsonicResponse? Response { get; init; }
}

public sealed class SubsonicResponse
{
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
    public SubsonicError? Error { get; init; }

    public ArtistsID3? Artists { get; init; }
    public ArtistWithAlbumsID3? Artist { get; init; }
    public AlbumWithSongsID3? Album { get; init; }
    public AlbumList2? AlbumList2 { get; init; }
    public TrackDto? Song { get; init; }
    public SearchResult3? SearchResult3 { get; init; }
    public SubsonicPlaylists? Playlists { get; init; }
    public PlaylistWithSongsDto? Playlist { get; init; }
}

public sealed record SubsonicError(int Code, string Message);

public sealed record ArtistsID3(List<IndexID3> Index);

public sealed record IndexID3(string Name, List<ArtistID3> Artist);

public sealed record ArtistID3(
    string Id,
    string Name,
    string? CoverArt,
    int AlbumCount);

public sealed record ArtistWithAlbumsID3(
    string Id,
    string Name,
    string? CoverArt,
    int AlbumCount,
    List<AlbumID3>? Album);

public sealed record AlbumID3(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    string? CoverArt,
    int SongCount,
    long Duration,
    int? Year,
    string? Genre);

public sealed record AlbumWithSongsID3(
    string Id,
    string Name,
    string? Artist,
    string? ArtistId,
    string? CoverArt,
    int SongCount,
    long Duration,
    int? Year,
    string? Genre,
    List<TrackDto>? Song);

public sealed record AlbumList2(List<AlbumID3> Album);

public sealed record SearchResult3(
    List<ArtistID3>? Artist,
    List<AlbumID3>? Album,
    List<TrackDto>? Song);

public sealed record SubsonicPlaylists(List<PlaylistDto> Playlist);

public sealed record PlaylistDto(
    string Id,
    string Name,
    string? Comment,
    int SongCount,
    long Duration,
    string? Owner,
    bool Public);

public sealed record PlaylistWithSongsDto(
    string Id,
    string Name,
    string? Comment,
    int SongCount,
    long Duration,
    string? Owner,
    bool Public,
    List<TrackDto>? Entry);
