using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;

namespace Flower.Services;

// Songs dropped on the desktop window: files, folders of them, or both.
//
// A song already inside a library folder is just added. One from anywhere
// else is copied in first - into the first library folder, under
// <album artist>/<album>/ - and the original is left where it was, the way
// Music.app's "Copy files to the Music folder" works. Copied rather than
// referenced where it lies, because the library is what its folders hold: a
// song outside them would vanish from the next scan, and is not one an admin
// device can mirror to its server, which names an upload by its path under a
// library folder (LibraryMirrorService).
public sealed class LibraryDrop(IReadOnlyList<string> libraryRoots, ILogger logger)
{
    public sealed record Result(IReadOnlyList<string> Files, int Copied, int Failed);

    // The songs among what was dropped, in place where they already sit in a
    // library folder and copied in where they do not. Files is every song to
    // add, at its library path. progress hears each song dealt with, of all
    // those dropped - the copying is what takes the time, an album off a slow
    // drive or a network share.
    public Task<Result> PrepareAsync(IEnumerable<string> dropped, IProgress<SongProgress>? progress = null) =>
        Task.Run(() => Prepare(dropped, progress));

    private Result Prepare(IEnumerable<string> dropped, IProgress<SongProgress>? progress)
    {
        var files = new List<string>();
        int copied = 0, failed = 0;
        var paths = dropped.ToList();
        var roots = paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p)).OfType<string>();
        var sources = paths.SelectMany(SongsAt).Distinct(PathRules.For(roots)).ToList();
        progress?.Report(new SongProgress(0, sources.Count));
        for (var i = 0; i < sources.Count; i++)
        {
            switch (Place(sources[i], files))
            {
                case true:
                    copied++;
                    break;
                case null:
                    failed++;
                    break;
            }
            progress?.Report(new SongProgress(i + 1, sources.Count));
        }

        return new Result(files, copied, failed);
    }

    // Puts one song where the library will find it and adds that path to
    // files: true when it was copied in, false when it was already there,
    // null when it could not be.
    private bool? Place(string source, List<string> files)
    {
        if (libraryRoots.Any(r => LibraryFolderWatcher.IsUnder(r, source)))
        {
            files.Add(source);
            return false;
        }
        if (libraryRoots.Count == 0)
            return null;

        try
        {
            var (destination, alreadyThere) = Destination(libraryRoots[0], source);
            if (!alreadyThere)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            files.Add(destination);
            return !alreadyThere;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not copy dropped song {Source} into the library", source);
            return null;
        }
    }

    // What AddAsync did: how many songs had been removed from the library
    // and came back, and the files that could not be read as songs.
    public sealed record Added(int Restored, IReadOnlyList<string> Unreadable);

    // The drop's second half: the songs PrepareAsync found, read into the
    // library now rather than when the watcher next notices them - or, with
    // no watcher, by the rescan given.
    //
    // Asked for by name, so a song removed from the library on purpose comes
    // back (UF-12): dropping it in again is as explicit as Settings' Restore,
    // which is the only other way back. Taken off the removed list before
    // anything reads it, and a scan that reads it first just adds it sooner. A
    // file the watcher sees change is never restored: that is a file
    // changing, not a person asking.
    public static async Task<Added> AddAsync(
        Library library, LibraryFolderWatcher? watcher, Func<Task> rescan,
        IReadOnlyCollection<string> files, IProgress<SongProgress>? progress = null)
    {
        if (files.Count == 0)
            return new Added(0, []);

        var restored = library.RestoreExcludedPaths(files);
        if (watcher == null)
        {
            await rescan();
            return new Added(restored, []);
        }

        var added = await watcher.AddFilesAsync(files, progress);
        return new Added(restored, added.Unreadable);
    }

    // What a drop says once it is done: the songs that did not go in (a copy
    // that failed, a file that is not a song after all) and the ones that had
    // been removed and came back. Null when every song went in as asked.
    public static string? Outcome(int notAdded, int restored)
    {
        var parts = new List<string>();
        if (notAdded > 0)
            parts.Add(notAdded == 1 ? "1 song could not be added" : $"{notAdded:N0} songs could not be added");
        if (restored > 0)
            parts.Add(restored == 1 ? "1 removed song is back in the library" : $"{restored:N0} removed songs are back in the library");
        return parts.Count == 0 ? null : string.Join(". ", parts);
    }

    private static IEnumerable<string> SongsAt(string path)
    {
        if (File.Exists(path))
            return Importer.Importer.IsImportable(path) ? [path] : [];
        if (!Directory.Exists(path))
            return [];
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(Importer.Importer.IsImportable)
            .Where(f => !Importer.Importer.IsUnderHiddenFolder(path, f))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    // Where a song from outside the library goes in it - or, when the same
    // file is already there because it was dropped before, that file. A
    // different file by the same name is numbered rather than overwritten.
    //
    // The same file means the same bytes. Name and size alone took a song
    // retagged since it was last dropped for the old copy, since a tag edit
    // often fits the padding a file already has, and the drop silently added
    // nothing (UF-13).
    private (string Path, bool AlreadyThere) Destination(string root, string source)
    {
        var folder = FolderFor(root, source);
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var candidate = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(candidate); n++)
        {
            if (SameBytes(candidate, source))
                return (candidate, true);
            candidate = Path.Combine(folder, $"{name} {n}{extension}");
        }
        return (candidate, false);
    }

    internal static bool SameBytes(string one, string other)
    {
        if (new FileInfo(one).Length != new FileInfo(other).Length)
            return false;

        using var a = File.OpenRead(one);
        using var b = File.OpenRead(other);
        var left = new byte[81920];
        var right = new byte[81920];
        while (true)
        {
            var read = a.ReadAtLeast(left, left.Length, throwOnEndOfStream: false);
            if (b.ReadAtLeast(right.AsSpan(0, read), read, throwOnEndOfStream: false) != read)
                return false;
            if (read == 0)
                return true;
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
                return false;
        }
    }

    private string FolderFor(string root, string source)
    {
        Track? tags = null;
        try
        {
            tags = new Importer.Importer(NullLogger<Importer.Importer>.Instance).ImportFile(source);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the tags of dropped song {Source}", source);
        }

        return Path.Combine(root,
            Segment(tags?.EffectiveAlbumArtist, "Unknown Artist"),
            Segment(tags?.Album, "Unknown Album"));
    }

    // A tag made into one folder name: anything the platform forbids in a
    // name taken out, and no leading or trailing dots or spaces - a leading
    // dot would hide the folder from every scan (Importer.IsUnderHiddenFolder).
    internal static string Segment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray())
            .Trim().Trim('.', ' ');
        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd('.', ' ');
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
