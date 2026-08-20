using System;
using System.Security.Cryptography;
using System.Text;

using Flower.Models;

namespace Flower.Services;

// The one and only (artist, album) -> id function in the codebase, shared by
// the embedded peer-to-peer host (LibraryOpenSubsonicMapper) and the
// standalone server (LibraryImportService/SubsonicEndpoints).
//
// There used to be two, differing by a punctuation character and an argument
// order: the client built "al:{album}|{artist}" from raw strings while the
// server built "al-{hash}" - and nothing stopped either from drifting further
// (see ARCHITECTURE-REVIEW Tier 2.1). The hashed form wins because it is
// opaque: the plain-text form embedded user data - including the "|"
// separator itself - straight into an id that travels in URLs, so an album
// literally named "A|B" could collide with a different artist/album pair.
//
// Argument order is (albumArtist, album), matching how these are always
// grouped. Callers must pass Track.EffectiveAlbumArtist, never the per-track
// Artists: a compilation has one album artist and many track artists, so
// using the latter fragments one album into one id per track and makes the
// grouped id unreachable. That mismatch was a live bug on the cover-art path
// - art requests for any album with an AlbumArtists tag asked for an id the
// serving side had never handed out, and quietly 404'd.
public static class SubsonicIdentity
{
    public static string ArtistId(string? albumArtist) =>
        "ar-" + Hash(Normalize(albumArtist));

    public static string AlbumId(string? albumArtist, string? album) =>
        "al-" + Hash(Normalize(albumArtist) + "|" + Normalize(album));

    // The grouping key for one track, in one place. An album is identified by
    // its *album* artist, never the per-track Artists - getting that wrong
    // silently 404'd cover art for every compilation (ARCHITECTURE-REVIEW Tier
    // 2.1). Every caller that groups tracks into albums goes through this:
    // the client's own embedded sync server (LibraryOpenSubsonicMapper) and
    // the standalone server's resident snapshot (LibrarySnapshot.Build).
    public static string AlbumIdFor(Track track) => AlbumId(track.EffectiveAlbumArtist, track.Album);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    // Truncated the same way, and for the same reason, as
    // SignedRequestCanonicalizer.ComputeFingerprint: long enough that an
    // accidental collision across a personal library is not a real concern,
    // short enough to stay readable in a URL and a log line.
    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
