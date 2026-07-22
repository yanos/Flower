using Avalonia;
using Avalonia.iOS;

using AVFoundation;

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

        // Lock Screen / Control Center "Now Playing" card + transport
        // commands - see AppleNowPlaying.cs / docs/MEDIA-KEYS-PLAN.md Phase
        // 2. Same before-Avalonia-starts timing as PlatformMdns above, since
        // NowPlayingIntegrationService reads PlatformNowPlaying.Current once
        // at construction.
        PlatformNowPlaying.Current = new AppleNowPlaying();

        // Info.plist already declares UIBackgroundModes=audio, but that alone
        // isn't enough - without an active session in the Playback category,
        // iOS doesn't consider this app entitled to keep running once the
        // screen locks or it backgrounds, and can throttle/suspend its
        // threads shortly after (not just LibVLC's own audio output).
        // Confirmed on a real device: the current track kept playing while
        // locked, but PlaylistControlViewModel's EndReached handler (which
        // runs the auto-advance-to-next-track logic) never got a chance to
        // run once the track actually finished, so playback just stopped
        // instead of continuing - Playback is the category iOS uses to
        // decide an app's audio (and, in practice, its ability to react to
        // that audio) shouldn't be cut off just because it's not visible.
        var audioSession = AVAudioSession.SharedInstance();
        audioSession.SetCategory(AVAudioSessionCategory.Playback.GetConstant(), out _);
        audioSession.SetActive(true, out _);

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
