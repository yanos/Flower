using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Flower.Services;

using Velopack.Sources;

namespace Flower.Desktop;

// Velopack's GitHub source, told which release to update from. Its own
// choice is all or nothing: with pre-releases off it never reaches a beta,
// and with them on it takes whatever was published last, an -alpha included,
// reading only that one release's feed. So the release is picked the way the
// Mac picks it (GitHubReleaseUpdater.Newest, by UpdateChannel), and handed
// over as the only release there is; reading its releases.<os>.json and
// downloading the package it names stay Velopack's.
sealed class ChannelGithubSource(HttpClient http)
    : GithubSource(VelopackUpdater.RepositoryUrl, accessToken: null, prerelease: true)
{
    // Set before each check (VelopackUpdater.FindAsync), since the setting
    // can change while the app runs.
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        using var releases = await GitHubReleaseUpdater.ListAsync(http, GitHubReleaseUpdater.Repository);
        return GitHubReleaseUpdater.Newest(releases.RootElement, Channel) is { } release
            ? [ToVelopack(release)]
            : [];
    }

    private static GithubRelease ToVelopack(JsonElement release) => new()
    {
        Name = Text(release, "tag_name"),
        Prerelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
        PublishedAt = DateTime.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var published) ? published : null,
        Assets = release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().Select(a => new GithubReleaseAsset
            {
                Name = Text(a, "name"),
                Url = Text(a, "url"),
                BrowserDownloadUrl = Text(a, "browser_download_url"),
                ContentType = Text(a, "content_type"),
            }).ToArray()
            : [],
    };

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
