using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Importer;

// What MusicLibrarySettings' three iTunes switches actually *mean*, in one place
// for both hosts. The two importers underneath already lived in Flower.Core and
// were already shared; the policy around them - is the integration on, is there
// a folder to adopt, which of the two imports should run - was not, and the app
// and the server had begun to answer those questions in their own words (see
// AppSettingsStore.Load and LibraryImportService.RescanAsync, both of which now
// call in here instead).
//
// Static rather than a service: every method is a pure function of the settings
// it is handed plus what is on disk, and both callers reach it from places with
// no container to resolve out of (a settings load before DI exists; a scan on
// its own scope). Loggers come in as parameters for the same reason - see
// ITunesPlayCountImporter.Apply's own note on this.
public static class ITunesIntegration
{
    // Music.app's configured media folder, when the integration is on, the
    // folder exists, and it is not already a library path - i.e. exactly when a
    // caller should add it to `settings.LibraryPaths`. Null in every other case,
    // including on hosts with no Music.app at all.
    //
    // Returns the folder rather than adding it, because where "add" lands
    // differs: the app appends to the AppSettings it is about to persist, the
    // server appends to the path list it is about to scan and writes that back
    // to flower-server.json. Both then have a folder the user can see and
    // remove, which is the point - and removing it is the *only* thing that
    // removes it: turning IntegrateWithITunes off stops this offering the folder
    // again but deliberately leaves an already-adopted one in place (see
    // SettingsViewModel.ApplyAppleMusicFolder). Dropping Music.app entirely is
    // therefore both - uncheck, so this stops re-adding it, and remove the
    // folder.
    public static string? ResolveMediaFolderToAdopt(MusicLibrarySettings settings, ILogger? logger = null)
    {
        if (!settings.IntegrateWithITunes)
            return null;

        if (Importer.TryResolveAppleMusicFolder(logger) is not { } folder)
            return null;

        return settings.LibraryPaths.Contains(folder, StringComparer.OrdinalIgnoreCase) ? null : folder;
    }

    // The two per-track imports only mean anything while the integration as a
    // whole is on - which is the one rule both hosts kept restating. Exposed
    // separately from ApplyImports below because the app does not run them the
    // way the server does: each is a cooldown-guarded, busy-scoped job with its
    // own status message (ITunesImportCoordinator), started from two different
    // places, so the app needs the question answered without the doing.
    public static bool ShouldSyncPlayCount(MusicLibrarySettings settings) =>
        settings.IntegrateWithITunes && settings.SyncPlayCountFromITunes;

    public static bool ShouldSyncDateAdded(MusicLibrarySettings settings) =>
        settings.IntegrateWithITunes && settings.SyncDateAddedFromITunes;

    // Runs whichever of the two the settings ask for, over tracks the caller
    // holds. Both mutate Track objects in place and neither persists anything -
    // the caller decides how to publish that (the server: one
    // Library.NotifyTrackChanged after both).
    //
    // Returns whether anything ran, so a caller can skip that publish entirely
    // rather than issuing a whole-table rewrite for two no-ops.
    public static bool ApplyImports(
        MusicLibrarySettings settings, IEnumerable<Track> tracks, ILogger? logger = null)
    {
        var ran = false;

        if (ShouldSyncPlayCount(settings))
        {
            ITunesPlayCountImporter.Apply(tracks, logger);
            ran = true;
        }
        if (ShouldSyncDateAdded(settings))
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
