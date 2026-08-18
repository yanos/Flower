using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Persistence;

using Microsoft.Extensions.DependencyInjection;

namespace Flower.Tests.TestSupport;

// Ioc.Default is a process-wide singleton that can only be configured once, so
// any test touching code that service-locates through it has to share one
// configuration for the whole test run. Only TrackRowControl (built by
// MusicListPanelTests) still needs anything out of it - a ColumnManager,
// registered here over a plain in-memory AppSettings so resolving it touches
// no disk.
//
// That one shared, immutable container is the *only* thing such a test can
// set up, which is the point of docs/ARCHITECTURE-REVIEW.md §2.3: code
// reaching into Ioc.Default cannot be given per-test dependencies. Controls
// resolving their own ViewModels are what is left of that pattern - see §4.2.
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
