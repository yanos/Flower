using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flower.Services;

// Questions about a library's folders that are asked on both hosts, by
// everything that writes, moves or deletes a file on the library's behalf.
public static class LibraryFolders
{
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

        // Ignoring case only to keep the walk inside the root: the root came
        // from this path's own PathRules.RootOf match, or is a prefix of it, so
        // the two already agree on which folder it is.
        var stop = root.TrimEnd('/', '\\');
        try
        {
            while (folder != null
                   && folder.Length > stop.Length
                   && folder.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
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

    // Whether a file no longer at path was deleted, rather than being on a
    // drive that is not plugged in or a share that did not mount. Asked of
    // the nearest folder above it that is still there, which is the folder it
    // lived in unless that went too (an album folder thrown away whole). The
    // library folder alone is not enough to ask, because a drive linked or
    // mounted inside it takes its songs away while the library folder stays
    // exactly as full as it was.
    //
    // That folder must not be reached through a link left pointing at
    // nothing - an unplugged drive linked into the library. If it holds
    // anything, the file was deleted. If it is empty, it was only when it is
    // the file's own folder: that is what "rm *" in an album leaves. An empty
    // folder further up, with everything below it gone, is what a share that
    // did not mount leaves, and so is an empty library folder or an empty
    // link or mount point of its own. The case this cannot tell apart is a
    // share holding nothing but loose songs at its top, which did not mount.
    //
    // Nothing above the library folder is asked: path must be under root.
    public static bool WasDeleted(string path, string root)
    {
        var top = Path.TrimEndingDirectorySeparator(root).Length;
        try
        {
            var below = path;
            for (var folder = Path.GetDirectoryName(path);
                 folder != null && folder.Length >= top;
                 folder = Path.GetDirectoryName(folder))
            {
                if (Directory.Exists(folder))
                {
                    if (new FileInfo(below).LinkTarget != null)
                        return false;
                    if (Directory.EnumerateFileSystemEntries(folder).Any())
                        return true;
                    return below == path && folder.Length > top && new DirectoryInfo(folder).LinkTarget == null;
                }
                below = folder;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
