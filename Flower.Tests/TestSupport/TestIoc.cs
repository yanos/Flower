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
// reaching into Ioc.Default cannot be given per-test dependencies. The
// ColumnManager below is all that is left of the pattern - MusicListView is
// built by XAML and has no DataContext by the time its panel must exist, so
// it is the one deliberate exception; see its own constructor comment.
//
// "Touches no disk" was wrong about the ColumnManager, and expensively so. It
// subscribes to every column's PropertyChanged and schedules a fire-and-forget
// save 500ms later - a width change during a resize gesture is enough - so this
// throwaway AppSettings gets written to settings.json for real, at whatever
// AppDataDirectory resolves to when the timer fires. Being a process-wide
// singleton, that is long after the test that moved the column, in some other
// class entirely. It overwrote the developer's own settings.json with a default
// AppSettings; see AssemblySetup.DefaultDataDirectory, which is what now makes
// that land somewhere disposable instead of somewhere real.
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
