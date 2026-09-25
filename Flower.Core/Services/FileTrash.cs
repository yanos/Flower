using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Flower.Services;

// Moving a file to the platform's own trash rather than deleting it - what
// "Remove from Library" with "also delete the files" does, on every host that
// has a trash to move to. .NET has no cross-platform call for it, so this is
// one per platform:
//
//   - macOS: NSFileManager's trashItemAtURL, reached through the Objective-C
//     runtime directly rather than through the macOS workload's bindings, so
//     it works the same from Flower.Desktop running on a Mac, from
//     Flower.MacOS, and from Flower.Server. That is the call Finder itself
//     uses, so "Put Back" works.
//   - Windows: the shell's Recycle Bin, via SHFileOperation with every dialog
//     suppressed - a server has nobody to click one.
//   - Linux: the freedesktop.org Trash specification (see LinuxTrash), which
//     is what GNOME, KDE and every file manager built on them read.
//   - iOS and Android: none. An app's own files have no trash to go to, so
//     IsSupported is false and the caller deletes them outright.
public static class FileTrash
{
    public static bool IsSupported =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    // Test-only, checked first: a test suite that removed songs with their
    // files would otherwise fill the developer's real Trash on every run.
    // Same shape as LibraryDownloadService.DownloadFolderOverride. Null
    // everywhere else.
    public static Func<string, bool>? Override { get; set; }

    // What the platform calls it, for a sentence in the UI.
    public static string Name => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    // True once the file is in the trash; false when this platform has none
    // (the caller decides what to do instead). Throws when there is a trash
    // and the move into it failed - a read-only mount, a permission, a file
    // held open - with the platform's own reason as the message.
    public static bool TryMoveToTrash(string path)
    {
        if (Override is { } testTrash)
            return testTrash(path);

        if (OperatingSystem.IsMacOS())
        {
            MacTrash.MoveToTrash(path);
            return true;
        }
        if (OperatingSystem.IsWindows())
        {
            WindowsTrash.MoveToTrash(path);
            return true;
        }
        if (OperatingSystem.IsLinux())
        {
            LinuxTrash.ForCurrentUser().MoveToTrash(path);
            return true;
        }

        return false;
    }

    private static class MacTrash
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
        [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

        // - (BOOL)trashItemAtURL:(NSURL *)url resultingItemURL:(NSURL **)outResultingURL error:(NSError **)error
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr resultingUrl, out IntPtr error);

        private static readonly Lazy<bool> FoundationLoaded = new(() =>
        {
            // objc_getClass only finds classes from images already loaded, and
            // a plain .NET process has not loaded Foundation.
            NativeLibrary.Load(Foundation);
            return true;
        });

        public static void MoveToTrash(string path)
        {
            _ = FoundationLoaded.Value;

            // The strings, URL and error below are all autoreleased; a pool of
            // our own keeps them from leaking on a thread that has none.
            var pool = objc_autoreleasePoolPush();
            try
            {
                var nsPath = SendUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), path);
                var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), nsPath);
                var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));

                if (SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out var error) != 0)
                    return;

                throw new IOException($"Could not move {path} to the Trash: {Describe(error)}");
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        private static string Describe(IntPtr error)
        {
            if (error == IntPtr.Zero)
                return "no reason given";

            var description = Send(error, sel_registerName("localizedDescription"));
            var utf8 = Send(description, sel_registerName("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8) ?? "no reason given";
        }
    }

    private static class WindowsTrash
    {
        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

        public static void MoveToTrash(string path)
        {
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // A list of paths, each null-terminated and the whole list
                // ending in a second null; the marshaller supplies one of them.
                pFrom = Path.GetFullPath(path) + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };

            var result = SHFileOperation(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted != 0)
                throw new IOException($"Could not move {path} to the Recycle Bin (shell error 0x{result:X}).");
        }
    }
}

