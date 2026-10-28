using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Windows.MusicLibrary;

[Collection("PlatformDataDirectory")]
public class FileTrashTests : PinnedDataDirectory
{
    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    // Proves the native call succeeds without a dialog and removes the source file.
    [Fact]
    public void On_Windows_a_file_goes_to_the_Recycle_Bin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires the native Windows Recycle Bin API.");

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
}
