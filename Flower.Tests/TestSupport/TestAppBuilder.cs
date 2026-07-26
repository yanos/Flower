using Avalonia;
using Avalonia.Headless;

using Flower.Tests.TestSupport;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Flower.Tests.TestSupport;

// Gives [AvaloniaFact] tests a real Dispatcher.UIThread to run against,
// headlessly - no window/rendering needed, just enough of an Avalonia app
// for Dispatcher.UIThread.Post (used by PlaylistControlViewModel's
// EndReached handler) to actually execute instead of throwing/no-op'ing
// without one.
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
