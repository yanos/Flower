using System;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services
{
    // Where a newer Flower comes from, for a desktop installed from a release -
    // see docs/AUTO-UPDATE-PLAN.md. Everything about when to look and what to
    // say is shared (AppUpdatesViewModel); only finding and installing differ,
    // and they differ by head:
    //
    // - Flower.Desktop (Windows, Linux) was installed by Velopack, which
    //   downloads the new version in the background and swaps it in on a
    //   restart (Flower.Desktop's VelopackUpdater).
    // - Flower.MacOS ships as a plain dmg, because Velopack's macOS updates
    //   need a signed app. It can only say a newer one exists and fetch the
    //   dmg (GitHubReleaseUpdater).
    //
    // Null on the phones, which the stores update, in the browser, and on any
    // build that was not installed from a release - a run from the IDE or from
    // `dotnet run` has nothing to update in place, and offering it the latest
    // release would be telling a developer to downgrade.
    public interface IAppUpdater
    {
        // A newer release, ready to install, or null when this build is the
        // latest. Throws when it cannot tell - offline, GitHub refusing.
        Task<AppUpdate?> FindAsync(CancellationToken ct = default);
    }

    public abstract class AppUpdate(string version)
    {
        // As the release names it - "0.5.0".
        public string Version { get; } = version;

        // The button that installs it: "Restart to Update" where it is already
        // downloaded, "Download" where a person has to take it from there.
        public abstract string InstallLabel { get; }

        // What the banner says about it.
        public abstract string Description { get; }

        // Restarts into the new version, or starts its download. May not
        // return, in the first case.
        public abstract void Install();
    }

    // Set by the platform entry point before Avalonia starts - same
    // timing/convention as PlatformRoutePicker.Current.
    public static class PlatformAppUpdater
    {
        public static IAppUpdater? Current { get; set; }
    }
}
