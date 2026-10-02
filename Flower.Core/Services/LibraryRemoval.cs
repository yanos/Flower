using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;

namespace Flower.Services;

// What "Remove from Library" did, for the caller to report.
//
// FilesNotDeleted are files the caller asked to delete that could not be: they
// are still on disk, and are kept out of future scans exactly like a file the
// user chose to keep, so the removal itself still holds.
//
// FilesTrashed went to the platform's trash (see FileTrash) and can be put
// back from there; FilesDeleted are gone for good, which only happens on a
// platform with no trash to use - a phone.
public sealed record LibraryRemovalResult(
    int Removed, int FilesTrashed, int FilesDeleted, IReadOnlyList<string> FilesNotDeleted);

// "Remove from Library", as either host carries it out on its own tracks: the
// app for a device's own files and placeholders, Flower.Server for the
// library it serves (AdminEndpoints' POST /library/remove, which is what an
// admin client's removal becomes on the server). One routine, so the rule for
// what happens to a file cannot differ between the two.
public static class LibraryRemoval
{
    public static LibraryRemovalResult Remove(
        Library library, IReadOnlyCollection<Track> tracks, bool deleteFiles, ILogger logger)
    {
        var keptOnDisk = new List<string>();
        var notDeleted = new List<string>();
        var trashed = 0;
        var deleted = 0;

        foreach (var track in tracks)
        {
            if (track.Path is not { } path)
                continue;

            if (!deleteFiles)
            {
                keptOnDisk.Add(path);
                continue;
            }

            try
            {
                // To the trash wherever there is one, so a removal made by
                // mistake - or by somebody else's admin phone, on the server -
                // is a trip to the trash to undo rather than a restore from
                // backup. A file restored from there is not excluded, so the
                // next scan finds it and it is simply back.
                if (File.Exists(path))
                {
                    if (FileTrash.TryMoveToTrash(path))
                    {
                        trashed++;
                    }
                    else
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not trash or delete {Path} while removing it from the library; keeping it out of future scans instead",
                    LogPath.Short(path));
                notDeleted.Add(path);
                keptOnDisk.Add(path);
            }
        }

        var removed = library.RemoveTracks(tracks, keptOnDisk);
        return new LibraryRemovalResult(removed.Count, trashed, deleted, notDeleted);
    }

    // The other way out of "Removed Songs": the files that were removed from
    // the library and left on disk, deleted for good. Restore puts a song
    // back; this is the cleaning up, done when the owner says so and not
    // before - a removal that reached this library from another device (see
    // Library.MergeSyncedTracks) never deletes anything on its own.
    //
    // Only paths on the library's own list are touched, whatever is asked
    // for, so the worst a caller can do is delete a file somebody already
    // removed. Deleted outright rather than sent to the trash: this is the
    // step whose whole meaning is "and now free the space".
    //
    // roots are the library folders, for tidying away the folders this
    // empties (LibraryFolders.RemoveEmptyFolders).
    public static (int Deleted, int NotDeleted) DeleteRemovedFiles(
        Library library, IReadOnlyCollection<string> paths, IReadOnlyList<string> roots, ILogger logger)
    {
        var deleted = new List<string>();
        var notDeleted = 0;
        foreach (var path in paths)
        {
            if (!library.IsExcludedPath(path))
                continue;

            try
            {
                // One already gone - deleted by hand since - is simply done.
                if (File.Exists(path))
                    File.Delete(path);
                deleted.Add(path);
                LibraryFolders.RemoveEmptyFolders(Path.GetDirectoryName(path), LibraryFolders.RootOf(path, roots));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {Path}; it stays in Removed Songs", LogPath.Short(path));
                notDeleted++;
            }
        }

        library.ForgetExcludedPaths(deleted);
        logger.LogInformation("Permanently deleted {Deleted} removed file(s); {NotDeleted} could not be deleted", deleted.Count, notDeleted);
        return (deleted.Count, notDeleted);
    }
}
