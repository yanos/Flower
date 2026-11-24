using System.Text.Json;

using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// Picking the update from GitHub's list of releases - the newest the channel
// offers, by version - and reading the dmg's out of it: the release carries
// the Windows and Linux installers and feeds too, so which file is handed to
// the browser matters. Velopack's source picks its release the same way.
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

    private static string? Newest(UpdateChannel channel, params string[] tags)
    {
        var listing = "[" + string.Join(",", System.Linq.Enumerable.Select(tags, t => t.StartsWith('!')
            ? $$"""{ "tag_name": "{{t[1..]}}", "draft": true }"""
            : $$"""{ "tag_name": "{{t}}" }""")) + "]";
        using var document = JsonDocument.Parse(listing);
        return GitHubReleaseUpdater.Newest(document.RootElement, channel)?.GetProperty("tag_name").GetString();
    }

    [Fact]
    public void Stable_takes_the_newest_release_and_skips_every_pre_release() =>
        Assert.Equal("v0.11.0", Newest(UpdateChannel.Stable, "v0.12.0-beta.1", "v0.12.0-alpha.3", "v0.11.0", "v0.10.1"));

    [Fact]
    public void Beta_takes_a_beta_newer_than_the_newest_release() =>
        Assert.Equal("v0.12.0-beta.2", Newest(UpdateChannel.Beta, "v0.12.0-alpha.3", "v0.12.0-beta.2", "v0.12.0-beta.1", "v0.11.0"));

    [Fact]
    public void Beta_takes_the_release_once_it_passes_its_betas() =>
        Assert.Equal("v0.12.0", Newest(UpdateChannel.Beta, "v0.12.0", "v0.12.0-beta.2", "v0.11.0"));

    [Fact]
    public void Beta_never_takes_another_pre_release_however_new() =>
        Assert.Equal("v0.11.0", Newest(UpdateChannel.Beta, "v0.13.0-alpha.1", "v0.12.0-rc.1", "v0.11.0"));

    [Fact]
    public void A_fix_to_an_older_line_published_last_is_not_the_newest() =>
        Assert.Equal("v0.12.0-beta.1", Newest(UpdateChannel.Beta, "v0.11.1", "v0.12.0-beta.1", "v0.11.0"));

    [Fact]
    public void A_draft_is_never_offered() =>
        Assert.Equal("v0.11.0", Newest(UpdateChannel.Stable, "!v0.12.0", "v0.11.0"));

    [Fact]
    public void Nothing_offered_is_no_release() =>
        Assert.Null(Newest(UpdateChannel.Stable, "v0.12.0-beta.1"));
}
