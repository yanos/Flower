using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

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
            // Highest-priority match - see TryDecodeLocationPath's own doc
            // comment for why a path match, when available, beats metadata
            // entirely (confirmed against a real track whose Artist tag had
            // been edited - "Takashi Kokubo (小久保隆)" locally vs "Takashi
            // Kokubo" in Music.app's stale record - where Location still
            // pointed at the exact same file).
            var dateAddedByPath = new Dictionary<string, DateTimeOffset>();
            // Fallback for when BuildSyncKey finds nothing - see Track.
            // BuildLooseKey's own doc comment. Tracks how many *distinct* full
            // sync keys (i.e. distinct durations) share a loose key - two raw
            // XML entries for the same file at the same duration (already
            // merged above) don't make a loose key ambiguous, only two
            // genuinely different durations do.
            var looseSyncKeys = new Dictionary<string, HashSet<string>>();
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

                    // Rounded, not truncated - see Track.SyncKey's own doc comment
                    // for the real-world boundary case (171.96s vs 172.01s) this
                    // fixes; both sides of the match must round the same way.
                    var syncKey = Track.BuildSyncKey(
                        nameNode.ToString(),
                        artistNode?.ToString(),
                        albumNode?.ToString(),
                        (int)Math.Round(totalTimeMs.ToInt() / 1000.0));
                    var looseKey = Track.BuildLooseKey(nameNode.ToString(), artistNode?.ToString(), albumNode?.ToString());

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

                    if (!looseSyncKeys.TryGetValue(looseKey, out var syncKeys))
                        looseSyncKeys[looseKey] = syncKeys = new HashSet<string>();
                    syncKeys.Add(syncKey);

                    iTunesTrack.TryGetValue("Location", out var locationNode);
                    if (TryDecodeLocationPath(locationNode?.ToString()) is { } path &&
                        (!dateAddedByPath.TryGetValue(path, out var existingByPath) || candidate < existingByPath))
                        dateAddedByPath[path] = candidate;
                }
            }

            var matchedCount = 0;
            foreach (var track in tracks)
            {
                DateTimeOffset? candidate = null;
                if (!string.IsNullOrEmpty(track.Path) && dateAddedByPath.TryGetValue(NormalizePath(track.Path), out var byPath))
                    candidate = byPath;
                else if (dateAddedBySyncKey.TryGetValue(track.SyncKey, out var exact))
                    candidate = exact;
                else if (looseSyncKeys.TryGetValue(Track.BuildLooseKey(track.Title, track.Artists, track.Album), out var syncKeys) && syncKeys.Count == 1)
                    candidate = dateAddedBySyncKey[syncKeys.Single()];

                if (candidate is { } value && value < track.DateAdded)
                {
                    track.DateAdded = value;
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

    // An iTunes entry's own file path, when it resolves and matches a local
    // Track.Path exactly, is a stronger identity signal than any tag-derived
    // key: metadata (title/artist/album/duration) can drift after Music.app
    // last indexed a file - see the Takashi Kokubo example above - or, per
    // ITunesPlayCountImporter's own class comment, a *static* export's paths
    // can predate an iTunes-to-Music.app migration and no longer resolve at
    // all. Either way this fallback degrades safely: a stale/non-matching
    // path here just means the metadata-based keys below get tried instead,
    // exactly as if this dictionary had never been built. Same mechanism as
    // ITunesPlayCountImporter.TryDecodeLocationPath, identical here.
    private static string? TryDecodeLocationPath(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return null;
        try
        {
            var uri = new Uri(location);
            return uri.IsFile ? NormalizePath(uri.LocalPath) : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // Confirmed necessary against a real file whose name contains "é": iTunes'
    // Location URL had it as the decomposed form ("e" + a combining acute
    // accent, U+0301) while the same path read off disk elsewhere used the
    // precomposed single-codepoint form (U+00E9) - visually identical, but a
    // byte-for-byte dictionary lookup treated them as two different strings
    // and the path match silently found nothing. Both sides of the path
    // match (this dictionary's keys and the local Track.Path used to look
    // them up) have to go through this same normalization or the mismatch
    // just resurfaces in the opposite direction.
    private static string NormalizePath(string path) => path.Normalize(NormalizationForm.FormC);

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