// The freedesktop.org Trash specification, version 1.0: a trash is a folder
// holding files/ (the trashed files themselves) and info/ (one .trashinfo per
// file, saying where it came from and when, which is what "Restore" reads).
//
// Which trash a file goes to depends on which drive it is on. The home trash
// ($XDG_DATA_HOME/Trash) is for files on the same filesystem as it - a move
// there is a rename. A file on any other drive goes to that drive's own
// $topdir/.Trash-$uid instead, which the spec provides so a trashed file never
// has to be copied across drives. That is also what makes this work for
// Flower.Server in a container, whose music is a mount of its own: the file
// stays on the music drive, in a hidden folder the importer skips.
//
// Only the spec's second per-drive method ($topdir/.Trash-$uid) is used, not
// the administrator-created shared $topdir/.Trash/$uid - the spec allows an
// implementation to go straight to the second.
//
// Instance-based, with the two facts it needs from the system handed in, so a
// test can run it against folders of its own on a machine that is not Linux.
public sealed class LinuxTrash(string homeTrash, Func<string, string> mountPointOf, uint uid)
{
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    public static LinuxTrash ForCurrentUser()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var mounts = ReadMountPoints();
        return new LinuxTrash(Path.Combine(dataHome, "Trash"), path => MountPointOf(path, mounts), GetUid());
    }

    public void MoveToTrash(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileMount = mountPointOf(fullPath);

        // The home trash's own drive, judged by the nearest folder that exists
        // - the trash itself may not have been created yet.
        var homeTrashMount = mountPointOf(NearestExisting(homeTrash));

        string trash;
        string recordedPath;
        if (fileMount == homeTrashMount)
        {
            trash = homeTrash;
            recordedPath = fullPath;
        }
        else
        {
            trash = Path.Combine(fileMount, $".Trash-{uid}");
            // Relative to the drive's top, as the spec asks for a per-drive
            // trash - it survives the drive being mounted somewhere else.
            recordedPath = Path.GetRelativePath(fileMount, fullPath);
        }

        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        CreatePrivateDirectory(trash);
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        // The .trashinfo is created first, exclusively: that is how the spec
        // reserves a name, so two trashings of same-named files cannot both
        // pick it.
        var (name, infoPath) = ReserveName(info, Path.GetFileName(fullPath), recordedPath);
        try
        {
            File.Move(fullPath, Path.Combine(files, name));
        }
        catch
        {
            File.Delete(infoPath);
            throw;
        }
    }

    private static (string Name, string InfoPath) ReserveName(string infoFolder, string fileName, string recordedPath)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var contents =
            "[Trash Info]\n"
            + $"Path={EncodePath(recordedPath)}\n"
            + $"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";

        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? fileName : $"{stem}.{attempt}{extension}";
            var infoPath = Path.Combine(infoFolder, name + ".trashinfo");
            try
            {
                using var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write);
                stream.Write(Encoding.UTF8.GetBytes(contents));
                return (name, infoPath);
            }
            catch (IOException) when (File.Exists(infoPath) && attempt < 10_000)
            {
                // Taken - try the next name.
            }
        }
    }

    // Percent-encoded per segment, the way a URL path is - the spec's rule.
    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static void CreatePrivateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string NearestExisting(string path)
    {
        var current = path;
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent)
            current = parent;
        return current;
    }

    // The mount points listed in /proc/self/mountinfo (the fifth field), with
    // the kernel's octal escapes for spaces and the like undone.
    private static List<string> ReadMountPoints()
    {
        try
        {
            return File.ReadLines("/proc/self/mountinfo")
                .Select(line => line.Split(' '))
                .Where(fields => fields.Length > 4)
                .Select(fields => UnescapeMountPath(fields[4]))
                .ToList();
        }
        catch (IOException)
        {
            return ["/"];
        }
    }

    // The longest mount point the path lies under.
    internal static string MountPointOf(string path, IReadOnlyList<string> mountPoints) =>
        mountPoints
            .Where(mount => mount == "/" || path == mount || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(mount => mount.Length)
            .FirstOrDefault() ?? "/";

    internal static string UnescapeMountPath(string escaped)
    {
        var builder = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == '\\' && i + 3 < escaped.Length
                && escaped.Substring(i + 1, 3).All(c => c is >= '0' and <= '7'))
            {
                builder.Append((char)Convert.ToInt32(escaped.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(escaped[i]);
            }
        }

        return builder.ToString();
    }
}
