namespace Flower.Audio
{
    // Set by the platform entry point (e.g. a future Flower.Mac's Program.cs)
    // before Avalonia starts - same timing/convention as
    // Flower.Services.PlatformNowPlaying.Current/PlatformMdns.Current. Left
    // null on Windows/Linux/Android/iOS/CLI, where App.axaml.cs falls back to
    // the default MiniaudioSink.
    public static class PlatformAudioManager
    {
        public static IAudioSink? Current { get; set; }
    }
}
