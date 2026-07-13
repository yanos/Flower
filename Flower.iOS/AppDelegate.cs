using Avalonia;
using Avalonia.iOS;

using Foundation;

using Flower.Services;

namespace Flower.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the
// User Interface of the application, as well as listening (and optionally responding) to
// application events from iOS.
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Real iOS hardware can't do raw multicast without a hard-to-get Apple
        // entitlement - see PlatformMdns.cs and BonjourMdnsBackend's own doc
        // comment. Must be set before Avalonia (and, in turn, App.axaml.cs's DI
        // container) starts, same timing as Flower.Android's PlatformDataDirectory/
        // PlatformPermissions wiring in MainActivity.
        PlatformMdns.Current = new BonjourMdnsBackend();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
