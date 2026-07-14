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

// Reads per-track "Play Count" from Music.app and applies it to
// Track.ImportedPlayCount. Deliberately leaves Track.PlayCount (Flower's own
// count, incremented by PlaylistControlViewModel) untouched -
// TrackRowViewModel.PlayCountDisplay is what sums the two for display, so
// re-running (or disabling) this import can never lose plays Flower itself
// recorded.
//
// Primary mechanism: Music.app's AppleScript dictionary exposes a genuine
// "export" command ("tell application \"Music\" to export source 1 as XML
// to ...", confirmed present in its .sdef and tested against a real, large
// library in ~5 seconds) that generates a fresh XML snapshot on demand -
// written to Flower's own app-data folder, not the user's Music folder.
// First use triggers a one-time macOS "Flower wants to control Music.app"
// automation permission prompt. Falls back to ResolveLibraryXmlPath's static
// detection (a pre-existing file from a previous manual "File > Library >
// Export Library..." or, on some machines, a much older classic-iTunes
// export) if the live export fails - Music.app not installed, automation
// permission denied, etc.
//
// Matches by Track.SyncKey (Title/Artist/Album/duration - the same identity
// key already used to match tracks across devices in the WiFi sync feature),
// not file path: confirmed against a real, years-old classic-iTunes export
// that its "Location" paths pointed at the old ~/Music/iTunes/iTunes Music/...
// layout, while the same files had long since moved to Music.app's
// ~/Music/Music/Media.localized/... after Apple's iTunes-to-Music.app
// migration - identical files, completely different paths, so path matching
// alone silently matched nothing for anyone in that (very common) situation.
public static class ITunesPlayCountImporter
{
    private static string LiveExportPath => Path.Combine(AppDataDirectory.Path, "itunes-library-export.xml");

    // A static class can't take constructor-injected ILogger<T> the way the
    // rest of the app does (see AppLogging.CreateTypedLogger's callers) - this
    // is the equivalent for a static entry point: the caller's own already-
    // DI-injected logger flows in as a parameter (see MainViewModel.SyncITunesPlayCountAsync)
    // instead of a static field resolved from a global. Optional/defaulting to
    // a no-op logger purely so the many test call sites that don't care about
    // log output don't all need updating.
    public static void Apply(IEnumerable<Track> tracks, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        if (!OperatingSystem.IsMacOS())
            return;

        var usedLiveExport = TryExportFreshLibraryXml(logger);
        var xmlPath = usedLiveExport ? LiveExportPath : ResolveLibraryXmlPath();
        if (xmlPath == null)
        {
            logger.LogInformation("iTunes play count sync skipped: no library export available (Music.app not installed, automation denied, or no static export found)");
            return;
        }

        logger.LogInformation("Syncing play counts from {Source}: {XmlPath}", usedLiveExport ? "live Music.app export" : "static library export", xmlPath);
        ApplyFromXmlFile(tracks, xmlPath, logger);
    }

    // The actual parse-and-match logic, split out from Apply() so tests can
    // exercise it against a specific synthetic XML file without the live
    // export (which runs first in Apply() and wins on any machine that
    // actually has Music.app installed, including the one this was developed
    // on) getting in the way.
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

