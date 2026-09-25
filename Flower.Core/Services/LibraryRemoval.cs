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
}
