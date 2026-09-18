using System;
using System.Collections.Generic;
using System.IO;

using Flower.Models;

namespace Flower.Services;

// A Track as it leaves this device: the one Track -> TrackDto mapping in the
// codebase, and what fills GET /api/flower/v1/library.
//
// It lived in Flower.Server as LibraryDtoMapper.ToTrackDto, which put Flower's own
// catalog mapping inside the OpenSubsonic adapter and made the sync manifest
// look like a by-product of a protocol it does not speak. The direction is the
// other way round: this produces the catalog, and the adapter reshapes what
// this produced. In Flower.Core so the server and the browser head reach the
// same one - the app already shares Track, LibrarySnapshot and CatalogIdentity
// with it, and this is the last piece of that path that was stranded.
//
// A field missing here is a field missing from every synced placeholder on
// every client, which is what happened to the technical fields for a whole
// release; LibraryDtoMapperTests is what holds each of them down.
public static class LibraryDtoMapper
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
    public static TrackDto ToTrackDto(Track track, string? selfFingerprint = null, IReadOnlyList<string>? libraryRoots = null)
    {
        var albumArtist = track.EffectiveAlbumArtist;
        var suffix = SuffixOf(track);

        return new TrackDto(
            Id: track.Id.ToKey(),
            Title: track.Title ?? (track.Path is null ? "" : Path.GetFileNameWithoutExtension(track.Path)),
            Album: track.Album,
            Artist: track.Artists,
            AlbumId: CatalogIdentity.AlbumId(albumArtist, track.Album),
            ArtistId: CatalogIdentity.ArtistId(albumArtist),
            Track: track.TrackNumber > 0 ? (int)track.TrackNumber : null,
            Year: ParseYear(track.Year),
            Genre: track.Genre,
            // Size, Suffix and ContentType are derived from the file and its
            // path here rather than stored as columns. They used to be three
            // TrackEntity fields stamped at import time, which meant they
            // described the file as it was when last scanned; a stat at map
            // time describes it as it is now, and keeps three columns the
            // client would never fill out of the shared schema. Bounded work:
            // the endpoints that map a TrackDto return one album, one playlist or
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
            CoverArt: CatalogIdentity.AlbumId(albumArtist, track.Album),
            Starred: track.Starred,
            DateAdded: track.DateAdded,
            // This server's own tally under its own name, plus every other
            // device's count it has learned. It sent none at all until a
            // browser tab
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
            // above, now also sent as a value - see TrackDto.DisplayAlbumArtist for
            // what a receiving head could not reconstruct without it.
            DisplayAlbumArtist: albumArtist,
            IsCompilation: track.IsCompilation,
            // See TrackDto.SamplingRate - null rather than 0 for anything the
            // scan did not produce, so the receiver keeps its own "unset"
            // instead of storing a confident zero.
            SamplingRate: track.SampleRate > 0 ? track.SampleRate : null,
            ChannelCount: track.Channels > 0 ? track.Channels : null,
            BitDepth: track.BitsPerSample > 0 ? track.BitsPerSample : null,
            Codec: track.Codec,
            RelativePath: RelativePathOf(track, libraryRoots),
            // See TrackDto.SortTitle/RememberPlaybackPosition. These reach
            // another device only here: they are not in the file, so a client
            // that holds the catalog but not the bytes has no other way to
            // learn them.
            SortTitle: track.TitleSort,
            SortArtist: track.ArtistsSort,
            SortAlbum: track.AlbumSort,
            SortComposer: track.ComposersSort,
            RememberPlaybackPosition: track.RememberPlaybackPosition,
            ResumePositionSeconds: track.ResumePosition is { } resume ? (int)resume.TotalSeconds : null,
            IgnoreWhenShuffling: track.IgnoreWhenShuffling,
            VolumeAdjustment: track.VolumeAdjustment,
            EncoderProfile: track.EncoderProfile,
            DiscNumber: track.DiscNumber > 0 ? (int)track.DiscNumber : null,
            DiscCount: track.DiscCount > 0 ? (int)track.DiscCount : null,
            StarredAt: track.Starred ? track.StarredAt : null);
    }

    // The part of the file's path below whichever configured library folder it
    // was found under, with the separator normalized to '/' so a Windows server
    // and a Unix client agree on what the tree looks like. See
    // TrackDto.RelativePath for why only this part travels.
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

        var bestLength = 0;
        foreach (var root in libraryRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var normalized = root.TrimEnd(Separators);
            if (normalized.Length <= bestLength)
                continue;
            if (path.Length > normalized.Length
                && IsSeparator(path[normalized.Length])
                && path.AsSpan(0, normalized.Length).Equals(normalized, PathComparison))
                bestLength = normalized.Length;
        }

        var relative = bestLength > 0 ? path[(bestLength + 1)..] : Path.GetFileName(path);
        return relative.Replace('\\', '/');
    }

    // Both separators, on every platform. A Windows server's own paths use
    // '\\', but a root typed into its configuration (or carried over from a
    // config written elsewhere) may use either, and a root that fails to match
    // is not an error - it silently drops the track's whole folder tree and
    // sends the bare file name instead.
    private static readonly char[] Separators = ['/', '\\'];

    private static bool IsSeparator(char c) => c is '/' or '\\';

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

    private static int? ParseYear(string? year) => int.TryParse(year, out var parsed) ? parsed : null;
}
