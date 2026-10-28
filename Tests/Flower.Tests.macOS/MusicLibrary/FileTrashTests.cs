using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.macOS.MusicLibrary;

[Collection("PlatformDataDirectory")]
public class FileTrashTests : PinnedDataDirectory
{
    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    // Into the real Trash and straight back out again, exercising the native API.
    [Fact]
    public void On_macOS_a_file_goes_to_the_real_Trash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires the native macOS Trash API.");

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
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires the native macOS Trash API.");

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
}
