using System;
using System.IO;
using System.Linq;

using Flower.Importer;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// FileTrash, per platform. The freedesktop half runs everywhere against folders
// of its own - it is plain file moves, given a mount table to consult. The
// macOS and Windows halves can only be proved on those platforms, against the
// real trash, so each runs on its own OS and takes back out what it put in.
//
// In the PlatformDataDirectory collection because two of these clear
// FileTrash.Override (the assembly-wide redirect to a test folder) for their
// duration, and the other tests that trash files are in that collection too.
[Collection("PlatformDataDirectory")]
public class FileTrashTests : PinnedDataDirectory
{
    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(DataDirectory, name)).FullName;

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    // ── freedesktop.org ──────────────────────────────────────────────────

    [Fact]
    public void A_file_on_the_home_drive_goes_to_the_home_trash_with_a_trashinfo_saying_where_it_came_from()
    {
        var root = Folder("root");
        var homeTrash = Path.Combine(root, "home", ".local", "share", "Trash");
        Directory.CreateDirectory(Path.Combine(root, "home"));
        var song = Touch(Path.Combine(root, "music", "My Band", "Song #1.flac"));
        var trash = new LinuxTrash(homeTrash, _ => root, uid: 1000);

        trash.MoveToTrash(song);

        Assert.False(File.Exists(song));
        Assert.True(File.Exists(Path.Combine(homeTrash, "files", "Song #1.flac")));
        var info = File.ReadAllLines(Path.Combine(homeTrash, "info", "Song #1.flac.trashinfo"));
        Assert.Equal("[Trash Info]", info[0]);
        Assert.Equal("Path=" + string.Join('/', song.Split('/', Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)), info[1]);
        Assert.StartsWith("DeletionDate=", info[2]);
    }

    // A file on another drive - a NAS mount, or the music volume of a server in
    // a container - goes to that drive's own .Trash-<uid>, so nothing is copied
    // across drives, and the path it records is relative to that drive's top.
    [Fact]
    public void A_file_on_another_drive_goes_to_that_drives_own_trash()
    {
        var home = Folder("home");
        var music = Folder("music");
        var song = Touch(Path.Combine(music, "Album", "Song.flac"));
        var trash = new LinuxTrash(
            Path.Combine(home, ".local", "share", "Trash"),
            path => path.StartsWith(music, StringComparison.Ordinal) ? music : home,
            uid: 1000);

        trash.MoveToTrash(song);

        var driveTrash = Path.Combine(music, ".Trash-1000");
        Assert.True(File.Exists(Path.Combine(driveTrash, "files", "Song.flac")));
        Assert.Contains("Path=Album/Song.flac", File.ReadAllLines(Path.Combine(driveTrash, "info", "Song.flac.trashinfo")));
        Assert.False(Directory.Exists(Path.Combine(home, ".local")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(driveTrash));
    }

    [Fact]
    public void Two_files_with_the_same_name_both_fit_in_the_trash()
    {
        var root = Folder("root");
        var homeTrash = Path.Combine(root, "Trash");
        var trash = new LinuxTrash(homeTrash, _ => root, uid: 1000);

        trash.MoveToTrash(Touch(Path.Combine(root, "a", "Intro.mp3")));
        trash.MoveToTrash(Touch(Path.Combine(root, "b", "Intro.mp3")));

        Assert.True(File.Exists(Path.Combine(homeTrash, "files", "Intro.mp3")));
        Assert.True(File.Exists(Path.Combine(homeTrash, "files", "Intro.2.mp3")));
        Assert.True(File.Exists(Path.Combine(homeTrash, "info", "Intro.2.mp3.trashinfo")));
    }

    // A move that fails leaves no .trashinfo behind describing a file the
    // trash does not hold - a restore would find nothing to put back.
    [Fact]
    public void A_file_that_will_not_move_leaves_no_trashinfo_behind()
    {
        var root = Folder("root");
        var homeTrash = Path.Combine(root, "Trash");
        var trash = new LinuxTrash(homeTrash, _ => root, uid: 1000);

        Assert.ThrowsAny<IOException>(() => trash.MoveToTrash(Path.Combine(root, "not-there.mp3")));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(homeTrash, "info")));
    }

    [Theory]
    [InlineData("/srv/music/a.flac", "/srv/music")]
    [InlineData("/srv/musical/a.flac", "/")]
    [InlineData("/home/me/a.flac", "/home")]
    public void A_path_belongs_to_the_longest_mount_it_lies_under(string path, string expected)
    {
        Assert.Equal(expected, LinuxTrash.MountPointOf(path, ["/", "/home", "/srv/music"]));
    }

    [Fact]
    public void A_mount_point_with_a_space_is_read_back_from_its_kernel_escape()
    {
        Assert.Equal("/media/me/My Music", LinuxTrash.UnescapeMountPath(@"/media/me/My\040Music"));
    }

    // ── The real platform trash ──────────────────────────────────────────

    // Into the real Trash and straight back out again: this is the only way to
    // know the Objective-C calls are right.
    [Fact]
    public void On_macOS_a_file_goes_to_the_real_Trash()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var name = $"flower-trash-test-{Guid.NewGuid():N}.mp3";
        var file = Touch(Path.Combine(DataDirectory, name));
        var previous = FileTrash.Override;
        FileTrash.Override = null;
        try
        {
            Assert.True(FileTrash.TryMoveToTrash(file));
        }
        finally
        {
            FileTrash.Override = previous;
        }

        var trashed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash", name);
        try
        {
            Assert.False(File.Exists(file));
            Assert.True(File.Exists(trashed));
        }
        finally
        {
            File.Delete(trashed);
        }
    }

    [Fact]
    public void On_macOS_a_file_that_is_not_there_is_an_error_saying_why()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var previous = FileTrash.Override;
        FileTrash.Override = null;
        try
        {
            var error = Assert.Throws<IOException>(() => FileTrash.TryMoveToTrash(Path.Combine(DataDirectory, "missing.mp3")));
            Assert.Contains("missing.mp3", error.Message);
        }
        finally
        {
            FileTrash.Override = previous;
        }
    }

    // The Recycle Bin gives nothing back to look for by name, so this proves
    // the call succeeds, with no dialog, and the file is gone from where it was.
    [Fact]
    public void On_Windows_a_file_goes_to_the_Recycle_Bin()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var file = Touch(Path.Combine(DataDirectory, $"flower-trash-test-{Guid.NewGuid():N}.mp3"));
        var previous = FileTrash.Override;
        FileTrash.Override = null;
        try
        {
            Assert.True(FileTrash.TryMoveToTrash(file));
            Assert.False(File.Exists(file));
        }
        finally
        {
            FileTrash.Override = previous;
        }
    }

    // ── And the scan that must not undo it ───────────────────────────────

    [Theory]
    [InlineData("/music", "/music/Album/Song.mp3", false)]
    [InlineData("/music", "/music/.Trash-1000/files/Song.mp3", true)]
    [InlineData("/music", "/music/.Trashes/501/Song.mp3", true)]
    [InlineData("/music", "/music/Album/.hidden.mp3", false)]
    [InlineData("/Users/me/.music", "/Users/me/.music/Album/Song.mp3", false)]
    public void A_scan_skips_anything_under_a_hidden_folder_below_its_root(string root, string file, bool skipped)
    {
        Assert.Equal(skipped, Importer.Importer.IsUnderHiddenFolder(root, file));
    }
}
