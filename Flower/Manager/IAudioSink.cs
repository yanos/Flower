using System;

namespace Flower.Manager
{
    // The one platform-forking piece of the gapless pipeline: consumes the
    // shared GaplessRingBuffer that GaplessCoordinator keeps continuously
    // fed across track boundaries, and actually produces sound.
    // MiniaudioSink (this project) is the default, cross-platform
    // implementation; Flower.Apple's AppleAudioEngineSink is the Apple-only
    // alternative that additionally makes real AirPlay/Bluetooth routing
    // possible via AVRoutePickerView.
    public interface IAudioSink : IDisposable
    {
        bool IsPlaying { get; }
        int Volume { get; set; }

        event EventHandler? Playing;
        event EventHandler? Paused;
        event EventHandler? Stopped;

        // Begins consuming ringBuffer. Called once, at construction time of
        // whichever GaplessAudioManager owns this sink - the sink runs for
        // the app's entire lifetime after this, the same way the old
        // VlcAudioManager's single MediaPlayer did.
        void Start(GaplessRingBuffer ringBuffer);

        void Resume();
        void Pause();
        void Stop();
    }
}
