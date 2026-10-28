using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Flower.Services;

// Artists' pictures on disk, by the artist's normalised name
// (MetadataNames.Normalize), and the artists that have none: the cache
// beside the album-art cache that docs/ONLINE-METADATA-PLAN.md puts them in.
// Used on both ends - by the engine, wherever it runs, for what it found
// online, and by a paired device for what its server answered (see
// ServerOnlineMetadata) - with a different lifetime for a miss on each.
//
// A miss is remembered so that an artist nobody has a picture of is not
// searched for again every time their row scrolls past. It is forgotten after
// missLifetime, because a store's catalogue grows. A picture found is kept
// until the cache is cleared.
public sealed class ArtistPictureCache(string directory, TimeSpan missLifetime, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public enum State
    {
        // Never asked, or a miss old enough to ask again.
        Unknown,
        Found,
        None,
    }

    public (State State, byte[]? Picture) Read(string key)
    {
        try
        {
            var picture = PathFor(key, PictureExtension);
            if (File.Exists(picture))
                return (State.Found, File.ReadAllBytes(picture));

            var miss = PathFor(key, MissExtension);
            if (!File.Exists(miss))
                return (State.Unknown, null);

            // The time it was looked for is written in the file rather than
            // read off its timestamp, so a clock a test moves is the one
            // that decides.
            if (long.TryParse(File.ReadAllText(miss), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && _clock.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero) < missLifetime)
                return (State.None, null);

            File.Delete(miss);
            return (State.Unknown, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (State.Unknown, null);
        }
    }

    public void WriteFound(string key, byte[] picture)
    {
        Write(PathFor(key, PictureExtension), picture);
        TryDelete(PathFor(key, MissExtension));
    }

    public void WriteNone(string key) =>
        Write(PathFor(key, MissExtension),
            Encoding.ASCII.GetBytes(_clock.GetUtcNow().UtcTicks.ToString(CultureInfo.InvariantCulture)));

    private const string PictureExtension = ".art";
    private const string MissExtension = ".none";

    // Hashed, because a name is not a file name: "AC/DC", "Sigur Rós" and a
    // name longer than a filesystem allows all have to become one.
    private string PathFor(string key, string extension) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + extension);

    // Through a file of its own, so a reader never meets half a picture -
    // the server answers several devices at once, from the same cache.
    private void Write(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept, so it is looked up again next time - which costs a
            // request, not a picture.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
