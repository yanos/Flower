using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.iOS;

using CommunityToolkit.Mvvm.DependencyInjection;

using Foundation;

using MetricKit;

using Microsoft.Extensions.Logging;

using UIKit;

using Flower.Logging;
using Flower.Audio;
using Flower.Persistence;
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
        // same underlying limitation as MiniaudioSink.ResolveIosFramework,
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

        // An exception thrown on the UI thread leaves .NET through
        // Objective-C, which ends the process without AppDomain's
        // UnhandledException ever being raised - found by crashing on
        // purpose. This is the one place iOS says so, and it is the crash.
        ObjCRuntime.Runtime.MarshalManagedException += (_, args) => App.RecordFatal(args.Exception);

        // Sentry's Cocoa SDK stays off - see KeepSentryCocoaOff.
        CrashReporting.ConfigurePlatform = KeepSentryCocoaOff;

        // Real iOS hardware can't do raw multicast without a hard-to-get Apple
        // entitlement - see PlatformMdns.cs and BonjourMdnsBackend's own doc
        // comment. Must be set before Avalonia (and, in turn, App.axaml.cs's DI
        // container) starts, same timing as Flower.Android's PlatformDataDirectory/
        // PlatformPermissions wiring in MainActivity.
        PlatformMdns.Current = new BonjourMdnsBackend();

        // The Keychain, so the device key and the pairing survive the app
        // being deleted and installed again - see ISecureStore. Before
        // Avalonia starts, since DeviceKeyStore and PairingBackup read it the
        // moment the container builds them.
        PlatformSecureStore.Current = new KeychainSecureStore();

        // Wi-Fi giving way to cellular, reported as it happens rather than
        // discovered from a request to an address that is gone - see
        // INetworkChangeSource. Same before-Avalonia-starts timing.
        PlatformNetworkChange.Current = new AppleNetworkChange();

        // Lock Screen / Control Center "Now Playing" card + transport
        // commands - see AppleNowPlaying.cs / docs/MEDIA-KEYS-PLAN.md Phase
        // 2. Same before-Avalonia-starts timing as PlatformMdns above, since
        // NowPlayingIntegrationService reads PlatformNowPlaying.Current once
        // at construction.
        PlatformNowPlaying.Current = new AppleNowPlaying();

        // Configured at launch but not activated: a non-mixing Playback session
        // would interrupt Music/Podcasts merely by opening Flower, and only
        // activation does that - the category has to be in place much earlier,
        // before the CoreAudio output unit exists (see AppleAudioSession's
        // constructor). GaplessAudioManager activates immediately before Flower
        // starts rendering and releases on pause/stop, preserving background
        // playback only while needed.
        //
        // A factory, unlike the three above, because AppleAudioSession takes an
        // injected ILogger<T> and there is no container here yet to resolve one
        // from; App.axaml.cs invokes this the moment there is. Nothing reads
        // the session before then - GaplessAudioManager, its only consumer, is
        // itself built by that container.
        PlatformAudioSession.Factory = loggerFactory =>
            new AppleAudioSession(loggerFactory.CreateLogger<AppleAudioSession>());

        // Apple's AirPlay/Bluetooth route button, hosted by
        // Flower.UserControls.RoutePickerControl wherever the mobile UI asks
        // for one. Same before-Avalonia-starts timing as the three above,
        // since RoutePickerControl reads Current once in its constructor.
        PlatformRoutePicker.Current = new AppleRoutePicker();

        // UIKit's own text field, laid over a box while it is typed in - see
        // INativeTextFieldFactory. Same before-Avalonia-starts timing: every
        // TextInput reads it as it is built.
        PlatformNativeTextField.Factory = new AppleNativeTextFieldFactory();

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

    // Sentry's Cocoa SDK sends its own crash events, and on iOS nothing puts
    // CrashReporting's BeforeSend in their path: a deliberate native crash
    // arrived with Cocoa's own install ID, a city, a permanent device hash and
    // the app container's paths, and it would have gone with the switch in
    // Settings off. So it is never started; the managed SDK still reports
    // everything managed, and a native crash comes through MetricKit
    // (DidReceiveDiagnosticPayloads, below).
    //
    // The switch that does it, InitNativeSdks, is internal to Sentry - the
    // only way to it is reflection, kept through trimming by the
    // DynamicDependency. If a Sentry update ever takes it away, this throws,
    // and CrashReporting.StartForApp then leaves Sentry off on iOS
    // altogether: no reports beats reports that leak.
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(Sentry.SentryOptions))]
    private static void KeepSentryCocoaOff(Sentry.SentryOptions options)
    {
        var property = typeof(Sentry.SentryOptions).GetProperty("InitNativeSdks", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.SetMethod == null)
            throw new InvalidOperationException("Sentry no longer has InitNativeSdks; not starting Sentry rather than let its Cocoa SDK send unscrubbed crashes");
        property.SetValue(options, false);
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

        // And it is how a native crash reaches crash reporting on iOS, where
        // Sentry's own native SDK is not started (CrashReporting.Configure
        // says why): scrubbed and throttled like any other report, or, with
        // reporting off or not running, left for the next launch's GitHub
        // banner.
        var logger = AppLogging.CreateLogger("Flower.MetricKit");
        foreach (var payload in payloads)
        {
            foreach (var crash in payload.CrashDiagnostics ?? [])
            {
                logger.LogCritical(
                    "Native crash reported via MetricKit: signal={Signal} exceptionType={ExceptionType} exceptionCode={ExceptionCode} terminationReason={TerminationReason}",
                    crash.Signal, crash.ExceptionType, crash.ExceptionCode, crash.TerminationReason);

                var tree = crash.CallStackTree?.JsonRepresentation is { } json
                    ? Foundation.NSString.FromData(json, Foundation.NSStringEncoding.UTF8)?.ToString()
                    : null;
                var pending = CrashReportScanner.DescribeMetricKitCrash(
                    crash.Signal, crash.ExceptionType, crash.TerminationReason, tree,
                    payload.TimeStampEnd is { } end ? new DateTimeOffset((DateTime)end) : DateTimeOffset.Now);
                if (CrashReporting.Running && CrashReporting.Enabled)
                    CrashReporting.CaptureNative(pending);
                else
                    pending.RecordUnlessPending();
            }
        }
    }
}
