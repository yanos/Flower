using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Claunia.PropertyList;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Importer;

// Reads per-track "Date Added" from Music.app and applies it to Track.DateAdded
// - the counterpart to ITunesPlayCountImporter (same live-export/static-fallback
// mechanism, same Track.SyncKey matching - see that class's doc comment for why
// either of those work the way they do), but for the "Added" column instead of
// play counts, and independently toggleable (AppSettings.SyncDateAddedFromITunes).
//
// Conflict rule: the OLDER of the two dates wins, never the newer. Flower's own
// Track.DateAdded already means "the earliest point this file is known to have
// entered any library this device has seen" (see Library.UpdateTracks, which
// carries it forward across rescans) - a later iTunes date is never more correct
// than that, but an earlier one usually means Music.app's own record predates
// whatever first triggered Flower's own DateAdded (a fresh install, a library
// path added after the fact, etc.) and should win.
public static class ITunesDateAddedImporter
{
    // Deliberately the same file ITunesPlayCountImporter exports to - both
    // importers read the one Music.app export, just different fields out of it.
    // Each does its own independent live-export attempt rather than sharing one
    // in-flight result, since the two sync toggles are independent and either
    // can run without the other - a modest, accepted duplicate-export cost
    // (~5s) on the rare launch where both happen to be enabled.
    private static string LiveExportPath => Path.Combine(AppDataDirectory.Path, "itunes-library-export.xml");

    public static void Apply(IEnumerable<Track> tracks, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        if (!OperatingSystem.IsMacOS())
            return;

        var usedLiveExport = TryExportFreshLibraryXml(logger);
        var xmlPath = usedLiveExport ? LiveExportPath : ITunesPlayCountImporter.ResolveLibraryXmlPath();
        if (xmlPath == null)
        {
            logger.LogInformation("iTunes date added sync skipped: no library export available (Music.app not installed, automation denied, or no static export found)");
            return;
        }

        logger.LogInformation("Syncing date added from {Source}: {XmlPath}", usedLiveExport ? "live Music.app export" : "static library export", xmlPath);
        ApplyFromXmlFile(tracks, xmlPath, logger);
    }

    // Split out from Apply() so tests can exercise it against a specific
    // synthetic XML file - see ITunesPlayCountImporter.ApplyFromXmlFile for why.
    public static void ApplyFromXmlFile(IEnumerable<Track> tracks, string xmlPath, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        try
        {
            if (PropertyListParser.Parse(xmlPath) is not NSDictionary root ||
                !root.TryGetValue("Tracks", out var tracksNode) ||
                tracksNode is not NSDictionary iTunesTracks)
            {
                logger.LogWarning("iTunes library export at {XmlPath} did not have the expected shape (missing/malformed Tracks dictionary)", xmlPath);
                return;
            }

            var dateAddedBySyncKey = new Dictionary<string, DateTimeOffset>();
            foreach (var entry in iTunesTracks.Values)
            {
                if (entry is not NSDictionary iTunesTrack)
                    continue;
                if (iTunesTrack.TryGetValue("Name", out var nameNode) &&
                    iTunesTrack.TryGetValue("Total Time", out var totalTimeNode) &&
                    totalTimeNode is NSNumber totalTimeMs &&
                    iTunesTrack.TryGetValue("Date Added", out var dateAddedNode) &&
                    dateAddedNode is NSDate dateAdded)
                {
                    iTunesTrack.TryGetValue("Artist", out var artistNode);
                    iTunesTrack.TryGetValue("Album", out var albumNode);

                    var syncKey = Track.BuildSyncKey(
                        nameNode.ToString(),
                        artistNode?.ToString(),
                        albumNode?.ToString(),
                        totalTimeMs.ToInt() / 1000);

                    var raw = dateAdded.Date;
                    var candidate = new DateTimeOffset(raw.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(raw, DateTimeKind.Utc)
                        : raw.ToUniversalTime());

                    // Oldest wins here too, same as the final merge against each
                    // Track's existing DateAdded below - a library that's been
                    // through a merge/duplicate-import can have more than one
                    // Music.app entry for the same file (see
                    // ITunesPlayCountImporter's identical duplicate-entry note),
                    // and the earliest of those is the correct "first added" date.
                    if (!dateAddedBySyncKey.TryGetValue(syncKey, out var existing) || candidate < existing)
                        dateAddedBySyncKey[syncKey] = candidate;
                }
            }

            var matchedCount = 0;
            foreach (var track in tracks)
            {
                if (dateAddedBySyncKey.TryGetValue(track.SyncKey, out var candidate) && candidate < track.DateAdded)
                {
                    track.DateAdded = candidate;
                    matchedCount++;
                }
            }

            logger.LogInformation("iTunes date added sync updated {MatchedCount} of {ExportedCount} exported tracks to an older date", matchedCount, dateAddedBySyncKey.Count);
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable/unexpected-shape XML - leave DateAdded as-is
            // rather than blocking startup over an optional enrichment step.
            logger.LogWarning(ex, "Failed to parse iTunes library export at {XmlPath}; date added left unchanged", xmlPath);
        }
    }

    // Same mechanism as ITunesPlayCountImporter.TryExportFreshLibraryXml - see
    // its own doc comment for the AppleScript/timeout details, identical here.
    private static bool TryExportFreshLibraryXml(ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory.Path);
            var script = $"tell application \"Music\" to export source 1 as XML to (POSIX file \"{LiveExportPath}\")";
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process == null)
                return false;
            return process.WaitForExit(60_000) && process.ExitCode == 0 && File.Exists(LiveExportPath);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Live Music.app export failed; will fall back to a static export if one exists");
            return false;
        }
    }
}
