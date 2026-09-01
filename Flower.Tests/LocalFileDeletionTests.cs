using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class LocalFileDeletionTests
{
    [Fact]
    public void LocalFiles_omits_remote_placeholders()
    {
        Track[] tracks =
        [
            new() { Path = "/music/downloaded.mp3", IsLocallyDownloaded = true },
            new() { Path = "/music/imported.mp3" },
            new(),
        ];

        var localFiles = LocalFileDeletion.LocalFiles(tracks);

        Assert.Equal(2, localFiles.Count);
        Assert.DoesNotContain(localFiles, track => track.Path == null);
    }

    [Fact]
    public void RequiresWarning_only_for_files_not_downloaded_from_a_server()
    {
        var downloaded = new Track
        {
            Path = "/music/downloaded.mp3",
            IsLocallyDownloaded = true,
        };
        // An imported file can still have an origin fingerprint after a sync
        // merge. IsLocallyDownloaded, not the fingerprint, is the source of
        // truth for whether this device fetched its bytes from a server.
        var imported = new Track
        {
            Path = "/music/imported.mp3",
            OriginDeviceFingerprint = "server-that-also-has-it",
            IsLocallyDownloaded = false,
        };

        Assert.False(LocalFileDeletion.RequiresWarning([downloaded]));
        Assert.True(LocalFileDeletion.RequiresWarning([downloaded, imported]));
    }
}
