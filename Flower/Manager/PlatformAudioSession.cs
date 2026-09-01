using System;

using Microsoft.Extensions.Logging;

namespace Flower.Manager;

// iOS needs the AVAudioSession category and activation to be timed around real
// playback. Starting that session at app launch interrupts another app's music
// merely by opening Flower; leaving it to miniaudio's CoreAudio device leaves
// the category at SoloAmbient and cannot keep Flower alive in the background.
// Other platforms have no corresponding work, so they leave Current null.
public interface IPlatformAudioSession
{
    void ActivateForPlayback();
    void DeactivateAfterPlayback();

    // Something else took the audio hardware away mid-track - a phone call,
    // Siri, an alarm, another app that plays with a non-mixing session. The
    // sound has already stopped by the time this arrives; what is left to do is
    // make the app agree that it stopped, so the transport controls and the
    // Lock Screen card do not go on claiming a track is playing in silence.
    //
    // iOS-only for the same reason as OutputDeviceLost is: it is the platform
    // that hands the audio session to one app at a time, so it is the only one
    // with a concept to report.
    event EventHandler? PlaybackInterrupted;

    // The interruption is over. ShouldResume carries the OS's own answer to
    // "may this app start again by itself" - false when the user is expected to
    // press play (they took a call and hung up in a way that ended with another
    // app in charge, say), and honouring it rather than always resuming is what
    // keeps a paused Flower from bursting into song in someone's pocket.
    //
    // Raised even when ShouldResume is false, so a listener that armed itself
    // on PlaybackInterrupted has one place to disarm.
    event EventHandler<PlaybackInterruptionEndedEventArgs>? PlaybackInterruptionEnded;

    // The output the user was listening on has gone away - AirPods out of the
    // ears, a Bluetooth speaker switched off, headphones unplugged. Every
    // platform that has this concept expects the same response, which is the
    // one every music app gives: pause, do not carry on out loud through
    // whatever the OS fell back to. GaplessAudioManager does that pausing; the
    // session only reports the fact.
    //
    // The twin of IAudioSink.OutputDeviceLost, which is where the same fact
    // comes from on every other platform - and the reason this one exists at
    // all is that iOS is the exception: the route belongs to the
    // AVAudioSession, so miniaudio's device never notices it move. Both feed
    // the same handler. If a platform ever reported through both, it would
    // pause twice, which is harmless but means neither should be added
    // speculatively.
    //
    // Raised on the UI thread. Subscribers update ViewModel state straight out
    // of the handler, and the platform's own notification arrives on whatever
    // thread the OS chose, so marshalling is the implementation's job.
    event EventHandler? OutputDeviceLost;
}

public sealed class PlaybackInterruptionEndedEventArgs(bool shouldResume) : EventArgs
{
    public bool ShouldResume { get; } = shouldResume;
}

// Registered by the platform entry point before Avalonia constructs the shared
// composition root, following PlatformAudioManager and PlatformNowPlaying.
public static class PlatformAudioSession
{
    // A factory rather than a ready-made instance, unlike its three sibling
    // seams. The entry point runs before Avalonia, so it has no container to
    // resolve an ILogger<T> from - but nothing needs a session that early
    // either, so the shared composition root calls Materialize below once
    // logging is up and hands the implementation what it needs. The siblings
    // stay instances because they genuinely are needed before then
    // (PlatformRoutePicker is read by a Control, outside DI entirely).
    public static Func<ILoggerFactory, IPlatformAudioSession>? Factory { get; set; }

    public static IPlatformAudioSession? Current { get; set; }

    // Called by App.axaml.cs immediately after UseLoggerFactory. Idempotent,
    // and leaves an explicitly-assigned Current alone so a test can substitute
    // one without racing startup.
    public static void Materialize(ILoggerFactory loggerFactory) =>
        Current ??= Factory?.Invoke(loggerFactory);
}
