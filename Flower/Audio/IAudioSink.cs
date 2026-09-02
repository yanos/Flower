using System;
using System.Collections.Generic;

namespace Flower.Audio
{
    // The one platform-forking piece of the gapless pipeline: consumes the
    // shared GaplessRingBuffer that GaplessCoordinator keeps continuously
    // fed across track boundaries, and actually produces sound.
    // MiniaudioSink (this project) is the implementation on every platform,
    // iOS included: AirPlay/Bluetooth routing there is a property of the
    // AVAudioSession, which every output unit in the process follows, so it
    // needs no sink of its own (see docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2).
    // macOS is the one place an AVAudioEngine-backed sink would buy anything,
    // and Flower.MacOS's net10.0-macos TFM now makes one possible - it is
    // simply not needed unless the route picker there turns out not to drive
    // the system output context (same doc).
    public interface IAudioSink : IDisposable
    {
        bool IsPlaying { get; }
        int Volume { get; set; }

        event EventHandler? Playing;
        event EventHandler? Paused;
        event EventHandler? Stopped;

        // The device this sink was rendering to has gone away - headphones
        // unplugged, a Bluetooth speaker switched off, a USB interface pulled.
        // Reported, never acted on: GaplessAudioManager decides what that
        // means (it pauses), so the one policy holds for every sink and for
        // iOS's IPlatformAudioSession, which reports the same fact from the
        // one place that can see it there.
        //
        // Deliberately not raised when the *user* changes output - neither
        // through SetOutputDevice nor by moving the OS default themselves.
        // Those are choices to keep playing somewhere else, and pausing on
        // them would be worse than the bug this exists to fix.
        //
        // Implementations must marshal onto the UI thread: a real sink learns
        // this on a backend thread, and subscribers update ViewModel state
        // straight out of the handler.
        event EventHandler? OutputDeviceLost;

        // Begins consuming ringBuffer. Called once, at construction time of
        // whichever GaplessAudioManager owns this sink - the sink runs for
        // the app's entire lifetime after this, the same way the old
        // the former VlcAudioManager's single MediaPlayer did.
        void Start(GaplessRingBuffer ringBuffer);

        // Swaps in a new EQ processor (rebuilt from settings), or clears it
        // (null = true bypass - the render callback skips processing
        // entirely rather than running an all-zero-dB filter). Safe to call
        // from any thread; implementations must publish this atomically for
        // the real-time callback thread to observe without locking.
        void ApplyEqualizer(Equalizer? equalizer);

        // Swaps in new prebuffer/fade/ramp timings - see AudioTimingSettings.
        // Same contract as ApplyEqualizer: safe from any thread, published
        // atomically, picked up by the render callback on its next pass, so a
        // change takes effect without restarting playback.
        void ApplyTiming(AudioTimingSettings timing);

        void Resume();
        void Pause();
        void Stop();

        // The output devices this sink could render to, re-queried on every
        // call - there is no hotplug subscription, so the picker asks again
        // each time it opens. Empty when the sink has no device concept at
        // all (the browser) or failed to open one.
        IReadOnlyList<AudioOutputDevice> GetOutputDevices();

        // The device SetOutputDevice was last given, or null when the sink is
        // following the OS default. Deliberately *not* "the device currently
        // in use": those coincide until the OS default moves underneath us,
        // and the picker needs to show which of the two states the user chose.
        string? OutputDeviceId { get; }

        // Routes playback to deviceId (an Id from GetOutputDevices), or back
        // to the OS default when null. Implementations reopen the underlying
        // device, so a short gap in sound is expected; playback state and
        // volume must survive the swap. An unknown or vanished deviceId falls
        // back to the OS default rather than throwing.
        void SetOutputDevice(string? deviceId);
    }
}
