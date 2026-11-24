using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

using Velopack;

namespace Flower.Desktop;

// Updates for a Flower installed by Setup.exe or run from the AppImage, read
// from the GitHub releases the `release` job in tests.yml publishes - each
// carries this OS's feed (releases.win.json, releases.linux.json) and the
// package it names. Which release is the update channel's choice
// (ChannelGithubSource); a pre-release that is not a beta is never offered.
//
// The new version is downloaded before anyone is told about it, so the
// banner's button restarts straight into it. Not pressing it costs nothing:
// VelopackApp.Run applies a downloaded update at the next launch anyway.
sealed class VelopackUpdater(UpdateManager manager, ChannelGithubSource source) : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/" + GitHubReleaseUpdater.Repository;

    // Null when this run was not installed by Velopack - `dotnet run`, the
    // IDE, a publish folder - which has nothing to update in place.
    public static VelopackUpdater? ForThisInstall()
    {
        var source = new ChannelGithubSource(GitHubReleaseUpdater.CreateClient());
        var manager = new UpdateManager(source);
        return manager.IsInstalled ? new VelopackUpdater(manager, source) : null;
    }

    // On the thread pool: the download hashes the whole package as it lands.
    public Task<AppUpdate?> FindAsync(UpdateChannel channel, CancellationToken ct = default) => Task.Run<AppUpdate?>(async () =>
    {
        source.Channel = channel;
        if (manager.UpdatePendingRestart is { } pending)
            return new DownloadedUpdate(manager, pending);

        if (await manager.CheckForUpdatesAsync() is not { } info)
            return null;

        await manager.DownloadUpdatesAsync(info, progress: null, cancelToken: ct);
        return new DownloadedUpdate(manager, info.TargetFullRelease);
    }, ct);

    private sealed class DownloadedUpdate(UpdateManager manager, VelopackAsset release)
        : AppUpdate(release.Version.ToString())
    {
        public override string InstallLabel => "Restart to Update";

        public override string Description =>
            $"Flower {Version} is ready. Restart to update, or it goes in the next time Flower starts.";

        public override void Install() => manager.ApplyUpdatesAndRestart(release);
    }
}
