using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Flower's own folder on this device: where a download lands, and - on a device
// paired to a server - where a song dropped on the window is copied. ~/Music/Flower
// on a desktop; on the phones, the app-private folders downloads already went to.
// See docs/SYNC-PLAN.md, "A paired device has two sources of files, and no
// folders".
//
// Flower adds to this folder, moves files within it and deletes downloads from
// it, so on a desktop it has to be Flower's and nobody else's. A marker file says
// so (MarkerName), written when Flower creates the folder or finds it empty. A
// folder by that name that already holds something and has no marker is
// somebody else's: it is not written to until whoever is at the keyboard has
// said whether Flower may take it over (AskToTakeOver), and a no puts Flower's
// folder beside it under another name, remembered in AppSettings.FlowerFolderPath.
// The phones' folders are inside the app's own container, where nothing else
// can have put anything, and are never asked about.
public sealed class FlowerFolder(AppSettings settings, Func<Task> saveSettings, ILogger<FlowerFolder> logger)
{
    public const string MarkerName = ".flower";

    // Test-only, checked first: SpecialFolder.MyMusic resolves through native
    // APIs that ignore a HOME override on macOS, so without this a test of a
    // download would write into the real developer's ~/Music. Marked like
    // any desktop folder, so the takeover rules are testable through it.
    public static string? Override { get; set; }

    // Asks whether a folder that is somebody else's may become Flower's, and
    // says yes or no. Set by a head that can ask (the desktop's main window);
    // with nothing to ask, the folder is left alone.
    public Func<string, Task<bool>>? AskToTakeOver { get; set; }

    private readonly SemaphoreSlim _claiming = new(1, 1);

    private static bool IsAppPrivate => Override == null && (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid());

    // Where the folder is, whether or not it exists yet, or is Flower's. One
    // chosen by a no to the takeover question first: only a desktop ever has
    // one, and Override stands in for the default, not for that answer.
    public static string PathFor(AppSettings settings)
    {
        if (settings.FlowerFolderPath is { Length: > 0 } chosen)
            return chosen;
        if (Override != null)
            return Override;
        if (OperatingSystem.IsIOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        // App-private storage, where MediaStore - a read-only index Flower
        // cannot insert into - does not look.
        if (OperatingSystem.IsAndroid() && PlatformDataDirectory.Current is { } androidRoot)
            return Path.Combine(androidRoot, "Downloads");

        return DefaultDesktopPath;
    }

    private static string DefaultDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Flower");

    // The folder, when it is Flower's already - without creating it or asking
    // anything. What a scan walks: a folder Flower has not claimed has nothing
    // of Flower's in it.
    public static string? UsablePath(AppSettings settings)
    {
        var path = PathFor(settings);
        if (IsAppPrivate)
            return Directory.Exists(path) ? path : null;
        return StateOf(path) == FolderState.Flowers ? path : null;
    }

    internal enum FolderState { Flowers, Free, SomebodyElses }

    // Free is absent, or there and empty - .DS_Store and its kind aside,
    // which Finder leaves in any folder it has shown.
    internal static FolderState StateOf(string path)
    {
        try
        {
            if (File.Exists(Path.Combine(path, MarkerName)))
                return FolderState.Flowers;
            if (!Directory.Exists(path))
                return FolderState.Free;
            return Directory.EnumerateFileSystemEntries(path).Any(e => !IsHouseKeeping(e))
                ? FolderState.SomebodyElses
                : FolderState.Free;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FolderState.SomebodyElses;
        }
    }

    private static bool IsHouseKeeping(string entry) =>
        Path.GetFileName(entry) is ".DS_Store" or ".localized" or "desktop.ini" or "Thumbs.db";

    // The folder, made Flower's if it is not yet: created and marked when it is
    // free, and asked about when it is somebody else's. Null when it cannot be
    // had - the question has nobody to ask, or the folder cannot be written.
    public async Task<string?> ClaimAsync()
    {
        await _claiming.WaitAsync();
        try
        {
            var path = PathFor(settings);
            if (IsAppPrivate)
            {
                Directory.CreateDirectory(path);
                return path;
            }

            switch (StateOf(path))
            {
                case FolderState.Flowers:
                    return path;
                case FolderState.Free:
                    return Mark(path);
            }

            if (AskToTakeOver is not { } ask)
            {
                logger.LogWarning("{Folder} already holds files Flower did not put there; leaving it alone until asked", path);
                return null;
            }

            if (await ask(path))
            {
                logger.LogInformation("Taking over {Folder} as Flower's folder, as asked", path);
                return Mark(path);
            }

            var elsewhere = FreeSibling(path);
            if (Mark(elsewhere) is not { } marked)
                return null;

            settings.FlowerFolderPath = marked;
            await saveSettings();
            logger.LogInformation("{Folder} is somebody else's; Flower's folder is {Elsewhere} instead", path, marked);
            return marked;
        }
        finally
        {
            _claiming.Release();
        }
    }

    // "Flower 2", "Flower 3"... beside it - the first that nothing is using,
    // or that is already Flower's from an earlier answer.
    private static string FreeSibling(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(parent, $"{name} {n}");
            if (StateOf(candidate) != FolderState.SomebodyElses)
                return candidate;
        }
    }

    private string? Mark(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var marker = Path.Combine(path, MarkerName);
            File.WriteAllText(marker,
                "This folder is Flower's: it keeps the songs Flower downloads, and the songs added to it, here.\n");
            // A leading dot hides it everywhere else.
            if (OperatingSystem.IsWindows())
                File.SetAttributes(marker, FileAttributes.Hidden);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not make {Folder} Flower's folder", path);
            return null;
        }
    }

    // Downloads that went straight into ~/Music, from before this folder
    // existed, moved into it at the same relative paths. Once: afterwards there
    // are none left outside it. Only files the library knows as downloads, so
    // nothing else in ~/Music - Music.app's library above all - is touched.
    // Desktop only; the phones' downloads were always where they still are.
    public async Task MoveOldDownloadsAsync(Library library)
    {
        if (IsAppPrivate || Override != null)
            return;

        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music) || !HasDownloadsOutside(library, music, PathFor(settings)))
            return;

        if (await ClaimAsync() is not { } folder)
            return;

        var moved = MoveDownloads(library, music, folder, logger);
        if (moved > 0)
            logger.LogInformation("Moved {Count} downloaded song(s) from {Music} into {Folder}", moved, music, folder);
    }

    private static IEnumerable<Track> DownloadsOutside(Library library, string from, string folder) =>
        library.Tracks.Where(t => t is { IsLocallyDownloaded: true, Path: { } path }
                                  && LibraryFolderWatcher.IsUnder(from, path)
                                  && !LibraryFolderWatcher.IsUnder(folder, path));

    private static bool HasDownloadsOutside(Library library, string from, string folder) =>
        DownloadsOutside(library, from, folder).Any();

    // Each file to the same place below folder that it had below from. A name
    // already taken there is left where it is: whatever holds it is not this
    // download, and is not overwritten.
    internal static int MoveDownloads(Library library, string from, string folder, ILogger logger)
    {
        var moved = 0;
        foreach (var track in DownloadsOutside(library, from, folder).ToList())
        {
            var path = track.Path!;
            var target = Path.Combine(folder, Path.GetRelativePath(from, path));
            try
            {
                if (!File.Exists(path) || File.Exists(target))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, target);
                library.MoveTrack(track, target);
                LibraryFolders.RemoveEmptyFolders(Path.GetDirectoryName(path), from);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not move downloaded {Path} into Flower's folder", LogPath.Short(path));
            }
        }

        return moved;
    }
}
