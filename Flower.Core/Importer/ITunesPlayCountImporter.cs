using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Models;

namespace Flower.Importer;

// Applies Music.app play counts; snapshot acquisition and matching are shared.
public static class ITunesPlayCountImporter
{
    public static bool ApplyFromXmlFile(IEnumerable<Track> tracks, string xmlPath, ILogger? logger = null) =>
        ITunesSnapshot.FromXmlFile(xmlPath, logger) is { } snapshot && Apply(tracks, snapshot, logger);

    public static bool Apply(IEnumerable<Track> tracks, ITunesSnapshot snapshot, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var matchedCount = 0;
        var changedCount = 0;
        foreach (var track in tracks)
        {
            var plays = snapshot.Plays.Find(track);

            if (plays is not { } value)
                continue;

            // Upwards only, as everywhere else these two are kept
            // (Library.ApplyReportedOwnerState, Library.MergeListening):
            // the library holds the highest count any of the owner's Macs
            // read, so a Mac whose Music.app has fewer - or only skips,
            // which reads as no plays - must not walk it back.
            var playCount = Math.Max(track.ImportedPlayCount, value.Count);
            var skipCount = Math.Max(track.ImportedSkipCount, value.Skips);
            var changed = track.ImportedPlayCount != playCount || track.ImportedSkipCount != skipCount;
            track.ImportedPlayCount = playCount;
            track.ImportedSkipCount = skipCount;
            // Later only: LastPlayedAt is a high-water mark everywhere it
            // is kept (Library.ApplyReportedOwnerState), so Music.app's
            // date fills in a song Flower has never played and moves one
            // that Music.app played since, and never walks back a listen
            // Flower recorded. Moved here, it goes to the server as owner
            // state the way the count does.
            if (value.LastPlayedAt is { } musicAppPlayed && (track.LastPlayedAt is not { } flowerPlayed || musicAppPlayed > flowerPlayed))
            {
                track.LastPlayedAt = musicAppPlayed;
                changed = true;
            }
            matchedCount++;
            if (changed)
                changedCount++;
        }

        logger.LogInformation("iTunes play count sync matched {MatchedCount} of {ExportedCount} exported tracks, {ChangedCount} changed", matchedCount, snapshot.Plays.Count, changedCount);
        return changedCount > 0;
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
