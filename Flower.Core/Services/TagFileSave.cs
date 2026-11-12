using System;
using System.Collections.Concurrent;
using System.IO;

using Microsoft.Extensions.Logging;

using Flower.Logging;

namespace Flower.Services;

// Every tag write in Flower goes through here: tags, covers, the compilation
// flag, on a device and on the server alike.
//
// The edit is made to a copy, which then replaces the song by a rename, never
// to the song in place. TagLib opens a file to save it with FileShare.None,
// which .NET turns into an exclusive flock on macOS, Linux and the phones, and
// anything already reading the song holds a shared one. The server holds one
// for the whole time it is sending the song, which on a phone on cellular is
// minutes (the phone pulls it as it plays), so every edit of the song a
// listener was playing failed with "being used by another process": 67 of
// them on one server in a week, all for that reason. A rename needs no lock on
// the file it replaces. Whoever was reading it carries on with the old file
// until they close it, and the next open gets the new one. A stream reopened
// mid-song lands at a different byte by then, which SeekableHttpStream
// handles (Realign).
//
// The copy is a hidden .tmp beside the song (a rename has to stay on one
// file system), so it is nothing a scan, the folder watcher or an upload
// would ever take for a song (Importer.IsImportable). One left behind by a
// crash is deleted by the next walk of the folders that finds it a day old
// (Importer.ScannableFiles).
//
// Not on Windows, where a file another process has open without
// FileShare.Delete cannot be replaced: there a held song fails the save as it
// always did, and the copy is removed.
public static class TagFileSave
{
    private const string LeftoverPrefix = ".flower-save-";
    private const string LeftoverExtension = ".tmp";

    // Far longer than any save takes, even of a large FLAC on a slow share.
    internal static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    // The copies this process is working on right now, which no sweep may
    // take however old the clock says they are.
    private static readonly ConcurrentDictionary<string, byte> InProgress = new(StringComparer.Ordinal);

    // Two saves of one song at once would each copy the original and the
    // second rename would undo the first's edit. In place, the lock refused
    // the second; here they take turns. Striped by path, so an edit of one
    // album does not wait behind another's.
    private static readonly object[] Stripes = CreateStripes(64);

    // Opens the song as TagLib would, lets edit change the tag, and saves the
    // result over the song. edit returns false when there turned out to be
    // nothing to change, and the song is left as it was. Throws whatever the
    // copy, TagLib or the rename throws; the song is untouched when it does.
    public static T Edit<T>(string path, Func<TagLib.File, (bool Changed, T Result)> edit)
    {
        // The file a link points at is the one to change; replacing the link
        // itself would turn it into a copy.
        var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var folder = Path.GetDirectoryName(target) ?? ".";
        var copy = Path.Combine(folder, LeftoverPrefix + Guid.NewGuid().ToString("N") + LeftoverExtension);

        lock (Stripes[(StringComparer.Ordinal.GetHashCode(target) & int.MaxValue) % Stripes.Length])
        {
            InProgress[copy] = 0;
            try
            {
                File.Copy(target, copy);
                // A copy may keep the original's date, which would make it look
                // a day old to a sweep running in another process.
                File.SetLastWriteTimeUtc(copy, DateTime.UtcNow);

                (bool Changed, T Result) outcome;
                // The type comes from the song's name, not the copy's, which
                // is the point of the copy's name.
                using (var tagFile = TagLib.File.Create(
                           new TagLib.File.LocalFileAbstraction(copy), MimeTypeOf(target), TagLib.ReadStyle.Average))
                {
                    outcome = edit(tagFile);
                    if (outcome.Changed)
                        tagFile.Save();
                }

                if (outcome.Changed)
                    File.Move(copy, target, overwrite: true);
                return outcome.Result;
            }
            finally
            {
                TryDelete(copy);
                InProgress.TryRemove(copy, out _);
            }
        }
    }

    public static void Edit(string path, Action<TagLib.File> edit) =>
        Edit(path, file =>
        {
            edit(file);
            return (true, 0);
        });

    // Whether this is the copy of a save, by its name.
    public static bool IsLeftover(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(LeftoverPrefix, StringComparison.Ordinal)
               && name.EndsWith(LeftoverExtension, StringComparison.Ordinal);
    }

    // Deletes the file if it is the copy of a save that never finished: named
    // as one, a day old, and not one this process is working on. True when it
    // is a save's copy at all, deleted or not, so a walk can pass over it.
    public static bool ClearIfLeftover(string path, ILogger? logger = null)
    {
        if (!IsLeftover(path))
            return false;

        try
        {
            if (!InProgress.ContainsKey(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > LeftoverAge)
            {
                File.Delete(path);
                logger?.LogInformation("Deleted {Path}, left behind by a tag save that never finished", LogPath.Short(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not delete {Path}, left behind by a tag save", LogPath.Short(path));
        }

        return true;
    }

    // What TagLib.File.Create(path) would have chosen for the song.
    private static string MimeTypeOf(string path) =>
        "taglib/" + Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for a sweep.
        }
    }

    private static object[] CreateStripes(int count)
    {
        var stripes = new object[count];
        for (var i = 0; i < count; i++)
            stripes[i] = new object();
        return stripes;
    }
}
