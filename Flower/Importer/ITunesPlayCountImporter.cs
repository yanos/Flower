using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Claunia.PropertyList;

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

    public static void Apply(IEnumerable<Track> tracks)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var xmlPath = TryExportFreshLibraryXml() ? LiveExportPath : ResolveLibraryXmlPath();
        if (xmlPath == null)
            return;

        ApplyFromXmlFile(tracks, xmlPath);
    }

    // The actual parse-and-match logic, split out from Apply() so tests can
    // exercise it against a specific synthetic XML file without the live
    // export (which runs first in Apply() and wins on any machine that
    // actually has Music.app installed, including the one this was developed
    // on) getting in the way.
    public static void ApplyFromXmlFile(IEnumerable<Track> tracks, string xmlPath)
    {
        try
        {
            if (PropertyListParser.Parse(xmlPath) is not NSDictionary root ||
                !root.TryGetValue("Tracks", out var tracksNode) ||
                tracksNode is not NSDictionary iTunesTracks)
                return;

            var playCountBySyncKey = new Dictionary<string, int>();
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

                    var syncKey = Track.BuildSyncKey(
                        nameNode.ToString(),
                        artistNode?.ToString(),
                        albumNode?.ToString(),
                        totalTimeMs.ToInt() / 1000);

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
                }
            }

            foreach (var track in tracks)
            {
                if (playCountBySyncKey.TryGetValue(track.SyncKey, out var count))
                    track.ImportedPlayCount = count;
            }
        }
        catch
        {
            // Corrupt/unreadable/unexpected-shape XML - leave ImportedPlayCount
            // as-is rather than blocking startup over an optional enrichment step.
        }
    }

    // Runs synchronously (this is always called from a background Task.Run at
    // both its call sites - App.axaml.cs's startup rescan and
    // MainViewModel.SyncPlayCountFromITunes's apply-immediately-on-toggle - so
    // blocking here doesn't freeze the UI). A generous timeout covers large
    // libraries without hanging forever if Music.app is unresponsive.
    private static bool TryExportFreshLibraryXml()
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
        catch
        {
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
