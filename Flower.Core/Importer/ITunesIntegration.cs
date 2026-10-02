using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Importer;

// The parts of the iTunes/Music.app integration that are the same on both
// hosts, in one place. The two importers underneath already lived in Flower.Core
// and were already shared; what sits on top of them - is there a folder to
// adopt, which of the two imports does a scan run - is here so the app and the
// server answer in the same words.
//
// *When* either happens is each host's own business and is not here: the server
// gates all of it on FlowerServerOptions.IntegrateWithITunes
// (LibraryImportService), and the app takes the folder only on a yes to its
// first-run question (ITunesLibraryOffer) and runs each import off its own
// switch.
//
// Static rather than a service: every method is a pure function of the settings
// it is handed plus what is on disk, and its callers reach it from places with
// no container to resolve out of (a scan on its own scope). Loggers come in as
// parameters for the same reason - see ITunesPlayCountImporter.Apply's own note
// on this.
public static class ITunesIntegration
{
    // Music.app's configured media folder, when the folder exists and is not
    // already a library path - i.e. exactly when a caller that wants it should
    // add it to `settings.LibraryPaths`. Null in every other case, including on
    // hosts with no Music.app at all.
    //
    // Returns the folder rather than adding it, because "add" is the caller's:
    // the server appends to the path list it is about to scan and writes that
    // back to flower-server.json, where it is a folder the owner can see and
    // remove.
    public static string? ResolveMediaFolderToAdopt(MusicLibrarySettings settings, ILogger? logger = null)
    {
        if (Importer.TryResolveAppleMusicFolder(logger) is not { } folder)
            return null;

        return settings.LibraryPaths.Contains(folder, StringComparer.OrdinalIgnoreCase) ? null : folder;
    }

    // Runs whichever of the two imports the settings ask for, over tracks the
    // caller holds. Both mutate Track objects in place and neither persists
    // anything - the caller decides how to publish that (the server: one
    // Library.NotifyLibraryChanged after both).
    //
    // Returns whether anything ran, so a caller can skip that publish entirely
    // rather than issuing a whole-table rewrite for two no-ops.
    //
    // The app does not come through here: each of its imports is a
    // cooldown-guarded, busy-scoped job with its own status message
    // (ITunesImportCoordinator), started from two different places.
    public static bool ApplyImports(
        MusicLibrarySettings settings, IEnumerable<Track> tracks, ILogger? logger = null)
    {
        var ran = false;

        if (settings.SyncPlayCountFromITunes)
        {
            ITunesPlayCountImporter.Apply(tracks, logger);
            ran = true;
        }
        if (settings.SyncDateAddedFromITunes)
        {
            ITunesDateAddedImporter.Apply(tracks, logger);
            ran = true;
        }

        return ran;
    }

    // Where the two imports above would actually read from, in one line a
    // settings screen can show, without doing any of the slow work: the live
    // AppleScript export is not triggered just to populate a label - this only
    // checks whether Music.app is installed at all, and otherwise whether a
    // static export exists to fall back to.
    public static string DescribeSource()
    {
        if (!OperatingSystem.IsMacOS())
            return "iTunes/Music.app is only available on macOS";

        if (Directory.Exists("/System/Applications/Music.app") || Directory.Exists("/Applications/Music.app"))
            return "Exports a fresh copy from Music.app each launch";

        return ITunesPlayCountImporter.ResolveLibraryXmlPath() is string fallbackPath
            ? $"Music.app not found - using {fallbackPath}"
            : "No iTunes/Music library data available";
    }
}
