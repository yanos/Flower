using System;
using System.IO;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.iOS;

using CommunityToolkit.Mvvm.DependencyInjection;

using Foundation;

using MetricKit;

using Microsoft.Extensions.Logging;

using UIKit;

using Flower.Logging;
using Flower.Manager;
using Flower.Services;

namespace Flower.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the
// User Interface of the application, as well as listening (and optionally responding) to
// application events from iOS.
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>, IMXMetricManagerSubscriber
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // SkiaSharp ships no ios-specific bindings assembly (its net10.0-ios*
        // lib group is an empty placeholder), so it falls back to the same
        // generic lib/net10.0/SkiaSharp.dll used on desktop, whose
        // DllImport("libSkiaSharp") is a bare name rather than the
        // @rpath-qualified string an ios-specific build would use (compare
        // HarfBuzzSharp, which does ship an ios-specific assembly and
        // resolves fine unaided). .NET-for-iOS's default P/Invoke probing
        // for that bare name never looks inside Frameworks/libSkiaSharp.framework -
        // same underlying limitation as MiniaudioSink.ResolveIosMiniaudio,
        // and it must run before base.CustomizeAppBuilder's .WithInterFont()/
        // UseSkia() below ever touches SkiaSharp.SKImageInfo.
        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKImageInfo).Assembly, ResolveIosSkiaSharp);

        // A native crash (e.g. inside libvlc) bypasses .NET's own exception
        // handling entirely - MetricKit is Apple's supported way for an app
        // to learn about its own past crashes without a third-party native-
        // crash library. Delivery is OS-batched and can arrive up to ~24h
        // after the crash (never on the very next launch), but by then
        // AppLogging.Initialize() will long since have run, so logging
        // directly from the callback (unlike Android's equivalent, which
        // needs a startup-order workaround - see PlatformCrashInfo) is fine.
        if (OperatingSystem.IsIOSVersionAtLeast(13))
            MXMetricManager.SharedManager.Add(this);

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

        // Do not activate this at launch: a non-mixing Playback session would
        // interrupt Music/Podcasts merely by opening Flower. GaplessAudioManager
        // activates it immediately before Flower starts rendering and releases
        // it on pause/stop, preserving background playback only while needed.
        PlatformAudioSession.Current = new AppleAudioSession();

        // Notification-based rather than overriding UIApplicationDelegate's
        // WillEnterForeground directly - the classic override isn't available
        // on however AvaloniaAppDelegate's base hooks into UIKit here (Scene
        // vs. plain UIApplicationDelegate lifecycle - see the SDK's own
        // deprecation note pointing at this same notification), and this
        // works regardless of which lifecycle model is in play. Only fires
        // when actually resuming from background (never on a cold launch),
        // so Ioc.Default is guaranteed already configured - see
        // NetworkDiscoveryService.Restart's own doc comment for why a browse
        // issued before suspension cannot be relied on afterwards.
        UIApplication.Notifications.ObserveWillEnterForeground((_, _) =>
            Ioc.Default.GetService<NetworkDiscoveryService>()?.Restart());

        return base.CustomizeAppBuilder(builder)
            .WithFlowerFonts();
    }

    private static IntPtr ResolveIosSkiaSharp(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "libSkiaSharp")
            return IntPtr.Zero;

        var path = Path.Combine(AppContext.BaseDirectory, "Frameworks", "libSkiaSharp.framework", "libSkiaSharp");
        return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }

    public void DidReceiveDiagnosticPayloads(MXDiagnosticPayload[] payloads)
    {
        // CrashDiagnostics is iOS 14+ only. MetricKit itself never invokes this
        // callback below 14 (diagnostics didn't exist as a concept pre-14; only
        // metrics did), but the analyzer can't know that from the Add(this) call
        // above being gated at 13 - this guard is what actually satisfies CA1416.
        // Plain nested foreach rather than SelectMany(payload => ...): the
        // guard-clause flow analysis CA1416 relies on doesn't reliably extend
        // into a captured lambda body, so CrashDiagnostics still needs to be
        // accessed directly in the guarded method body, not inside one.
        if (!OperatingSystem.IsIOSVersionAtLeast(14))
            return;

        var logger = AppLogging.CreateLogger("Flower.MetricKit");
        foreach (var payload in payloads)
        {
            foreach (var crash in payload.CrashDiagnostics ?? [])
            {
                logger.LogCritical(
                    "Native crash reported via MetricKit: signal={Signal} exceptionType={ExceptionType} exceptionCode={ExceptionCode} terminationReason={TerminationReason}",
                    crash.Signal, crash.ExceptionType, crash.ExceptionCode, crash.TerminationReason);
            }
        }
    }
}
