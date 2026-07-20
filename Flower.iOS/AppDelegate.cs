using Avalonia;
using Avalonia.iOS;

using CommunityToolkit.Mvvm.DependencyInjection;

using Foundation;

using UIKit;

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

        // Notification-based rather than overriding UIApplicationDelegate's
        // WillEnterForeground directly - the classic override isn't available
        // on however AvaloniaAppDelegate's base hooks into UIKit here (Scene
        // vs. plain UIApplicationDelegate lifecycle - see the SDK's own
        // deprecation note pointing at this same notification), and this
        // works regardless of which lifecycle model is in play. Only fires
        // when actually resuming from background (never on a cold launch),
        // so Ioc.Default is guaranteed already configured - see
        // NetworkDiscoveryService.Restart/SyncHttpServer.Restart's own doc
        // comments for why both are needed here. SyncHttpServer restarts
        // first so the mDNS advertisement below points at whatever port it
        // actually rebinds to (almost always the same DefaultPort, but
        // Start()'s own port-hunting logic is what decides that, not this
        // code).
        UIApplication.Notifications.ObserveWillEnterForeground((_, _) =>
        {
            var networkDiscovery = Ioc.Default.GetService<NetworkDiscoveryService>();
            var syncHttpServer = Ioc.Default.GetService<SyncHttpServer>();
            syncHttpServer?.Restart();
            networkDiscovery?.Restart(syncHttpServer?.BoundPort ?? SyncHttpServer.DefaultPort);
        });

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
