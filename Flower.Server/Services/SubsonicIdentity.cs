using System.Security.Cryptography;
using System.Text;

namespace Flower.Server.Services;

// Deterministic ids for the artist/album groupings TrackEntity denormalizes
// (see TrackEntity's doc comment) - same normalize-then-hash shape as
// Flower.Core's Track.SyncKey, for the same reason: a stable identity
// derived purely from the string fields themselves needs no reconciliation
// step to stay consistent across rescans.
public static class SubsonicIdentity
{
    public static string ArtistId(string? artistName) =>
        "ar-" + Hash(Normalize(artistName));

    public static string AlbumId(string? artistName, string? albumName) =>
        "al-" + Hash(Normalize(artistName) + "|" + Normalize(albumName));

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
