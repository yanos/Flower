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
    private readonly NSObject _interruptionObserver;

    // Set between an interruption beginning and ending. Read only by
    // DeactivateAfterPlayback, to keep it from arguing with the OS - see there.
    private bool _interrupted;

    public event EventHandler? OutputDeviceLost;
    public event EventHandler? PlaybackInterrupted;
    public event EventHandler<PlaybackInterruptionEndedEventArgs>? PlaybackInterruptionEnded;

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

        // The category is set here, at startup, and not left to the first
        // ActivateForPlayback below. It has to be in place before the CoreAudio
        // output unit is created, which happens well before anything is played:
        // GaplessAudioManager starts its sink from its own constructor, and
        // MiniaudioSink builds the ma_device there. A unit created while the
        // session still holds iOS's process default - SoloAmbient - behaves the
        // way that category says even once Playback is set underneath it: muted
        // by the ringer switch (so audio reaches AirPods but never the built-in
        // speaker) and stopped when the screen locks, background mode in
        // Info.plist or not.
        //
        // Doing this at launch does not interrupt whatever else is playing.
        // Only *activating* a non-mixing session does that, and activation
        // still waits for real playback - which is the whole point of the split
        // between this and ActivateForPlayback.
        ConfigureCategory();
        LogSessionState("configured at startup");

        // Registered for the app's whole life, not just while the session is
        // active: a route change that arrives a moment after playback stopped
        // is still worth knowing about, and re-registering around activation
        // would be one more thing to get wrong.
        _routeChangeObserver = AVAudioSession.Notifications.ObserveRouteChange(OnRouteChange);

        // Same lifetime, and for a stronger reason: the *end* of an
        // interruption is the notification that matters most, and it arrives
        // when Flower is - by definition - not the app holding the session.
        _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(OnInterruption);
    }

    public void ActivateForPlayback()
    {
        // Set again rather than assumed to still hold: the category is
        // process-wide state, and an interruption or another app in the same
        // process space can leave it somewhere else. Re-setting an unchanged
        // category is a no-op.
        //
        // Activation goes ahead even when that failed. A session in the wrong
        // category still plays; a session that is never made active does not,
        // so a category problem must not be allowed to turn into silence.
        ConfigureCategory();

        if (!_session.SetActive(true, out var activationError))
            _logger.LogWarning("Could not activate the iOS playback audio session: {Error}", activationError);

        LogSessionState("activated for playback");
    }

    // Playback, with the long-form route sharing policy AVRoutePickerView needs
    // to offer AirPlay 2 receivers, and A2DP so Bluetooth output is stereo
    // music rather than the mono headset path.
    //
    // Written as a ladder rather than one call because the two things this asks
    // for are not equally important. The route sharing policy buys AirPlay 2
    // receivers in the picker; iOS accepts it only alongside an exact
    // combination of category, mode and options, and rejects the whole call if
    // anything about that combination displeases it. Playback itself is what
    // makes Flower a music app at all - audible with the ringer switch down,
    // alive with the screen locked. Losing the first is a missing feature;
    // losing the second is the app not working, so it must not be possible for
    // one refused call to cost both.
    private bool ConfigureCategory()
    {
        if (_session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                                 AVAudioSessionRouteSharingPolicy.LongFormAudio,
                                 AVAudioSessionCategoryOptions.AllowBluetoothA2DP,
                                 out var longFormError))
        {
            return true;
        }

        _logger.LogWarning(
            "Could not configure the iOS audio session for long-form audio: {Error}. Falling back to plain playback - the route picker may only offer legacy AirPlay devices.",
            longFormError);

        if (_session.SetCategory(AVAudioSession.CategoryPlayback,
                                 AVAudioSessionCategoryOptions.AllowBluetoothA2DP,
                                 out var playbackError))
        {
            return true;
        }

        _logger.LogWarning("Could not configure the iOS audio session for playback with A2DP: {Error}", playbackError);

        // Last rung: the bare category, no options at all. If even this fails
        // the session is whatever iOS made it, which is SoloAmbient - silent
        // through the speaker with the ringer switch down, and stopped by the
        // screen locking.
        if (_session.SetCategory(AVAudioSession.CategoryPlayback, out var bareError))
            return true;

        _logger.LogError("Could not put the iOS audio session into the playback category at all: {Error}", bareError);
        return false;
    }

    // What the session actually is, as opposed to what was asked for. Logged
    // rather than inspected in a debugger because the answer is only true on a
    // real device: the simulator's session is not the one that mutes the
    // speaker or ends playback at the lock screen.
    private void LogSessionState(string when)
    {
        var outputs = _session.CurrentRoute?.Outputs;
        var route = outputs is { Length: > 0 }
            ? string.Join(", ", Array.ConvertAll(outputs, o => $"{o.PortType} ({o.PortName})"))
            : "nothing";

        _logger.LogInformation(
            "iOS audio session {When}: category={Category} mode={Mode} policy={Policy} options={Options} route={Route} otherAudioPlaying={OtherAudio}",
            when, _session.Category, _session.Mode, _session.RouteSharingPolicy, _session.CategoryOptions,
            route, _session.OtherAudioPlaying);
    }

    public void DeactivateAfterPlayback()
    {
        // An interruption has already taken the session away, and the pause it
        // caused comes straight back here. Handing it back a second time is at
        // best a no-op and at worst an error the OS is right to refuse, so the
        // only thing it would reliably produce is a warning in the log for
        // every phone call. The app that interrupted Flower does not need
        // telling it may resume - it is the one playing.
        if (_interrupted)
            return;

        // Tell a player Flower interrupted (e.g. Music) that it may resume.
        if (!_session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var deactivationError))
            _logger.LogWarning("Could not deactivate the iOS playback audio session: {Error}", deactivationError);
    }

    // A call, Siri, an alarm, or another app claiming the session. iOS has
    // already silenced Flower by the time this arrives; both halves are
    // reported so the app's own state can follow, and so playback can pick up
    // again afterwards when the OS says that is welcome.
    //
    // ShouldResume is the OS's answer, not a guess: it is absent when the user
    // ended up somewhere that expects them to press play themselves. Whether
    // Flower was even playing when the call came in is a separate question, and
    // one this class deliberately does not track - GaplessAudioManager knows it
    // first-hand, so it is the one that decides.
    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        _logger.LogInformation("iOS audio interrupted: {Type} (options {Options})", e.InterruptionType, e.Option);

        if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
        {
            _interrupted = true;
            DispatchQueue.MainQueue.DispatchAsync(() => PlaybackInterrupted?.Invoke(this, EventArgs.Empty));
            return;
        }

        _interrupted = false;
        var shouldResume = e.Option.HasFlag(AVAudioSessionInterruptionOptions.ShouldResume);
        DispatchQueue.MainQueue.DispatchAsync(
            () => PlaybackInterruptionEnded?.Invoke(this, new PlaybackInterruptionEndedEventArgs(shouldResume)));
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

    public void Dispose()
    {
        _routeChangeObserver.Dispose();
        _interruptionObserver.Dispose();
    }
}
