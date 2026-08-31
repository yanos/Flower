using System;

using AVFoundation;

using CoreFoundation;

using Foundation;

using Microsoft.Extensions.Logging;

using Flower.Manager;

namespace Flower.iOS;

// Own the process-wide AVAudioSession only while Flower has audio to render.
// Playback is intentionally non-mixing: Flower's music should replace another
// music app only after the user explicitly presses Play. The playback category
// supports AirPlay and, with AllowBluetoothA2DP, stereo Bluetooth output such
// as AirPods rather than falling back to the handset speaker.
//
// The route sharing policy is what decides whether AVRoutePickerView (see
// AppleRoutePicker) can offer AirPlay 2 receivers - HomePods, an Apple TV, a
// multi-room group - or only the legacy AirPlay 1 devices the default policy
// exposes. LongFormAudio is Apple's declaration of "this app plays music or
// podcasts, not UI sounds", and is only accepted alongside the playback
// category with the default mode, which is exactly what is set here.
//
// Nothing about that policy or the picker requires Flower's audio to be
// rendered by AVAudioEngine: on iOS the *session* owns the route, and every
// output unit in the process follows it - including the CoreAudio RemoteIO
// unit MiniaudioSink's ma_device ends up on. See
// docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2 for why that is worth stating.
public sealed class AppleAudioSession : IPlatformAudioSession, IDisposable
{
    private readonly ILogger<AppleAudioSession> _logger;
    private readonly AVAudioSession _session = AVAudioSession.SharedInstance();
    private readonly NSObject _routeChangeObserver;

    public event EventHandler? OutputDeviceLost;

    // Injected, not fetched from AppLogging: this is an ordinary instance class
    // with a constructor, and AppLogging is the hatch for the cases that have
    // nowhere to inject into - static classes (VlcNativeSetup) and
    // UnmanagedCallersOnly callbacks (BonjourMdnsBackend). AppDelegate can't
    // supply a logger itself, since it runs before the container exists, so it
    // registers a PlatformAudioSession.Factory and the composition root calls
    // it once logging is up.
    public AppleAudioSession(ILogger<AppleAudioSession> logger)
    {
        _logger = logger;

        // Registered for the app's whole life, not just while the session is
        // active: a route change that arrives a moment after playback stopped
        // is still worth knowing about, and re-registering around activation
        // would be one more thing to get wrong.
        _routeChangeObserver = AVAudioSession.Notifications.ObserveRouteChange(OnRouteChange);
    }

    public void ActivateForPlayback()
    {
        var categoryOptions = AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
        if (!_session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                                  AVAudioSessionRouteSharingPolicy.LongFormAudio, categoryOptions,
                                  out var categoryError))
        {
            _logger.LogWarning("Could not configure the iOS playback audio session: {Error}", categoryError);
            return;
        }

        if (!_session.SetActive(true, out var activationError))
            _logger.LogWarning("Could not activate the iOS playback audio session: {Error}", activationError);
    }

    public void DeactivateAfterPlayback()
    {
        // Tell a player Flower interrupted (e.g. Music) that it may resume.
        if (!_session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var deactivationError))
            _logger.LogWarning("Could not deactivate the iOS playback audio session: {Error}", deactivationError);
    }

    // Route changes arrive for plenty of reasons that are none of Flower's
    // business - Flower itself setting the category above raises
    // CategoryChange, and picking an AirPlay speaker raises Override or
    // NewDeviceAvailable, both of which should keep playing on the new route.
    // OldDeviceUnavailable is the single reason that means "what they were
    // listening on is gone", and it is the only one acted on.
    //
    // MiniaudioSink.HandleReroute is the same judgement made the hard way, for
    // every other platform: miniaudio's rerouted notification carries no
    // reason at all, so it has to re-enumerate and check whether the device it
    // was on is still there. This is the version of that with the answer
    // handed over.
    private void OnRouteChange(object? sender, AVAudioSessionRouteChangeEventArgs e)
    {
        _logger.LogInformation("iOS audio route changed: {Reason}", e.Reason);

        if (e.Reason != AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
            return;

        // AVFoundation posts this on its own notification thread; the handler
        // ends up updating ViewModel state, so it has to land on the main
        // thread - which is Avalonia's UI thread on iOS.
        DispatchQueue.MainQueue.DispatchAsync(() => OutputDeviceLost?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose() => _routeChangeObserver.Dispose();
}
