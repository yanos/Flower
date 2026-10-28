using System.Text.Json;

using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// Reading GitHub's `releases/latest` answer for the dmg's update - the release
// carries the Windows and Linux installers and feeds too, so which file is
// handed to the browser matters.
public class GitHubReleaseUpdaterTests
{
    private const string Release = """
        {
          "tag_name": "v0.5.0",
          "html_url": "https://github.com/yanos/Flower/releases/tag/v0.5.0",
          "assets": [
            { "name": "Flower-0.5.0-win-x64-Setup.exe", "browser_download_url": "https://example.test/setup.exe" },
            { "name": "Flower-0.5.0-macos-arm64.dmg",   "browser_download_url": "https://example.test/flower.dmg" },
            { "name": "Flower.Desktop-0.5.0-full.nupkg", "browser_download_url": "https://example.test/full.nupkg" }
          ]
        }
        """;

    private static AppUpdate? Read(string json, string current)
    {
        using var document = JsonDocument.Parse(json);
        return GitHubReleaseUpdater.Read(document.RootElement, current, "-macos-arm64.dmg");
    }

    [Fact]
    public void A_newer_release_is_offered_with_its_dmg()
    {
        var update = Read(Release, "0.4.1");

        Assert.NotNull(update);
        Assert.Equal("0.5.0", update.Version);
        Assert.Contains("flower.dmg", Assert.IsType<GitHubReleaseUpdater.DownloadableUpdate>(update).Url);
    }

    [Fact]
    public void The_release_this_build_is_is_not_offered() =>
        Assert.Null(Read(Release, "0.5.0"));

    [Fact]
    public void A_release_without_a_dmg_sends_to_its_page()
    {
        var update = Read("""
            { "tag_name": "v0.5.0", "html_url": "https://github.com/yanos/Flower/releases/tag/v0.5.0", "assets": [] }
            """, "0.4.1");

        Assert.Contains("/releases/tag/v0.5.0", Assert.IsType<GitHubReleaseUpdater.DownloadableUpdate>(update).Url);
    }
}
