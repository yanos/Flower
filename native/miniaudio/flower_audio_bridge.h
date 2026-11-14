#ifndef flower_audio_bridge_h
#define flower_audio_bridge_h

#include <stdint.h>

/* The library's whole API, and nothing else: every build compiles with
   -fvisibility=hidden, so what this does not mark stays internal. A Windows
   DLL exports nothing unmarked anyway. */
#if defined(_WIN32)
#define FLOWER_AUDIO_BRIDGE_API __declspec(dllexport)
#else
#define FLOWER_AUDIO_BRIDGE_API __attribute__((visibility("default")))
#endif

struct ma_device;

/*
 * A native PCM hand-off between managed code and miniaudio's render
 * callback, so that no managed code runs on the real-time audio thread.
 *
 * Why this exists: on iOS and Android the runtime is Mono, whose GC
 * suspends every managed thread at a safepoint - the CoreAudio render
 * thread included, because our data callback was itself managed. Device
 * logs from an iPhone show render callbacks arriving up to 668ms late with
 * the PCM ring full, the app's own watchdog timer late in the same window,
 * and CoreAudio's host timestamps skipping 442ms: a whole-process stop,
 * heard as a blip. A thread that never enters managed code is never
 * suspended, so the fix is to make the callback pure C reading from a
 * buffer that managed code fills ahead of time.
 *
 * Threading: single-producer/single-consumer. The producer is one managed
 * feeder thread (AudioFeeder), the consumer is the render callback. Read
 * and write indices are monotonic and never rebased, so neither side has
 * to reason about an epoch; a flush is a request the consumer acknowledges
 * by dropping everything queued, and the producer waits for that
 * acknowledgement before writing anything new.
 *
 * Everything the callback touches is a plain atomic or its own state, plus
 * the monotonic clock it times itself by. No allocation, no locks, no
 * logging. The output stage it runs - EQ, volume ramp, declick, dither - is
 * fixed arithmetic over pre-allocated buffers,
 * and the iPhone logs that motivated all this showed the same work never
 * past ~3ms against a 42.7ms period: what stalled the thread was never the
 * workload, only a managed runtime suspending it.
 */

typedef struct flower_audio_bridge flower_audio_bridge;

/* What the render watchdog reads once a second. Everything but the two
   "last" fields and the frames-at-max-gap pair is a count or a peak over the
   window since the previous snapshot, and taking one starts the next window.

   The byte counts are the pipeline's float bytes, as the shared ring counts
   them. The timing half is in nanoseconds and device frames: whether the
   audio thread itself was called late or spent too long rendering a period,
   which a ring that never ran short cannot say. A gap is the time from one
   callback's start to the next's, set against the period the earlier one
   asked for; an interesting gap is one of 60ms or more, past the 42ms period
   Bluetooth routes run at but short enough to catch an audible stall; a
   render overrun is a callback that took longer than the audio it rendered
   lasts. timedCallbackCount is callbackCount's twin for this half, since
   reset_timing restarts only this half. */
typedef struct
{
    uint64_t callbackCount;
    uint64_t requestedBytes;
    uint64_t realBytes;
    uint64_t silenceBytes;
    uint64_t shortReadCount;
    uint64_t underrunCount;
    uint64_t lastPcmFingerprint;
    uint64_t lastReadBytes;
    uint64_t timedCallbackCount;
    uint64_t totalFrames;
    uint64_t interestingGapCount;
    uint64_t renderOverrunCount;
    uint64_t maxGapNanoseconds;
    uint64_t expectedNanosecondsAtMaxGap;
    uint64_t maxLateNanoseconds;
    uint64_t maxRenderNanoseconds;
    uint32_t maxIdenticalCallbackRun;
    uint32_t minFrames;
    uint32_t maxFrames;
    uint32_t precedingFramesAtMaxGap;
    uint32_t currentFramesAtMaxGap;
} flower_audio_bridge_snapshot;

/* capacityFrames is in the pipeline's frames - interleaved float32, which is
   what the bridge carries: the shared ring's PCM, unprocessed, so that the
   volume, the EQ and the requantisation all happen in the callback, after
   the queue rather than before it (AUDIO-QUALITY-PLAN.md, finding M).

   channels must be 2 and deviceBytesPerSample 2 (S16) or 3 (packed
   little-endian S24): the format the device was opened in, which the
   output stage requantises to. A bridge lives exactly as long as one
   device, so this is fixed for its life. Anything else is refused rather
   than rendered as something it is not.

   sampleRate is the device's, which the callback's timing diagnostics
   measure a period against. Capacities larger than INT32_MAX bytes are
   refused because managed callers expose signed byte counts. Returns NULL
   on failure. */