            var playCountBySyncKey = new Dictionary<string, int>();
            // Highest-priority match - see TryDecodeLocationPath's own doc
            // comment for why a path match, when available, beats metadata
            // entirely.
            var playCountByPath = new Dictionary<string, int>();
            // Fallback for when BuildSyncKey finds nothing - see Track.
            // BuildLooseKey's own doc comment. Tracks how many *distinct* full
            // sync keys (i.e. distinct durations) share a loose key - two raw
            // XML entries for the same file at the same duration (already
            // summed above) don't make a loose key ambiguous, only two
            // genuinely different durations do.
            var looseSyncKeys = new Dictionary<string, HashSet<string>>();
            foreach (var entry in iTunesTracks.Values)
            {
                if (entry is not NSDictionary iTunesTrack)
                    continue;
                if (iTunesTrack.TryGetValue("Name", out var nameNode) &&
                    iTunesTrack.TryGetValue("Total Time", out var totalTimeNode) &&
                    totalTimeNode is NSNumber totalTimeMs &&
                    iTunesTrack.TryGetValue("Play Count", out var playCountNode) &&
                    playCountNode is NSNumber playCount)
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

                    // Sum rather than overwrite: a library that's been through a
                    // merge/duplicate-import can have more than one Music.app
                    // entry for the exact same file (confirmed against a real
                    // library - two "Wishwanderer" entries, same Location, play
                    // counts 19 and 1) - all of them collapse to the same
                    // SyncKey since Flower only keeps one Track per file, so
                    // overwriting silently discarded whichever entry's play
                    // count didn't happen to be enumerated last.
                    playCountBySyncKey[syncKey] =
                        playCountBySyncKey.GetValueOrDefault(syncKey) + playCount.ToInt();

                    if (!looseSyncKeys.TryGetValue(looseKey, out var syncKeys))
                        looseSyncKeys[looseKey] = syncKeys = new HashSet<string>();
                    syncKeys.Add(syncKey);

                    iTunesTrack.TryGetValue("Location", out var locationNode);
                    if (TryDecodeLocationPath(locationNode?.ToString()) is { } path)
                        playCountByPath[path] = playCountByPath.GetValueOrDefault(path) + playCount.ToInt();
                }
            }

            var matchedCount = 0;
            foreach (var track in tracks)
            {
                int? count = null;
                if (!string.IsNullOrEmpty(track.Path) && playCountByPath.TryGetValue(NormalizePath(track.Path), out var byPath))
                    count = byPath;
                else if (playCountBySyncKey.TryGetValue(track.SyncKey, out var exact))
                    count = exact;
                else if (looseSyncKeys.TryGetValue(Track.BuildLooseKey(track.Title, track.Artists, track.Album), out var syncKeys) && syncKeys.Count == 1)
                    count = playCountBySyncKey[syncKeys.Single()];

                if (count is { } value)
                {
                    track.ImportedPlayCount = value;
                    matchedCount++;
                }
            }

            logger.LogInformation("iTunes play count sync matched {MatchedCount} of {ExportedCount} exported tracks", matchedCount, playCountBySyncKey.Count);
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable/unexpected-shape XML - leave ImportedPlayCount
            // as-is rather than blocking startup over an optional enrichment step.
            logger.LogWarning(ex, "Failed to parse iTunes library export at {XmlPath}; play counts left unchanged", xmlPath);
        }
    }

    // An iTunes entry's own file path, when it resolves and matches a local
    // Track.Path exactly, is a stronger identity signal than any tag-derived
    // key: metadata (title/artist/album/duration) can drift after Music.app
    // last indexed a file - confirmed against a real track whose Artist tag
    // had been edited ("Takashi Kokubo (小久保隆)" locally vs "Takashi Kokubo"
    // in Music.app's stale record) where Location still pointed at the exact
    // same file - or, per this class's own doc comment, a *static* export's
    // paths can predate an iTunes-to-Music.app migration and no longer
    // resolve at all. Either way this fallback degrades safely: a stale/non-
    // matching path here just means the metadata-based keys below get tried
    // instead, exactly as if this dictionary had never been built.
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

    // Runs synchronously (this is always called from a background Task.Run at
    // both its call sites - App.axaml.cs's startup rescan and
    // MainViewModel.SyncPlayCountFromITunes's apply-immediately-on-toggle - so
    // blocking here doesn't freeze the UI). A generous timeout covers large
    // libraries without hanging forever if Music.app is unresponsive.
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
            // Expected/handled - Apply() falls back to a static export if one
            // exists. Debug rather than Warning since this is routine on a
            // machine without Music.app or without automation permission granted.
            logger.LogDebug(ex, "Live Music.app export failed; will fall back to a static export if one exists");
            return false;
        }
    }

    // Fallback for when the live AppleScript export isn't available (Music.app
    // not installed, automation permission denied, etc.) - both classic iTunes
    // and Music.app (when "Share Library XML..." was enabled, on macOS
    // versions old enough to still have that checkbox) can have written one of
    // these, under a couple of different names/folders depending on version.
    // Music.app's own naming is checked first and wins if more than one
    // exists, since a leftover classic-iTunes file (like the one this was
    // originally confirmed against) is frozen at whatever it was last
    // generated rather than actively maintained. Public so SettingsWindow can
    // show the user exactly which file (if any) this would fall back to.
    public static string? ResolveLibraryXmlPath()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var musicRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

        foreach (var candidate in new[]
        {
            Path.Combine(musicRoot, "Music", "Music Library.xml"),
            Path.Combine(musicRoot, "iTunes", "iTunes Music Library.xml"),
            Path.Combine(musicRoot, "iTunes", "iTunes Library.xml"),
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
