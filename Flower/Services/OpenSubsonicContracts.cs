using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Flower.Services;

// Wire shapes for the OpenSubsonic/Subsonic REST API (see SYNC-PLAN.md, "The
// unifying decision"). Every response is JSON-wrapped in a "subsonic-response"
// envelope; only the fields Flower's client actually reads are modeled here
// (browse, stream/download, playlist CRUD, search, cover art, star, scrobble) -
// this is not a complete mirror of the spec (no bookmarks, internet radio,
// shares, chat, podcasts, etc).

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
    public Child? Song { get; init; }
    public SearchResult3? SearchResult3 { get; init; }
    public Playlists? Playlists { get; init; }
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
    List<Child>? Song);

public sealed record AlbumList2(List<AlbumID3> Album);

// A song, in Subsonic's terminology ("Child" is the spec's own name for this
// shape - it's shared with directory entries in the pre-ID3 browsing API,
// which Flower's client doesn't use).
public sealed record Child(
    string Id,
    string Title,
    string? Album,
    string? Artist,
    string? AlbumId,
    string? ArtistId,
    int? Track,
    int? Year,
    string? Genre,
    long? Size,
    string? ContentType,
    string? Suffix,
    int? Duration,
    int? BitRate,
    string? CoverArt,
    bool Starred = false,
    // Not part of the real OpenSubsonic spec - Flower-specific, ignored by any
    // third-party server/client that doesn't know about it. Every device's
    // latest known play count for this song, keyed by DeviceIdentity.Fingerprint
    // - see LibraryOpenSubsonicMapper.ToChild and Track.RemotePlayCounts for how
    // this propagates play counts between devices without a central server.
    Dictionary<string, int>? PlayCounts = null,
    // Also Flower-specific, same reasoning as PlayCounts above. The sending
    // device's own Track.DateAdded - without this, a synced placeholder
    // defaults DateAdded to "now" (see LibrarySyncMapper.ToPlaceholderTrack),
    // so a Client's Recently Added view would show a burst of everything at
    // sync time instead of matching the paired Server's actual chronology.
    // Null when talking to a third-party server that doesn't send it.
    System.DateTimeOffset? DateAdded = null);

public sealed record SearchResult3(
    List<ArtistID3>? Artist,
    List<AlbumID3>? Album,
    List<Child>? Song);

public sealed record Playlists(List<PlaylistDto> Playlist);

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
    List<Child>? Entry);
