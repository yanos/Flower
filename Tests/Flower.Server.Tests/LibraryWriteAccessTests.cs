using Flower.Server.Services;
using Flower.Services;

using Microsoft.Extensions.Logging;

namespace Flower.Server.Tests;

// A music folder the server cannot write to is said once, ahead of time, rather
// than discovered by an owner's phone one refused upload at a time. What is
// worth holding still: that the probe tells a read-only folder from a writable
// one by trying, that the warning names the folder, and that it is logged when
// the answer changes and not at every scan - a server left read-only on purpose
// rescans for years.
public class LibraryWriteAccessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-write-access").FullName;

    private sealed class RecordingLogger : ILogger<LibraryWriteAccess>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private string Writable(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // Null where a folder cannot be made read-only for this process: Windows,
    // which has no mode bits, and root, which they do not bind.
    internal static string? ReadOnlyFolder(string parent, string name)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var folder = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (LibraryFolders.CanWriteIn(folder))
        {
            MakeWritable(folder);
            return null;
        }

        return folder;
    }

    internal static void MakeWritable(string folder) =>
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var folder in Directory.GetDirectories(_root))
                MakeWritable(folder);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_writable_folder_is_writable_and_the_probe_leaves_nothing_behind()
    {
        var folder = Writable("music");

        Assert.True(LibraryFolders.CanWriteIn(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public void Only_the_folders_that_are_there_and_refuse_a_file_are_unwritable()
    {
        var writable = Writable("music");
        var missing = Path.Combine(_root, "not-mounted");
        if (ReadOnlyFolder(_root, "read-only") is not { } readOnly)
            return;

        Assert.Equal([readOnly], LibraryFolders.Unwritable([writable, readOnly, missing, ""]));
    }

    [Fact]
    public void A_server_that_can_write_everywhere_says_nothing()
    {
        var logger = new RecordingLogger();
        var access = new LibraryWriteAccess(logger);

        access.Check([Writable("music")]);

        Assert.Empty(access.UnwritableFolders);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_read_only_folder_is_warned_about_by_name_once_and_not_at_every_scan()
    {
        if (ReadOnlyFolder(_root, "read-only") is not { } readOnly)
            return;
        var logger = new RecordingLogger();
        var access = new LibraryWriteAccess(logger);

        access.Check([readOnly]);
        access.Check([readOnly]);
        access.Check([readOnly]);

        Assert.Equal([readOnly], access.UnwritableFolders);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(readOnly, entry.Message);
    }

    [Fact]
    public void A_folder_that_becomes_writable_is_said_to_be_and_one_that_stops_is_warned_about_again()
    {
        if (ReadOnlyFolder(_root, "music") is not { } folder)
            return;
        var logger = new RecordingLogger();
        var access = new LibraryWriteAccess(logger);

        access.Check([folder]);
        MakeWritable(folder);
        access.Check([folder]);

        Assert.Empty(access.UnwritableFolders);
        Assert.Equal([LogLevel.Warning, LogLevel.Information], logger.Entries.Select(e => e.Level));

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        access.Check([folder]);

        Assert.Equal([LogLevel.Warning, LogLevel.Information, LogLevel.Warning], logger.Entries.Select(e => e.Level));
    }
}
