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
    System.DateTimeOffset? DateAdded = null,
    // Also Flower-specific, and the History counterpart to DateAdded above.
    // Without it a receiving head's History is empty until it plays something
    // itself - which for a browser tab means every refresh, since a tab keeps
    // nothing across one. See Track.LastPlayedAt, and IPlayReporter for the
    // road a tab's own plays take in the other direction.
    System.DateTimeOffset? LastPlayed = null,
    // The sender's Track.EffectiveAlbumArtist - the artist the album is
    // *grouped* by, as opposed to Artist above, which is this one song's own
    // credit. This one IS part of the OpenSubsonic spec (it serializes as
    // "displayAlbumArtist" under the camelCase policy), so a third-party client
    // gets the same benefit.
    //
    // Without it a receiving head had no way to reconstruct the grouping: it
    // recomputes EffectiveAlbumArtist locally from AlbumArtists/IsCompilation,
    // and neither field crossed the wire, so every synced track fell through to
    // its per-track Artists. A various-artists compilation therefore shattered
    // into one album tile per contributing artist on the client - a 31-artist
    // compilation became 31 tiles sharing a name. The AlbumId/ArtistId this
    // record already carried were computed from the album artist and so were
    // correct all along; only the value they were derived from was missing.
    string? DisplayAlbumArtist = null,
    // Flower-specific, same reasoning as PlayCounts above - the OpenSubsonic
    // spec has no compilation flag. Carried alongside DisplayAlbumArtist rather
    // than folded into it because EffectiveAlbumArtist is a three-way fallback
    // and collapsing it to one string loses which branch produced it: a
    // compilation whose AlbumArtists tag was left blank and an album genuinely
    // tagged "Various Artists" both arrive as the same display string, and a
    // receiving head that later downloads the file would disagree with its own
    // rescan of it. See Track.IsCompilation.
    bool IsCompilation = false,
    // The rest of what the Technical tab of Track Info shows. BitRate above was
    // the only one of these the manifest carried, and even it was dropped on
    // arrival (see LibrarySyncMapper.ToPlaceholderTrack), so a library made
    // entirely of synced placeholders showed an all-"—" Technical tab while the
    // serving side had every value from its own TagLib scan.
    //
    // SamplingRate, ChannelCount and BitDepth are real OpenSubsonic fields, so a
    // third-party client browsing a Flower host gets them too. Codec is not in
    // the spec - it is TagLib's own codec description ("MPEG Version 1 Audio,
    // Layer 3 VBR"), which ContentType/Suffix cannot reconstruct: both .m4a alac
    // and .m4a aac are audio/mp4. Flower-specific, same as PlayCounts above.
    int? SamplingRate = null,
    int? ChannelCount = null,
    int? BitDepth = null,
    string? Codec = null,
    // The file's path relative to the library folder it was found under -
    // "Angine de Poitrine/Vol.II/01 Fabienk.mp3", never the absolute path and
    // never the root itself. This is the spec's own Child.path in everything but
    // name; it is sent as "relativePath" rather than "path" because "path" on a
    // Flower Track means the absolute local one, and one field meaning both
    // things is the kind of confusion the rest of this file exists to avoid.
    //
    // This is the one thing SYNC-PLAN.md's Path-can't-cross-the-wire rule does
    // not cover. The rule protects the serving machine's layout - where its
    // music lives, and therefore who the user is and how their disks are
    // arranged. The part under the root is not layout, it is the library's own
    // organization, and the receiving side already knows every tag it was built
    // from.
    //
    // What needs it is the download: a saved file used to be named after the
    // receiver's own Track.Id ("904740018a1d4b22bdb45d9a9b84c7fb.mp3" in a flat
    // folder), which is unrecognisable to anything but Flower. With this, a
    // download reproduces the origin's own tree under the download folder. See
    // Track.OriginRelativePath and LibraryDownloadService.
    string? RelativePath = null);

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
