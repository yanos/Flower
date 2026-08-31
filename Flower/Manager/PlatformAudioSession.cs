using System;

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

// Registered by the platform entry point before Avalonia constructs the shared
// composition root, following PlatformAudioManager and PlatformNowPlaying.
public static class PlatformAudioSession
{
    public static IPlatformAudioSession? Current { get; set; }
}
