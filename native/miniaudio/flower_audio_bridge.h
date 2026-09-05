#ifndef flower_audio_bridge_h
#define flower_audio_bridge_h

#include <stdint.h>

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
 * Everything the callback touches is a plain atomic. No allocation, no
 * locks, no logging.
 */

typedef struct flower_audio_bridge flower_audio_bridge;

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
    uint32_t maxIdenticalCallbackRun;
} flower_audio_bridge_snapshot;

/* capacityBytes is rounded down to a whole frame. Returns NULL on failure.

   bytesPerSample is 2 for S16 or 3 for packed little-endian S24, and it is
   passed rather than inferred because the ring carries interleaved frames
   with no header: bytesPerFrame alone cannot tell 2-channel S24 from
   3-channel S16. The transport envelope is the only thing here that reads
   individual samples rather than bytes, and it has to scale them in their
   own width - see flower_audio_bridge_apply_envelope. Anything else is
   refused, which is a stricter contract than the pipeline needs today and
   the point: a format this cannot fade is a format it must not render. */
flower_audio_bridge* flower_audio_bridge_create(uint32_t capacityBytes, uint32_t bytesPerFrame, uint32_t bytesPerSample);
void flower_audio_bridge_destroy(flower_audio_bridge* pBridge);

/* The ma_device_data_proc to hand to ma_device_config.dataCallback. Cast
   rather than typed, so the header does not have to include miniaudio.h. */
void* flower_audio_bridge_data_callback(void);

/* Binds the bridge to the device the callback will be invoked for. The
   callback finds its bridge by device pointer rather than through
   pUserData, which already carries the managed sink's GCHandle for the
   notification callback. Call after ma_device_init, before ma_device_start. */
int flower_audio_bridge_attach(flower_audio_bridge* pBridge, struct ma_device* pDevice);
void flower_audio_bridge_detach(flower_audio_bridge* pBridge);

uint32_t flower_audio_bridge_capacity(const flower_audio_bridge* pBridge);
uint32_t flower_audio_bridge_available(const flower_audio_bridge* pBridge);

/* Producer side. Writes as much as fits and returns how much was taken.
   Returns 0 while a requested flush is still unacknowledged. */
uint32_t flower_audio_bridge_write(flower_audio_bridge* pBridge, const void* pData, uint32_t byteCount);

/* Producer side. Asks the consumer to drop everything queued; returns the
   request id to wait for. Until flush_acked matches it, write() takes
   nothing - dropping "everything queued" must not be allowed to swallow
   post-flush audio. */
uint64_t flower_audio_bridge_request_flush(flower_audio_bridge* pBridge);
uint64_t flower_audio_bridge_flush_acked(const flower_audio_bridge* pBridge);

/* Applies a pending flush from the producer thread. Only valid while the
   device is stopped, where no consumer exists to acknowledge one. */
void flower_audio_bridge_flush_now(flower_audio_bridge* pBridge);

/* Producer side. While unprimed the callback renders silence rather than
   whatever little has been buffered so far. Cleared by every flush. */
void flower_audio_bridge_set_primed(flower_audio_bridge* pBridge, int primed);
int flower_audio_bridge_is_primed(const flower_audio_bridge* pBridge);

/* The transport envelope, applied by the callback itself so that pause and
   resume stay click-free and immediate however deep the buffer is. Safe
   from any thread. */
void flower_audio_bridge_begin_fade_in(flower_audio_bridge* pBridge, uint32_t fadeFrames);
void flower_audio_bridge_begin_fade_out(flower_audio_bridge* pBridge, uint32_t fadeFrames);
int flower_audio_bridge_fade_out_completed(const flower_audio_bridge* pBridge);

void flower_audio_bridge_take_snapshot(flower_audio_bridge* pBridge, flower_audio_bridge_snapshot* pSnapshot);

#endif
