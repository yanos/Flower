using System;

namespace Flower.Audio
{
    // The producer's view of the native PCM hand-off described in
    // native/miniaudio/flower_audio_bridge.h: a buffer of already-processed
    // S16 frames that a pure-C render callback drains, so that no managed
    // code runs on the real-time audio thread.
    //
    // An interface rather than the P/Invoke wrapper alone because the feeder
    // that drives it is ordinary logic worth testing on a desktop, where the
    // native symbols do not exist - see FakeAudioBridge in the tests.
    //
    // Every member except the fade pair is producer-thread-only (AudioFeeder's
    // own thread). The fades are set from whichever thread issued the
    // transport command and are published atomically.
    internal interface IAudioBridge : IDisposable
    {
        int Capacity { get; }

        // Bytes written but not yet rendered. Read by the producer as a
        // conservative lower bound on how full the buffer is: the consumer
        // only ever drains, so free space computed from this can only grow.
        int Available { get; }

        // Takes as much as fits and reports how much that was. Returns 0
        // while a requested flush has not been acknowledged - anything
        // written before the flush lands would be dropped along with it.
        int Write(ReadOnlySpan<byte> data);

        // Asks the consumer to drop everything queued, returning the id to
        // wait for. The consumer acknowledges on its next callback.
        long RequestFlush();
        long FlushAcked { get; }

        // Applies a pending flush without a consumer. Only valid while the
        // device is stopped, where no callback will ever run to acknowledge
        // one and waiting would hang.
        void FlushNow();

        // While unprimed the callback renders silence rather than the starved
        // trickle available immediately after a flush. Cleared by every flush.
        void SetPrimed(bool primed);

        // The transport envelope, applied by the callback itself: a pause or
        // resume stays click-free and immediate however deep this buffer is,
        // which a fade applied on the producer side could not be.
        void BeginFadeIn(int fadeFrames);
        void BeginFadeOut(int fadeFrames);
        bool FadeOutCompleted { get; }

        // Windowed counters for the render watchdog. Reading resets them.
        AudioBridgeSnapshot TakeSnapshot();
    }

    internal readonly record struct AudioBridgeSnapshot(
        long CallbackCount,
        long RequestedBytes,
        long RealBytes,
        long SilenceBytes,
        long ShortReadCount,
        long UnderrunCount,
        long LastPcmFingerprint,
        long LastReadBytes,
        int MaxIdenticalCallbackRun);
}