FLOWER_AUDIO_BRIDGE_API flower_audio_bridge* flower_audio_bridge_create(uint32_t capacityFrames, uint32_t channels, uint32_t deviceBytesPerSample, uint32_t sampleRate);
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_destroy(flower_audio_bridge* pBridge);

/* The ma_device_data_proc to hand to ma_device_config.dataCallback. Cast
   rather than typed, so the header does not have to include miniaudio.h. */
FLOWER_AUDIO_BRIDGE_API void* flower_audio_bridge_data_callback(void);

/* Binds the bridge to the device the callback will be invoked for. The
   callback finds its bridge by device pointer rather than through
   pUserData, which already carries the managed sink's GCHandle for the
   notification callback. Call after ma_device_init, before ma_device_start.
   Returns 0 when every slot is taken, and the callback then renders
   nothing. */
FLOWER_AUDIO_BRIDGE_API int flower_audio_bridge_attach(flower_audio_bridge* pBridge, struct ma_device* pDevice);
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_detach(flower_audio_bridge* pBridge);

FLOWER_AUDIO_BRIDGE_API uint32_t flower_audio_bridge_capacity(const flower_audio_bridge* pBridge);
FLOWER_AUDIO_BRIDGE_API uint32_t flower_audio_bridge_available(const flower_audio_bridge* pBridge);

/* Producer side. Writes as much as fits and returns how much was taken.
   Returns 0 while a requested flush is still unacknowledged. */
FLOWER_AUDIO_BRIDGE_API uint32_t flower_audio_bridge_write(flower_audio_bridge* pBridge, const void* pData, uint32_t byteCount);

/* Producer side. Asks the consumer to drop everything queued; returns the
   request id to wait for. Until flush_acked matches it, write() takes
   nothing - dropping "everything queued" must not be allowed to swallow
   post-flush audio. */
FLOWER_AUDIO_BRIDGE_API uint64_t flower_audio_bridge_request_flush(flower_audio_bridge* pBridge);
FLOWER_AUDIO_BRIDGE_API uint64_t flower_audio_bridge_flush_acked(const flower_audio_bridge* pBridge);

/* Applies a pending flush from the producer thread. Only valid while the
   device is stopped, where no consumer exists to acknowledge one. */
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_flush_now(flower_audio_bridge* pBridge);

/* Producer side. While unprimed the callback renders silence rather than
   whatever little has been buffered so far. Cleared by every flush. */
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_set_primed(flower_audio_bridge* pBridge, int primed);
FLOWER_AUDIO_BRIDGE_API int flower_audio_bridge_is_primed(const flower_audio_bridge* pBridge);

/* The transport envelope, applied by the callback itself so that pause and
   resume stay click-free and immediate however deep the buffer is. Safe
   from any thread. */
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_begin_fade_in(flower_audio_bridge* pBridge, uint32_t fadeFrames);
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_begin_fade_out(flower_audio_bridge* pBridge, uint32_t fadeFrames);
FLOWER_AUDIO_BRIDGE_API int flower_audio_bridge_fade_out_completed(const flower_audio_bridge* pBridge);

/* The output stage's controls. Each applies from the next device buffer,
   however deep the queue is.

   set_target_gain and set_timing are safe from any thread. The gain is
   linear amplitude, clamped to [0,1]; the ramp and declick lengths are in
   frames at the device's rate, and 0 means none (the declick then lasts one
   frame).

   set_equalizer takes Equalizer.BuildFrom's curve: bandCount biquads of five
   coefficients each (b0 b1 b2 a1 a2, normalised so a0 is 1), plus the
   preamp as linear gain. NULL coefficients with a bandCount of 0 is a true
   bypass, which is not the same as no bands: that still applies the preamp. The callback crossfades to a new curve over one buffer. Callers
   must not overlap: it is single-producer, so take a lock around it if more
   than one thread can call it. Returns 0 when refused. */
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_set_target_gain(flower_audio_bridge* pBridge, float gain);
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_set_timing(flower_audio_bridge* pBridge, uint32_t gainRampFrames, uint32_t declickFadeFrames);
FLOWER_AUDIO_BRIDGE_API int flower_audio_bridge_set_equalizer(flower_audio_bridge* pBridge, const float* pCoefficients, uint32_t bandCount, float preamp);

FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_take_snapshot(flower_audio_bridge* pBridge, flower_audio_bridge_snapshot* pSnapshot);

/* Forgets the last callback's start, so the next callback is not measured
   against it: the gap across a pause or a stop is the device not running,
   not the audio thread being late. Also starts a fresh window. Safe from any
   thread; the callback picks the forgetting up on its next run. */
FLOWER_AUDIO_BRIDGE_API void flower_audio_bridge_reset_timing(flower_audio_bridge* pBridge);

#endif
