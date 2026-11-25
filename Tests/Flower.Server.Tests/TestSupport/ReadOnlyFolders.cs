using Flower.Services;

namespace Flower.Server.Tests.TestSupport;

// A music folder this process cannot write to, for the tests of the warning a
// server gives about one ahead of time.
internal static class ReadOnlyFolders
{
    // Null where a folder cannot be made read-only for this process: Windows,
    // which has no mode bits, and root, which they do not bind.
    public static string? Create(string parent, string name)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var folder = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        MakeReadOnly(folder);
        if (LibraryFolders.CanWriteIn(folder))
        {
            MakeWritable(folder);
            return null;
        }

        return folder;
    }

    // Neither does anything on Windows, where Create never makes a folder
    // read-only and there are no mode bits to set.
    public static void MakeReadOnly(string folder)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    public static void MakeWritable(string folder)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
