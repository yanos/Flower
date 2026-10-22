using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Persistence;

namespace Flower.Services;

// The folders this device's scan walks - every caller that used to scan, watch
// or name a file by AppSettings.LibraryPaths asks here instead.
//
// A device of its own scans its library folders and nothing else. A device
// paired to a server picks no folders (SettingsViewModel.CanManageLibrary), and
// its files come from two places instead, neither of them a folder anyone
// chose: Flower's own folder (FlowerFolder), which holds its downloads and the
// songs dropped on it, and - an administrator's Mac, when asked to - the music
// in its Music.app library. Folders it had before pairing are still scanned as
// they were. See docs/SYNC-PLAN.md, "A paired device has two sources of files,
// and no folders".
public static class DeviceLibraryFolders
{
    // Test-only: Music.app's media folder, in place of the one its
    // preferences name.
    public static string? MusicAppMediaFolderOverride { get; set; }

    public static List<string> ScanPaths(AppSettings settings)
    {
        var paths = new List<string>(settings.LibraryPaths);
        if (!IsPaired(settings))
            return paths;

        if (FlowerFolder.UsablePath(settings) is { } flower && !IsCovered(paths, flower))
            paths.Add(flower);
        if (MusicAppSource(settings) is { } musicApp)
            paths.Add(musicApp);
        return paths;
    }

    // The folders a scan walks that only ever add songs: a file gone from one
    // of them is dropped from this device and is never news for the server.
    //
    //   - Music.app's, because Music.app deletes local files of its own accord
    //     - Optimize Storage, Remove Download on a song in iCloud - and a file
    //     gone from its folder says nothing about whether the owner still wants
    //     the song. Ingest's guard against a false deletion (the folder must
    //     still be there and not empty) does not help: the folder is there and
    //     full.
    //   - Flower's own, because what it holds is the server's songs, lent:
    //     downloads, and imports once the server has them (LibraryMirrorService.
    //     LendImports). One still waiting to go up is not on the server either,
    //     so has nothing to report.
    //
    // Taking a song off the server is something done in Flower, never inferred
    // from a file.
    public static List<string> AddOnlyRoots(AppSettings settings)
    {
        var roots = new List<string>();
        if (!IsPaired(settings))
            return roots;

        if (FlowerFolder.UsablePath(settings) is { } flower)
            roots.Add(flower);
        if (MusicAppSource(settings) is { } musicApp)
            roots.Add(musicApp);
        return roots;
    }

    // The music in this Mac's Music.app library, when this device takes it in
    // (AppSettings.AddSongsFromMusicApp, SyncRolePolicy.MayAddSongsFromMusicApp).
    //
    // <media folder>/Music, not the media folder itself, which also holds
    // "Automatically Add to Music.localized" - whose files Music.app moves on
    // as it takes them in, so sending them would send every song twice - and
    // Podcasts, Movies, TV Shows and whatever else Music.app keeps beside the
    // music. Protected .m4p files are outside the importer's extensions anyway.
    //
    // Not when a library folder from before pairing already covers it: that
    // folder is scanned anyway, and one file under two roots has two relative
    // paths.
    public static string? MusicAppSource(AppSettings settings)
    {
        if (!settings.AddSongsFromMusicApp || !SyncRolePolicy.MayAddSongsFromMusicApp(settings))
            return null;
        if ((MusicAppMediaFolderOverride ?? Importer.Importer.TryResolveAppleMusicFolder()) is not { } media)
            return null;

        var music = Path.Combine(media, "Music");
        if (!Directory.Exists(music) || IsCovered(settings.LibraryPaths, music))
            return null;
        return music;
    }

    // Under one of these, or one of them.
    public static bool IsUnderAny(IEnumerable<string> roots, string path) =>
        roots.Any(root => LibraryFolderWatcher.IsUnder(root, path));

    private static bool IsCovered(IEnumerable<string> paths, string folder) =>
        paths.Any(p => !string.IsNullOrWhiteSpace(p) && LibraryFolderWatcher.IsUnder(p, folder));

    private static bool IsPaired(AppSettings settings) =>
        !string.IsNullOrEmpty(settings.PairedServerFingerprint) && !OperatingSystem.IsBrowser();
}
