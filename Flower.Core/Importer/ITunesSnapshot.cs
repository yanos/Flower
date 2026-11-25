using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Claunia.PropertyList;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Importer;

// One export and parse per import pass. Each field keeps its own duplicate
// merge rule and candidate set, using the same location/exact/loose matcher.
public sealed class ITunesSnapshot
{
    internal ITunesMatchIndex<MusicAppPlays> Plays { get; } = new(MusicAppPlays.Combine);
    internal ITunesMatchIndex<DateTimeOffset> Dates { get; } = new((a, b) => a < b ? a : b);

    public static ITunesSnapshot? Load(ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        logger ??= NullLogger.Instance;
        DeleteAbandonedExports(logger);
        // Independent passes can overlap; they must never export over a file
        // another pass is reading, including after an AppleScript timeout.
        var export = ExportPath(Guid.NewGuid().ToString("N"));
        try
        {
            var live = TryExport(export, logger);
            var path = live ? export : ITunesPlayCountImporter.ResolveLibraryXmlPath();
            if (path == null)
            {
                logger.LogInformation("Music.app import skipped: no library export available (Music.app not installed, automation denied, or no static export found)");
                return null;
            }

            logger.LogInformation("Importing from Music.app's {Source}: {XmlPath}", live ? "live export" : "static library export", path);
            return FromXmlFile(path, logger);
        }
        finally
        {
            TryDelete(export, logger);
        }
    }

    private const string ExportPrefix = "itunes-";

    private static string ExportPath(string id) => Path.Combine(AppDataDirectory.Path, $"{ExportPrefix}{id}.xml");

    // An export whose AppleScript timed out can still be written by Music.app
    // after its pass deleted it, and then nothing would ever remove it. Any
    // export older than the longest a pass can take is one of those; a younger
    // one may be another pass's, still being read.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(10);

    private static void DeleteAbandonedExports(ILogger logger)
    {
        try
        {
            if (!Directory.Exists(AppDataDirectory.Path))
                return;

            var cutoff = DateTime.UtcNow - AbandonedAfter;
            foreach (var file in Directory.EnumerateFiles(AppDataDirectory.Path, $"{ExportPrefix}*.xml"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    TryDelete(file, logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not list old Music.app exports");
        }
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not remove Music.app export {Path}", path);
        }
    }

    public static ITunesSnapshot? FromXmlFile(string path, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        try
        {
            if (PropertyListParser.Parse(path) is not NSDictionary root ||
                !root.TryGetValue("Tracks", out var node) || node is not NSDictionary tracks)
            {
                logger.LogWarning("Music.app export at {Path} has no Tracks dictionary", path);
                return null;
            }
            var snapshot = new ITunesSnapshot();
            foreach (var entry in tracks.Values.OfType<NSDictionary>())
            {
                if (!entry.TryGetValue("Name", out var name) ||
                    !entry.TryGetValue("Total Time", out var time) || time is not NSNumber milliseconds)
                    continue;
                entry.TryGetValue("Artist", out var artist);
                entry.TryGetValue("Album", out var album);
                entry.TryGetValue("Location", out var location);
                var exact = Track.BuildSyncKey(name.ToString(), artist?.ToString(), album?.ToString(),
                    Track.RoundedSeconds(milliseconds.ToInt() / 1000.0));
                var loose = Track.BuildLooseKey(name.ToString(), artist?.ToString(), album?.ToString());
                int? plays = entry.TryGetValue("Play Count", out var p) && p is NSNumber pn ? pn.ToInt() : null;
                int? skips = entry.TryGetValue("Skip Count", out var s) && s is NSNumber sn ? sn.ToInt() : null;
                if (plays != null || skips != null)
                {
                    entry.TryGetValue("Play Date UTC", out var date);
                    snapshot.Plays.Add(exact, loose, location?.ToString(),
                        new MusicAppPlays(plays ?? 0, date is NSDate d ? Utc(d.Date) : null, skips ?? 0));
                }
                if (entry.TryGetValue("Date Added", out var added) && added is NSDate addedDate)
                    snapshot.Dates.Add(exact, loose, location?.ToString(), Utc(addedDate.Date));
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read Music.app export at {Path}", path);
            return null;
        }
    }

    private static DateTimeOffset Utc(DateTime raw) =>
        new(raw.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(raw, DateTimeKind.Utc) : raw.ToUniversalTime());

    private static bool TryExport(string path, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory.Path);
            var script = $"tell application \"Music\" to export source 1 as XML to (POSIX file \"{path}\")";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript", ArgumentList = { "-e", script }, UseShellExecute = false,
            });
            if (process == null)
                return false;
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }
            return process.ExitCode == 0 && File.Exists(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Live Music.app export failed; using a static export if available");
            return false;
        }
    }
}

internal sealed class ITunesMatchIndex<T>(Func<T, T, T> combine) where T : struct
{
    private readonly Dictionary<string, T> _exact = new();
    private readonly Dictionary<string, HashSet<string>> _loose = new();
    private readonly ITunesLocations<T> _locations = new(combine);
    public int Count => _exact.Count;

    public void Add(string exact, string loose, string? location, T value)
    {
        _exact[exact] = _exact.TryGetValue(exact, out var previous) ? combine(previous, value) : value;
        if (!_loose.TryGetValue(loose, out var keys))
            _loose[loose] = keys = new HashSet<string>();
        keys.Add(exact);
        _locations.Add(location, value);
    }

    public T? Find(Track track)
    {
        if (_locations.TryFind(track, out var location))
            return location;
        if (_exact.TryGetValue(track.SyncKey, out var exact))
            return exact;
        return _loose.TryGetValue(Track.BuildLooseKey(track.Title, track.Artists, track.Album), out var keys) && keys.Count == 1
            ? _exact[keys.Single()] : null;
    }
}

internal readonly record struct MusicAppPlays(int Count, DateTimeOffset? LastPlayedAt, int Skips)
{
    public static MusicAppPlays Combine(MusicAppPlays a, MusicAppPlays b) =>
        new(a.Count + b.Count, a.LastPlayedAt is null || b.LastPlayedAt > a.LastPlayedAt ? b.LastPlayedAt : a.LastPlayedAt, a.Skips + b.Skips);
}
