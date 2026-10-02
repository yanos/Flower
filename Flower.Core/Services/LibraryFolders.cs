using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flower.Services;

// Two questions about a library's folders that are asked on both hosts, by
// everything that moves or deletes a file on the library's behalf.
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
}
