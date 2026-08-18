using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Persistence;

using Microsoft.Extensions.DependencyInjection;

namespace Flower.Tests.TestSupport;

// Ioc.Default is a process-wide singleton that can only be configured once,
// so any test touching code that service-locates through it (AlbumArtLoader's
// remote-art path) has to share one configuration for the whole test run.
// AlbumArtLoader's tests want it to hold *nothing* - GetService returning
// null is the "no peer to fetch from" branch they exercise - while
// TrackRowControl (built by MusicListPanelTests) genuinely needs a
// ColumnManager out of it. The
// ColumnManager registered here is constructed over a plain in-memory
// AppSettings, so resolving it touches no disk.
//
// That one shared, immutable container is the *only* thing a test can
// reasonably set up here is the point of docs/ARCHITECTURE-REVIEW.md §2.3 -
// code reaching into Ioc.Default cannot be given per-test dependencies.
internal static class TestIoc
{
    private static readonly object Gate = new();
    private static bool _configured;

    public static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured)
                return;

            var services = new ServiceCollection();
            services.AddSingleton(new ColumnManager(new AppSettings(), new AppSettingsStore()));
            Ioc.Default.ConfigureServices(services.BuildServiceProvider());
            _configured = true;
        }
    }
}
