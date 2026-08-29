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
}

// Registered by the platform entry point before Avalonia constructs the shared
// composition root, following PlatformAudioManager and PlatformNowPlaying.
public static class PlatformAudioSession
{
    public static IPlatformAudioSession? Current { get; set; }
}
