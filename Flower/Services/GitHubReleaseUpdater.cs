using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services
{
    // Finds a newer release by listing GitHub's releases, picking the newest
    // the update channel offers (UpdateChannel), and comparing its tag with
    // this build's version - for Flower.MacOS, whose dmg has no updater of
    // its own (see IAppUpdater). Installing is handing the dmg to the browser
    // to download: replacing a running app bundle in place is what Velopack
    // would do, and what needs the app signed. Flower.Desktop's Velopack
    // source picks its release here too (ChannelGithubSource).
    //
    // Not `releases/latest`, which only ever names the newest stable release
    // and so cannot serve the beta channel. Read with JsonDocument rather
    // than a serializer, because the head this runs in is trimmed.
    public sealed class GitHubReleaseUpdater(HttpClient http, string repository, string assetSuffix) : IAppUpdater
    {
        public const string Repository = "yanos/Flower";

        // GitHub refuses an API request with no User-Agent.
        public static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Flower", AppVersion.Display));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        }

        // GitHub's largest page. The list comes newest first, so a channel's
        // release is missed only behind a hundred newer ones it skips.
        public const int ListedReleases = 100;

        public async Task<AppUpdate?> FindAsync(UpdateChannel channel, CancellationToken ct = default)
        {
            using var releases = await ListAsync(http, repository, ct);
            return Newest(releases.RootElement, channel) is { } release
                ? Read(release, AppVersion.Display, assetSuffix)
                : null;
        }

        // The repository's releases, newest first. No release at all yet is
        // an empty list, not a failure.
        public static async Task<JsonDocument> ListAsync(HttpClient http, string repository, CancellationToken ct = default)
        {
            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{repository}/releases?per_page={ListedReleases}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return JsonDocument.Parse("[]");
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }

        // The release in a listing that `channel` offers with the highest
        // version - by version rather than by date, so a fix to an older line
        // published after a newer one is not taken for the newest. Drafts
        // are never listed without a token, but are skipped anyway.
        public static JsonElement? Newest(JsonElement releases, UpdateChannel channel)
        {
            if (releases.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? best = null;
            string? bestTag = null;
            foreach (var release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    continue;
                if (!release.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName)
                    continue;
                if (!channel.Offers(tagName))
                    continue;
                if (bestTag is null || ReleaseVersion.IsNewer(tagName, bestTag))
                {
                    best = release;
                    bestTag = tagName;
                }
            }
            return best;
        }

        // The update in one release of the listing, if it is newer than
        // `current`. Prefers the file ending in `assetSuffix` (the dmg), and
        // falls back to the release's page when it has none.
        public static AppUpdate? Read(JsonElement release, string current, string assetSuffix)
        {
            if (!release.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName)
                return null;
            if (!ReleaseVersion.IsNewer(tagName, current))
                return null;

            var download = release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out var name)
                        && name.GetString()?.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(a => a.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null)
                    .FirstOrDefault(url => url != null)
                : null;
            download ??= release.TryGetProperty("html_url", out var page) ? page.GetString() : null;
            if (download is null)
                return null;

            var version = tagName.TrimStart('v', 'V');
            return new DownloadableUpdate(version, download);
        }

        internal sealed class DownloadableUpdate(string version, string url) : AppUpdate(version)
        {
            public string Url { get; } = url;

            public override string InstallLabel => "Download";

            public override string Description =>
                $"Flower {Version} is available. Open the download, then drag Flower into Applications to replace this one.";

            public override void Install() => PlatformShell.OpenUrl(Url);
        }
    }
}
