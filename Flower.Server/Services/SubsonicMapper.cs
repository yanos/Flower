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

    // selfFingerprint is this server's own DeviceIdentity.Fingerprint, and is
    // what makes the PlayCounts field below meaningful - it names whose tally
    // the count is. Optional because only the callers whose response a Flower
    // client *merges into its own library* need it: GET /api/flower/v1/library
    // (see SyncEndpoints). The /rest browse endpoints pass nothing and send no
    // counts, which is what they did before and what a third-party Subsonic
    // client expects; a Flower client pulls its catalog through the bulk route,
    // not through those.
    // libraryRoots is the server's configured LibraryPaths
    // (FlowerServerOptions.LibraryPaths, re-read per request the same way the
    // rescan re-reads it) - what RelativePath below is relative *to*. Optional
    // only so a test can map a track without a folder configuration; every
    // caller in the server passes it.
    public static Child ToChild(Track track, string? selfFingerprint = null, IReadOnlyList<string>? libraryRoots = null)
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
            DateAdded: track.DateAdded,
            // The same snapshot the app's own LibraryOpenSubsonicMapper.ToChild
            // sends, and deliberately the same expression: this server's own
            // tally under its own name, plus every other device's count it has
            // learned. It sent none at all until now, which meant a browser tab
            // could report a play here (see IPlayReporter) and then never see
            // it again - the count was stored and never served, so the next tab
            // showed an empty Plays column for a track it had just played.
            PlayCounts: selfFingerprint == null
                ? null
                : new Dictionary<string, int>(track.RemotePlayCounts)
                {
                    [selfFingerprint] = track.PlayCount + track.ImportedPlayCount,
                },
            LastPlayed: track.LastPlayedAt,
            // The same albumArtist already used for AlbumId/ArtistId/CoverArt
            // above, now also sent as a value - see Child.DisplayAlbumArtist for
            // what a receiving head could not reconstruct without it.
            DisplayAlbumArtist: albumArtist,
            IsCompilation: track.IsCompilation,
            // See Child.SamplingRate - null rather than 0 for anything the
            // scan did not produce, so the receiver keeps its own "unset"
            // instead of storing a confident zero.
            SamplingRate: track.SampleRate > 0 ? track.SampleRate : null,
            ChannelCount: track.Channels > 0 ? track.Channels : null,
            BitDepth: track.BitsPerSample > 0 ? track.BitsPerSample : null,
            Codec: track.Codec,
            RelativePath: RelativePathOf(track, libraryRoots),
            // See Child.SortTitle/RememberPlaybackPosition - the same two
            // groups the app's own LibraryOpenSubsonicMapper.ToChild sends, and
            // deliberately the same expressions: a client pulling its catalog
            // from this server must not get a thinner track than one pulling it
            // from a desktop head.
            SortTitle: track.TitleSort,
            SortArtist: track.ArtistsSort,
            SortAlbum: track.AlbumSort,
            SortComposer: track.ComposersSort,
            RememberPlaybackPosition: track.RememberPlaybackPosition,
            ResumePositionSeconds: track.ResumePosition is { } resume ? (int)resume.TotalSeconds : null,
            IgnoreWhenShuffling: track.IgnoreWhenShuffling,
            VolumeAdjustment: track.VolumeAdjustment,
            EncoderProfile: track.EncoderProfile);
    }

    // The part of the file's path below whichever configured library folder it
    // was found under, with the separator normalized to '/' so a Windows server
    // and a Unix client agree on what the tree looks like. See
    // Child.RelativePath for why only this part travels.
    //
    // Longest matching root wins, for the nested case (/music and
    // /music/lossless both configured): the shorter one would prepend
    // "lossless/" to a file the deeper root already accounts for. A path under
    // no configured root at all - a file scanned before a folder was removed,
    // or one adopted from Music.app - keeps its bare file name, which is still
    // better than the receiver's own track id and still says nothing about
    // where the file sits.
    public static string? RelativePathOf(Track track, IReadOnlyList<string>? libraryRoots)
    {
        if (track.Path is not { } path)
            return null;

        var best = "";
        foreach (var root in libraryRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length <= best.Length)
                continue;
            if (path.StartsWith(normalized + Path.DirectorySeparatorChar, PathComparison))
                best = normalized;
        }

        var relative = best.Length > 0 ? path[(best.Length + 1)..] : Path.GetFileName(path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    // Case-insensitive off Linux, matching how Importer's own seen-files set
    // and Library.UpdateTracks' path dictionary already compare paths.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

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
