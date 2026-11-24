using Flower.Models;
using Flower.Services;

namespace Flower.Tests.MusicLibrary;

public class LocalFileDeletionTests
{
    private const string Paired = "paired-server";

    private static Track Download(string? origin) => new()
    {
        Path = "/music/downloaded.mp3",
        OriginDeviceFingerprint = origin,
        IsLocallyDownloaded = true,
    };

    [Fact]
    public void A_download_its_paired_server_still_has_needs_no_warning()
    {
        Assert.False(LocalFileDeletion.RequiresWarning([Download(Paired)], Paired));
    }

    // UF-5: IsLocallyDownloaded outlives the server the file came from.
    [Theory]
    [InlineData(null, Paired)]            // The server stopped listing it.
    [InlineData("old-server", Paired)]    // From a server this device left.
    [InlineData(Paired, null)]            // Unpaired since.
    public void A_download_with_no_server_behind_it_is_the_only_copy(string? origin, string? paired)
    {
        Assert.True(LocalFileDeletion.RequiresWarning([Download(origin)], paired));
    }

    // An imported file can carry an origin after a sync merge, and is still
    // the user's own rather than cache.
    [Fact]
    public void An_imported_file_is_warned_about_even_when_the_server_has_it()
    {
        var imported = new Track
        {
            Path = "/music/imported.mp3",
            OriginDeviceFingerprint = Paired,
            IsLocallyDownloaded = false,
        };

        Assert.True(LocalFileDeletion.RequiresWarning([Download(Paired), imported], Paired));
    }

    // The phone deletes outright, having no trash, and used to describe any
    // file its server has as a download it could fetch again: an imported
    // file included, which is the user's own. Both heads ask the same
    // question now, with or without a trash.
    [Theory]
    [InlineData("Trash")]
    [InlineData(null)]
    public void An_imported_file_the_server_has_is_not_described_as_a_download(string? trash)
    {
        var imported = new Track
        {
            Path = "/music/imported.mp3",
            OriginDeviceFingerprint = Paired,
            IsLocallyDownloaded = false,
        };

        Assert.NotEqual(
            LocalFileDeletion.WarningMessage([Download(Paired)], Paired, trash),
            LocalFileDeletion.WarningMessage([imported], Paired, trash));
    }
}
