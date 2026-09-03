using System;

namespace Flower.Audio
{
    // The latency/declick trade, exposed rather than hardcoded. Every value is
    // a few milliseconds of audio, and the right answer depends on taste and
    // on the machine: longer fades and a deeper prebuffer are inaudible and
    // glitch-proof, shorter ones make Next feel instant on a fast box. The
    // defaults are quality-first, because glitch-free playback is the point;
    // anyone who wants snappier can edit settings.json without a rebuild.
    //
    // Persisted on AppSettings and delivered live to the sink via
    // IAudioSink.ApplyTiming, the same way EqualizerSettings reaches Equalizer
    // - so a change takes effect without restarting playback.
    //
    // Deliberately no Settings-window UI this round: these are tuning knobs
    // for one person diagnosing their own machine, not a preference anyone
    // should be asked to have an opinion about.
    public sealed class AudioTimingSettings
    {
        // How much PCM must be buffered before a freshly started or freshly
        // seeked stream is allowed to render. Below this the callback emits
        // silence instead of a starved trickle - see MiniaudioSink's prime
        // latch. Costs exactly this much added latency on a start, and nothing
        // at all on a gapless handover (the ring is already full).
        public int PrebufferMs { get; set; } = 200;

        // Fade to silence before pause/stop/a manual skip's flush. Without it
        // the waveform is cut at whatever amplitude it happened to be at,
        // which is a click.
        public int TransportFadeMs { get; set; } = 15;

        // Fade back up after a flush (seek, skip, a fresh start). The first
        // sample of a new stream is rarely zero either.
        public int DeclickFadeMs { get; set; } = 8;

        // How long a gain change takes to travel to its new value - the user's
        // volume slider, a track's VolumeAdjustment, the EQ preamp. A step
        // change applied instantly is zipper noise; anything in this range is
        // inaudible as a ramp.
        public int GainRampMs { get; set; } = 20;

        // How long a transport command waits for the render callback to
        // actually finish the fade above before stopping the device. Bounded
        // because the callback may never run again (device already stopping,
        // no device at all), and a pause must not hang the UI thread.
        public int FadeOutWaitMs { get; set; } = 30;

        // How much already-processed PCM the native bridge holds ahead of the
        // device on the platforms that have one (see NativeAudioBridge). This
        // is the only buffer a GC pause cannot stall, so it is what decides
        // how long a pause has to be before it is audible: iPhone logs put
        // almost every stall under 200ms with a rare outlier near 700ms, and
        // 300 buys all but the outlier. It costs the same in responsiveness -
        // a volume or EQ change is applied here, so it reaches the speaker
        // this many milliseconds later. Zero disables the bridge and puts the
        // render callback back in managed code.
        //
        // The one value here that is not live: it sizes a buffer allocated
        // when the output device is opened, so a change takes effect on the
        // next device open (an output switch, or a restart) rather than
        // immediately like the rest.
        public int NativeBufferMs { get; set; } = 300;

        // Clamped on the way in rather than trusted: this file is meant to be
        // hand-edited, and a typo that puts a two-second fade on every pause
        // or a zero-length prebuffer back where it started should not be able
        // to make the app feel broken with no clue why.
        public AudioTimingSettings Clamped() => new()
        {
            PrebufferMs = Math.Clamp(PrebufferMs, 0, 2000),
            TransportFadeMs = Math.Clamp(TransportFadeMs, 0, 200),
            DeclickFadeMs = Math.Clamp(DeclickFadeMs, 0, 200),
            GainRampMs = Math.Clamp(GainRampMs, 0, 500),
            FadeOutWaitMs = Math.Clamp(FadeOutWaitMs, 0, 500),
            NativeBufferMs = Math.Clamp(NativeBufferMs, 0, 2000),
        };
    }
}
