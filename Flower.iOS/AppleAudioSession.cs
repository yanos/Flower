using System;

using AVFoundation;

using CoreFoundation;

using Foundation;

using Microsoft.Extensions.Logging;

using Flower.Audio;

namespace Flower.iOS;

// Own the process-wide AVAudioSession only while Flower has audio to render.
// Playback is intentionally non-mixing: Flower's music should replace another
// music app only after the user explicitly presses Play. The playback category
// supports AirPlay and stereo Bluetooth output such as AirPods rather than
// falling back to the handset speaker.
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
    private readonly NSObject _mediaServicesLostObserver;
    private readonly NSObject _mediaServicesResetObserver;

    // Set between an interruption beginning and ending. Read only by
    // DeactivateAfterPlayback, to keep it from arguing with the OS - see there.
    private bool _interrupted;

    public event EventHandler? OutputDeviceLost;
    public event EventHandler? PlaybackInterrupted;
    public event EventHandler<PlaybackInterruptionEndedEventArgs>? PlaybackInterruptionEnded;

    // Injected, not fetched from AppLogging: this is an ordinary instance class
    // with a constructor, and AppLogging is the hatch for the cases that have
    // nowhere to inject into - UnmanagedCallersOnly callbacks
    // (BonjourMdnsBackend). AppDelegate can't
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
        // Mixable at launch, and only at launch. The theory that setting a
        // category is inert until something activates the session does not
        // survive contact with the device: opening Flower while another app
        // was playing cut that app off, before a single note of Flower's own
        // had been asked for. Whether it is setCategory itself or CoreAudio
        // implicitly activating the session when MiniaudioSink initialises its
        // output unit - which also happens at startup, from
        // GaplessAudioManager's constructor - does not much matter, because
        // MixWithOthers answers both: a mixable session takes nobody's audio
        // away, activated or not.
        //
        // The category underneath it is still Playback, which is the part that
        // had to be in place this early. What is deferred is only the claim to
        // be the one app playing, and that is ActivateForPlayback's to make.
        ConfigureCategory(SessionShape.Silent);
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
        _mediaServicesLostObserver = AVAudioSession.Notifications.ObserveMediaServicesWereLost(OnMediaServicesWereLost);
        _mediaServicesResetObserver = AVAudioSession.Notifications.ObserveMediaServicesWereReset(OnMediaServicesWereReset);
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
        //
        // This is also where the mixable launch category is dropped: from here
        // on Flower is the app playing, so it is entitled to say so.
        ConfigureCategory(SessionShape.Playing);

        if (!_session.SetActive(true, out var activationError))
            _logger.LogWarning("Could not activate the iOS playback audio session: {Error}", activationError);

        LogSessionState("activated for playback");
    }

    private enum SessionShape
    {
        // Playback, but mixable - the shape for every moment Flower is not
        // actually rendering, so that merely existing costs another app
        // nothing.
        Silent,

        // Playback proper: non-mixing, long-form, the app in charge of the
        // route.
        Playing,
    }

    // Playback, with the long-form route sharing policy AVRoutePickerView needs
    // to offer AirPlay 2 receivers. Bluetooth output is stereo A2DP rather than
    // the mono headset path without asking: AllowBluetoothA2DP is implicit for
    // the playback category, and is one of the options iOS refuses to be *told*
    // about there.
    //
    // That refusal is why this used to fail on every single call. Passing
    // AllowBluetoothA2DP alongside Playback is not a no-op that iOS shrugs at -
    // it is an invalid option for the category, so setCategory returns
    // paramErr (OSStatus -50) and applies nothing. Both of the first two rungs
    // passed it, so both always failed, and the session was left on the bare
    // third rung with the default route sharing policy: no AirPlay 2 receivers
    // in the picker, and a warning pair in the log for every play, pause,
    // resume and route change (1,056 of them in one day of phone logs).
    //
    // Still written as a ladder, because the two things this asks for are not
    // equally important. The route sharing policy buys AirPlay 2 receivers in
    // the picker; iOS accepts it only alongside an exact combination of
    // category, mode and options, and rejects the whole call if anything about
    // that combination displeases it. Playback itself is what makes Flower a
    // music app at all - audible with the ringer switch down, alive with the
    // screen locked. Losing the first is a missing feature; losing the second
    // is the app not working, so it must not be possible for one refused call
    // to cost both.
    private bool ConfigureCategory(SessionShape shape)
    {
        if (shape == SessionShape.Silent)
        {
            if (_session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                                     AVAudioSessionRouteSharingPolicy.Default,
                                     AVAudioSessionCategoryOptions.MixWithOthers,
                                     out var mixableError))
            {
                return true;
            }

            // Falling through to the playing ladder rather than giving up: a
            // session left in iOS's SoloAmbient default is an app that goes
            // silent with the ringer switch down and stops at the lock screen,
            // which is worse than one that is rude to whatever was playing.
            LogConfigurationFailure(
                "Could not put the iOS audio session into mixable playback: {Error}. Falling back to the ordinary playback category - another app's audio may stop when Flower opens its output device.",
                mixableError);
        }

        if (_session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                                 AVAudioSessionRouteSharingPolicy.LongFormAudio,
                                 default,
                                 out var longFormError))
        {
            return true;
        }

        LogConfigurationFailure(
            "Could not configure the iOS audio session for long-form audio: {Error}. Falling back to plain playback - the route picker may only offer legacy AirPlay devices.",
            longFormError);

        // Last rung: the bare category, no mode, policy or options at all. If
        // even this fails the session is whatever iOS made it, which is
        // SoloAmbient - silent through the speaker with the ringer switch down,
        // and stopped by the screen locking.
        if (_session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                                 AVAudioSessionRouteSharingPolicy.Default, default, out var bareError))
        {
            return true;
        }

        _logger.LogError("Could not put the iOS audio session into the playback category at all: {Error}", bareError);
        return false;
    }

    // ConfigureCategory runs on every play, pause, resume and route change, so
    // a failure that is a standing condition rather than an event - the wrong
    // iOS version, an option this OS will not take - reports itself hundreds of
    // times a day and buries everything else in the log. Only a *change* is
    // news: the same message with the same error is logged once and then
    // counted, and the count rides along with the next distinct one.
    private string? _lastConfigurationFailure;
    private int _repeatedConfigurationFailures;

    private void LogConfigurationFailure(string template, NSError? error)
    {
        var signature = $"{template}|{error?.Code}|{error?.Domain}";
        if (signature == _lastConfigurationFailure)
        {
            _repeatedConfigurationFailures++;
            return;
        }

        if (_repeatedConfigurationFailures > 0)
        {
            _logger.LogWarning(
                "The previous iOS audio session configuration warning repeated {RepeatCount} more time(s) before this one",
                _repeatedConfigurationFailures);
        }

        _lastConfigurationFailure = signature;
        _repeatedConfigurationFailures = 0;
        _logger.LogWarning(template, error);
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
            "iOS audio session {When}: category={Category} mode={Mode} policy={Policy} options={Options} route={Route} otherAudioPlaying={OtherAudio} sampleRate={SampleRate}Hz ioBufferMs={IoBufferMs:F2}",
            when, _session.Category, _session.Mode, _session.RouteSharingPolicy, _session.CategoryOptions,
            route, _session.OtherAudioPlaying, _session.SampleRate, _session.IOBufferDuration * 1000);
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

        // Back to the launch shape, so the invariant is the same at every
        // moment Flower is not playing rather than only at startup: an
        // inactive non-mixing session is one implicit activation - a device
        // reopened after its output vanished, say - away from silencing
        // whoever took over while Flower was paused.
        ConfigureCategory(SessionShape.Silent);
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
        LogSessionState($"interruption {e.InterruptionType}");

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
        LogSessionState($"route change {e.Reason}");

        if (e.Reason != AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
            return;

        // AVFoundation posts this on its own notification thread; the handler
        // ends up updating ViewModel state, so it has to land on the main
        // thread - which is Avalonia's UI thread on iOS.
        DispatchQueue.MainQueue.DispatchAsync(() => OutputDeviceLost?.Invoke(this, EventArgs.Empty));
    }

    private void OnMediaServicesWereLost(object? sender, NSNotificationEventArgs e)
    {
        _logger.LogWarning("iOS media services were lost; audio output may be interrupted");
    }

    private void OnMediaServicesWereReset(object? sender, NSNotificationEventArgs e)
    {
        _logger.LogWarning("iOS media services were reset; recording the replacement audio-session state");
        LogSessionState("media services reset");
    }

    public void Dispose()
    {
        _routeChangeObserver.Dispose();
        _interruptionObserver.Dispose();
        _mediaServicesLostObserver.Dispose();
        _mediaServicesResetObserver.Dispose();
    }
}
