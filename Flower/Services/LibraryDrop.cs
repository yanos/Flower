using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    // add, at its library path.
    public Task<Result> PrepareAsync(IEnumerable<string> dropped) => Task.Run(() => Prepare(dropped));

    private Result Prepare(IEnumerable<string> dropped)
    {
        var files = new List<string>();
        int copied = 0, failed = 0;
        foreach (var source in dropped.SelectMany(SongsAt).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (libraryRoots.Any(r => LibraryFolderWatcher.IsUnder(r, source)))
            {
                files.Add(source);
                continue;
            }
            if (libraryRoots.Count == 0)
            {
                failed++;
                continue;
            }

            try
            {
                var (destination, alreadyThere) = Destination(libraryRoots[0], source);
                if (!alreadyThere)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                    copied++;
                }
                files.Add(destination);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not copy dropped song {Source} into the library", source);
                failed++;
            }
        }

        return new Result(files, copied, failed);
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

    // Where a song from outside the library goes in it - or, when an
    // identical file (same name, same size) is already there because it was
    // dropped before, that file. A different file by the same name is
    // numbered rather than overwritten.
    private (string Path, bool AlreadyThere) Destination(string root, string source)
    {
        var folder = FolderFor(root, source);
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var candidate = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(candidate); n++)
        {
            if (new FileInfo(candidate).Length == new FileInfo(source).Length)
                return (candidate, true);
            candidate = Path.Combine(folder, $"{name} {n}{extension}");
        }
        return (candidate, false);
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
