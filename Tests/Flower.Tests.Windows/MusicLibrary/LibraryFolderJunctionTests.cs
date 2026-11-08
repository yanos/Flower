using System.Diagnostics;

using Flower.Services;

namespace Flower.Tests.Windows.MusicLibrary;

// A drive linked into the library on Windows is usually a junction, which
// needs no privilege to make, unlike a symbolic link - and a junction whose
// drive is gone is not reported the way a broken symbolic link is: Windows
// can still see the junction itself. LibraryFolders.WasDeleted must not take
// songs that vanished with the drive for songs somebody deleted.
public class LibraryFolderJunctionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-junction-library").FullName;
    private readonly string _drive = Directory.CreateTempSubdirectory("flower-junction-drive").FullName;

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _drive })
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    // External in the library folder, joined to the drive.
    private string Junction()
    {
        var link = Path.Combine(_root, "External");
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_drive}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, mklink.StandardError.ReadToEnd());
        return link;
    }

    [Fact]
    public void A_song_on_a_junctioned_drive_that_is_gone_was_not_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");

        Touch(Path.Combine(_root, "keep.mp3"));
        var link = Junction();
        var song = Path.Combine(link, "Album", "song.mp3");
        Touch(song);
        Assert.True(File.Exists(song));

        Directory.Delete(_drive, recursive: true);

        Assert.False(File.Exists(song));
        Assert.False(LibraryFolders.WasDeleted(song, _root));
    }

    // A drive that is there but empty, its songs straight under the junction:
    // the empty folder is the songs' own, and still not a deletion, because it
    // is the junction itself - what a volume mounted into a folder leaves too.
    [Fact]
    public void A_song_whose_own_folder_is_an_emptied_junction_was_not_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");

        Touch(Path.Combine(_root, "keep.mp3"));
        var link = Junction();
        var song = Path.Combine(link, "song.mp3");
        Touch(song);

        File.Delete(Path.Combine(_drive, "song.mp3"));

        Assert.False(LibraryFolders.WasDeleted(song, _root));
    }

    // The junction is no obstacle to a real deletion through it.
    [Fact]
    public void A_song_deleted_through_a_junction_that_is_there_was_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");

        var link = Junction();
        var song = Path.Combine(link, "Album", "a.mp3");
        Touch(song);
        Touch(Path.Combine(link, "Album", "b.mp3"));

        File.Delete(song);

        Assert.True(LibraryFolders.WasDeleted(song, _root));
    }
}
