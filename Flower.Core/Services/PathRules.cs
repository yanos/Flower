using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flower.Services;

// Whether two paths name the same file - asked by everything that keys on a
// path: the scan, the library's path indexes, the removed-on-purpose list, the
// folder watcher, an upload landing on the server. They used to answer it
// three ways: Library and the importer ignored case everywhere, LibraryFolders
// and LibraryDtoMapper ignored it everywhere but Linux, and LibraryIngest did
// both in different methods. On a Linux server, the reference deployment,
// "Song.flac" and "song.flac" in one folder are two files, and the importer
// kept whichever it met first and never saw the other.
//
// The answer belongs to the filesystem, not the OS: a Linux server reading a
// NAS over SMB sees a folder that ignores case, and a Mac can be formatted to
// respect it. So each library folder is asked once (ProbeIgnoresCase), and a
// path is compared by the rule of the deepest folder it sits under. A path
// under none of them - a download, a placeholder's old location - takes the
// platform's usual rule.
//
// Also an IEqualityComparer, so a dictionary of paths can be keyed by it
// directly. That holds as an equivalence: two paths are equal when they are the
// same string, or when both sit under folders that ignore case and differ only
// in case - and either way they hash alike.
public sealed class PathRules : IEqualityComparer<string>
{
    // Linux and Android are the platforms whose own disks respect case.
    private static readonly bool PlatformIgnoresCase = !OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

    // Deepest first, so the first match is the folder a path belongs to.
    private readonly (string Root, bool IgnoresCase)[] _roots;

    // The platform's rule for every path - what a library with no folders, or
    // a test, gets.
    public static PathRules Default { get; } = new([]);

    internal PathRules(IEnumerable<(string Root, bool IgnoresCase)> roots)
    {
        _roots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Root))
            .Select(r => (r.Root.TrimEnd('/', '\\'), r.IgnoresCase))
            .Where(r => r.Item1.Length > 0)
            .OrderByDescending(r => r.Item1.Length)
            .ToArray();
    }

    // Asks each folder that exists how it compares names. A folder that cannot
    // say - empty, unreadable, nothing in it with a letter - takes the
    // platform's rule; it holds no files for the rule to matter to, and the next
    // scan asks again.
    public static PathRules For(IEnumerable<string>? roots) =>
        new((roots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .Select(r => (r, ProbeIgnoresCase(r) ?? PlatformIgnoresCase)));

    public bool IgnoresCase(string path)
    {
        foreach (var (root, ignoresCase) in _roots)
        {
            if (IsUnder(path, root, ignoresCase))
                return ignoresCase;
        }

        return PlatformIgnoresCase;
    }

    public StringComparison ComparisonFor(string path) =>
        IgnoresCase(path) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The library folder this path is under - the deepest, where one is inside
    // another - or null when it is under none of them.
    public string? RootOf(string path)
    {
        foreach (var (root, ignoresCase) in _roots)
        {
            if (IsUnder(path, root, ignoresCase))
                return root;
        }

        return null;
    }

    public bool Equals(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
            return true;
        if (x == null || y == null || x.Length != y.Length)
            return false;

        return IgnoresCase(x) && IgnoresCase(y) && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(string path) =>
        IgnoresCase(path)
            ? StringComparer.OrdinalIgnoreCase.GetHashCode(path)
            : StringComparer.Ordinal.GetHashCode(path);

    private static bool IsUnder(string path, string root, bool ignoresCase) =>
        path.Length > root.Length
        && path[root.Length] is '/' or '\\'
        && path.StartsWith(root, ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Whether this folder's filesystem treats names differing only in case as
    // one name. Found out by reading, never writing, so it works on a
    // read-only mount: take something already in the folder, flip the case of
    // its name, and see whether that name opens anything. Asked of an entry
    // inside the folder rather than of the folder's own name, because the
    // folder's name belongs to whatever it is mounted on.
    //
    // A filesystem that respects case can hold both spellings as two entries,
    // and then the flipped name opens the other one - so a name whose flipped
    // twin is itself listed proves the folder respects case, rather than
    // suggesting it ignores it.
    internal static bool? ProbeIgnoresCase(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            var names = Directory.EnumerateFileSystemEntries(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            foreach (var name in names)
            {
                var flipped = FlipCase(name);
                if (flipped == name)
                    continue;
                if (names.Contains(flipped))
                    return false;

                var twin = Path.Combine(folder, flipped);
                return File.Exists(twin) || Directory.Exists(twin);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FlipCase(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsUpper(c) ? char.ToLowerInvariant(c)
                    : char.IsLower(c) ? char.ToUpperInvariant(c)
                    : c;
            }
        });
}
