using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flower.Services;

// Questions about a library's folders that are asked on both hosts, by
// everything that writes, moves or deletes a file on the library's behalf.
public static class LibraryFolders
{
    // Case-insensitive off Linux, matching how the importer and
    // Library.UpdateTracks already compare paths.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Whichever library folder this file is under - the deepest, where one is
    // inside another - or null when it is under none of them.
    public static string? RootOf(string path, IEnumerable<string> roots)
    {
        string? best = null;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var prefix = root.TrimEnd('/', '\\');
            if (path.Length > prefix.Length
                && path.StartsWith(prefix, PathComparison)
                && path[prefix.Length] is '/' or '\\'
                && (best == null || prefix.Length > best.Length))
            {
                best = prefix;
            }
        }

        return best;
    }

    // Tidies up after a file has left a folder: the folder goes if that left
    // it empty, then the one above it, and so on up - stopping below the
    // library folder itself, which is the user's and stays however empty.
    // Moving the last song out of "Artist/Old Album" should not leave "Old
    // Album" behind for ever.
    //
    // Best effort and silent. A folder that still holds anything at all - a
    // cover image, a .DS_Store - is not empty and is left exactly as it is.
    public static void RemoveEmptyFolders(string? folder, string? root)
    {
        if (root == null)
            return;

        var stop = root.TrimEnd('/', '\\');
        try
        {
            while (folder != null
                   && folder.Length > stop.Length
                   && folder.StartsWith(stop, PathComparison)
                   && Directory.Exists(folder)
                   && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Somebody else's file appeared, or the folder is not ours to
            // remove. Either way it stays.
        }
    }

    // Whether a file can be created in this folder, found out the only way that
    // is true on every filesystem: by creating one. Permission bits do not
    // answer it - a read-only mount shows a writable directory to a process
    // that owns it, and an NFS export can refuse a user its own mode bits allow.
    // The probe is named so that one left behind by a kill between the two
    // lines says what it was.
    public static bool CanWriteIn(string folder)
    {
        var probe = Path.Combine(folder, $".flower-write-check-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The library folders this process cannot write to, of the ones that are
    // there. A folder that does not exist is not on the list: it is a different
    // problem (nothing is scanned from it either) and "cannot write" would
    // misname it.
    public static List<string> Unwritable(IEnumerable<string> roots) =>
        roots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.Ordinal)
            .Where(root => !CanWriteIn(root))
            .ToList();
}
