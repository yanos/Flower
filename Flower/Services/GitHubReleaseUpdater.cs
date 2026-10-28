using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services
{
    // Finds a newer release by asking GitHub for the latest one and comparing
    // its tag with this build's version - for Flower.MacOS, whose dmg has no
    // updater of its own (see IAppUpdater). Installing is handing the dmg to
    // the browser to download: replacing a running app bundle in place is
    // what Velopack would do, and what needs the app signed.
    //
    // `releases/latest` never names a pre-release or a draft, so a stable
    // build is only ever offered a stable one. Read with JsonDocument rather
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

        public async Task<AppUpdate?> FindAsync(CancellationToken ct = default)
        {
            using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
            // No release at all yet is an answer, not a failure.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return Read(json.RootElement, AppVersion.Display, assetSuffix);
        }

        // The update in a `releases/latest` answer, if it is newer than
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
