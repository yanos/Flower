/* Compiles miniaudio's implementation into a shared library exposing the
   ma_* C API that Miniaudio-CS's generated P/Invoke bindings call into.
   Pinned to the exact miniaudio commit (0.11.22, see vendor/miniaudio.h)
   that Miniaudio-CS 1.0.4's own bindings were generated against - a
   mismatched miniaudio version here would silently desync struct layouts
   (ma_context/ma_device) from what the C# side expects. */
#include "flower_coreaudio_diagnostics.h"
#include "flower_audio_bridge.h"

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#if defined(MA_APPLE_MOBILE)
#include <mach/mach_time.h>
#include <stdatomic.h>

#define FLOWER_COREAUDIO_DIAGNOSTIC_DEVICE_CAPACITY 4
/* A sample-to-sample jump of 75% of full scale, counted per frame by
   flower_coreaudio_diagnostics_pcm_submitted_s16_interleaved below.

   Worth knowing before reading AbruptFrames as a defect count: music crosses
   this on its own. A full-scale sine at frequency f has a maximum adjacent
   delta of 32768 * 2*sin(pi*f/48000), which reaches 24576 at about 5.9kHz - so
   any loud content above roughly 6kHz trips it legitimately, and cymbals,
   hi-hats and sibilance on a modern master do so constantly. A handful of
   abrupt frames per ten seconds out of 480,000 is ordinary percussion, not a
   discontinuity.

   What the counter is actually good for is a step change: a rate that jumps by
   orders of magnitude, or abrupt frames appearing in quiet passages, against
   the same material. Treat it as a comparison, never as an absolute. */
#define FLOWER_COREAUDIO_ABRUPT_SAMPLE_DELTA 24576

typedef struct
{
    _Atomic(ma_device*) pDevice;
    _Atomic uint64_t callbackCount;
    _Atomic uint64_t requestedFrames;
    _Atomic uint64_t submittedFrames;
    _Atomic uint64_t actionFlags;
    _Atomic uint64_t previousCallbackTime;
    _Atomic uint64_t previousHostTime;
    _Atomic uint64_t callbackStartedAt;
    _Atomic uint64_t maxCallbackGapNanoseconds;
    _Atomic uint64_t maxHostTimeGapNanoseconds;
    _Atomic uint64_t maxCallbackDurationNanoseconds;
    _Atomic uint64_t maxSampleDelta;
    _Atomic uint64_t abruptFrameCount;
    _Atomic uint64_t repeatedBufferCount;
    _Atomic uint64_t previousPcmHash;
    _Atomic uint32_t previousLeftSample;
    _Atomic uint32_t previousRightSample;
    _Atomic uint32_t minFrames;
    _Atomic uint32_t maxFrames;
    _Atomic uint32_t maxActionFlags;
    _Atomic uint32_t hasPreviousFrame;
    _Atomic uint32_t consecutiveRepeatedBufferCount;
    _Atomic uint32_t maxRepeatedBufferRun;
} flower_coreaudio_diagnostics_slot;

static flower_coreaudio_diagnostics_slot g_flowerCoreAudioDiagnostics[FLOWER_COREAUDIO_DIAGNOSTIC_DEVICE_CAPACITY];
static mach_timebase_info_data_t g_flowerCoreAudioTimebase;
static _Atomic int g_flowerCoreAudioTimebaseInitialized;

static uint64_t flower_coreaudio_nanoseconds(uint64_t ticks)
{
    if (atomic_load_explicit(&g_flowerCoreAudioTimebaseInitialized, memory_order_acquire) == 0) {
        mach_timebase_info(&g_flowerCoreAudioTimebase);
        atomic_store_explicit(&g_flowerCoreAudioTimebaseInitialized, 1, memory_order_release);
    }

    return (uint64_t)(((__uint128_t)ticks * g_flowerCoreAudioTimebase.numer) / g_flowerCoreAudioTimebase.denom);
}

static flower_coreaudio_diagnostics_slot* flower_coreaudio_slot_for_device(ma_device* pDevice)
{
    uint32_t i;

    if (pDevice == NULL) {
        return NULL;
    }

    for (i = 0; i < FLOWER_COREAUDIO_DIAGNOSTIC_DEVICE_CAPACITY; ++i) {
        if (atomic_load_explicit(&g_flowerCoreAudioDiagnostics[i].pDevice, memory_order_relaxed) == pDevice) {
            return &g_flowerCoreAudioDiagnostics[i];
        }
    }

    return NULL;
}

static void flower_coreaudio_update_maximum_u64(_Atomic uint64_t* pTarget, uint64_t value)
{
    uint64_t current = atomic_load_explicit(pTarget, memory_order_relaxed);

    while (value > current && !atomic_compare_exchange_weak_explicit(
        pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
    }
}

static void flower_coreaudio_update_maximum_u32(_Atomic uint32_t* pTarget, uint32_t value)
{
    uint32_t current = atomic_load_explicit(pTarget, memory_order_relaxed);

    while (value > current && !atomic_compare_exchange_weak_explicit(
        pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
    }
}

static void flower_coreaudio_update_minimum_u32(_Atomic uint32_t* pTarget, uint32_t value)
{
    uint32_t current = atomic_load_explicit(pTarget, memory_order_relaxed);

    while ((current == 0 || value < current) && !atomic_compare_exchange_weak_explicit(
        pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
    }
}

static uint32_t flower_coreaudio_sample_delta(int16_t left, int16_t right)
{
    int32_t delta = (int32_t)left - (int32_t)right;
    return (uint32_t)((delta < 0) ? -delta : delta);
}

static void flower_coreaudio_reset_slot(flower_coreaudio_diagnostics_slot* pSlot)
{
    atomic_store_explicit(&pSlot->callbackCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->requestedFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->submittedFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->actionFlags, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousCallbackTime, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousHostTime, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->callbackStartedAt, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxCallbackGapNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxHostTimeGapNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxCallbackDurationNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxSampleDelta, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->abruptFrameCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->repeatedBufferCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousPcmHash, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousLeftSample, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousRightSample, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->minFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxActionFlags, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->hasPreviousFrame, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->consecutiveRepeatedBufferCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pSlot->maxRepeatedBufferRun, 0, memory_order_relaxed);
}

int flower_coreaudio_diagnostics_register(ma_device* pDevice)
{
    uint32_t i;

    if (pDevice == NULL) {
        return 0;
    }

    /* Initialise the conversion factor before the render thread can enter. */
    flower_coreaudio_nanoseconds(0);

    for (i = 0; i < FLOWER_COREAUDIO_DIAGNOSTIC_DEVICE_CAPACITY; ++i) {
        ma_device* expected = NULL;
        if (atomic_compare_exchange_strong_explicit(
            &g_flowerCoreAudioDiagnostics[i].pDevice, &expected, pDevice,
            memory_order_release, memory_order_relaxed) || expected == pDevice) {
            flower_coreaudio_reset_slot(&g_flowerCoreAudioDiagnostics[i]);
            return 1;
        }
    }

    return 0;
}

void flower_coreaudio_diagnostics_unregister(ma_device* pDevice)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);

    if (pSlot == NULL) {
        return;
    }

    flower_coreaudio_reset_slot(pSlot);
    atomic_store_explicit(&pSlot->pDevice, NULL, memory_order_release);
}

int flower_coreaudio_diagnostics_take_snapshot(
    ma_device* pDevice,
    flower_coreaudio_diagnostics_snapshot* pSnapshot)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);

    if (pSlot == NULL || pSnapshot == NULL) {
        return 0;
    }

    pSnapshot->callbackCount = atomic_exchange_explicit(&pSlot->callbackCount, 0, memory_order_relaxed);
    pSnapshot->requestedFrames = atomic_exchange_explicit(&pSlot->requestedFrames, 0, memory_order_relaxed);
    pSnapshot->submittedFrames = atomic_exchange_explicit(&pSlot->submittedFrames, 0, memory_order_relaxed);
    pSnapshot->actionFlags = atomic_exchange_explicit(&pSlot->actionFlags, 0, memory_order_relaxed);
    pSnapshot->maxCallbackGapNanoseconds = atomic_exchange_explicit(&pSlot->maxCallbackGapNanoseconds, 0, memory_order_relaxed);
    pSnapshot->maxHostTimeGapNanoseconds = atomic_exchange_explicit(&pSlot->maxHostTimeGapNanoseconds, 0, memory_order_relaxed);
    pSnapshot->maxCallbackDurationNanoseconds = atomic_exchange_explicit(&pSlot->maxCallbackDurationNanoseconds, 0, memory_order_relaxed);
    pSnapshot->maxSampleDelta = atomic_exchange_explicit(&pSlot->maxSampleDelta, 0, memory_order_relaxed);
    pSnapshot->abruptFrameCount = atomic_exchange_explicit(&pSlot->abruptFrameCount, 0, memory_order_relaxed);
    pSnapshot->repeatedBufferCount = atomic_exchange_explicit(&pSlot->repeatedBufferCount, 0, memory_order_relaxed);
    pSnapshot->minFrames = atomic_exchange_explicit(&pSlot->minFrames, 0, memory_order_relaxed);
    pSnapshot->maxFrames = atomic_exchange_explicit(&pSlot->maxFrames, 0, memory_order_relaxed);
    pSnapshot->maxActionFlags = atomic_exchange_explicit(&pSlot->maxActionFlags, 0, memory_order_relaxed);
    pSnapshot->maxRepeatedBufferRun = atomic_exchange_explicit(&pSlot->maxRepeatedBufferRun, 0, memory_order_relaxed);
    return 1;
}

void flower_coreaudio_diagnostics_callback_started(
    ma_device* pDevice,
    uint32_t frameCount,
    uint64_t hostTime,
    uint32_t actionFlags)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);
    uint64_t now;
    uint64_t previous;

    if (pSlot == NULL) {
        return;
    }

    now = mach_continuous_time();
    previous = atomic_exchange_explicit(&pSlot->previousCallbackTime, now, memory_order_relaxed);
    if (previous != 0 && now > previous) {
        flower_coreaudio_update_maximum_u64(&pSlot->maxCallbackGapNanoseconds, flower_coreaudio_nanoseconds(now - previous));
    }

    if (hostTime != 0) {
        previous = atomic_exchange_explicit(&pSlot->previousHostTime, hostTime, memory_order_relaxed);
        if (previous != 0 && hostTime > previous) {
            flower_coreaudio_update_maximum_u64(&pSlot->maxHostTimeGapNanoseconds, flower_coreaudio_nanoseconds(hostTime - previous));
        }
    }

    atomic_store_explicit(&pSlot->callbackStartedAt, now, memory_order_relaxed);
    atomic_fetch_add_explicit(&pSlot->callbackCount, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&pSlot->requestedFrames, frameCount, memory_order_relaxed);
    atomic_fetch_or_explicit(&pSlot->actionFlags, actionFlags, memory_order_relaxed);
    flower_coreaudio_update_minimum_u32(&pSlot->minFrames, frameCount);
    flower_coreaudio_update_maximum_u32(&pSlot->maxFrames, frameCount);
    flower_coreaudio_update_maximum_u32(&pSlot->maxActionFlags, actionFlags);
}

void flower_coreaudio_diagnostics_frames_submitted(ma_device* pDevice, uint32_t frameCount)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);

    if (pSlot != NULL) {
        atomic_fetch_add_explicit(&pSlot->submittedFrames, frameCount, memory_order_relaxed);
    }
}

void flower_coreaudio_diagnostics_pcm_submitted_s16_interleaved(
    ma_device* pDevice,
    const int16_t* pFrames,
    uint32_t frameCount,
    uint32_t channelCount)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);
    uint64_t hash = 14695981039346656037ULL;
    uint32_t frameIndex;
    uint32_t maximumDelta = 0;
    uint64_t abruptFrames = 0;
    int16_t previousLeft;
    int16_t previousRight;
    int hasPreviousFrame;

    if (pSlot == NULL || pFrames == NULL || frameCount == 0 || channelCount != 2) {
        return;
    }

    previousLeft = (int16_t)atomic_load_explicit(&pSlot->previousLeftSample, memory_order_relaxed);
    previousRight = (int16_t)atomic_load_explicit(&pSlot->previousRightSample, memory_order_relaxed);
    hasPreviousFrame = atomic_load_explicit(&pSlot->hasPreviousFrame, memory_order_relaxed) != 0;

    for (frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
        int16_t left = pFrames[frameIndex * 2];
        int16_t right = pFrames[frameIndex * 2 + 1];
        uint32_t leftDelta;
        uint32_t rightDelta;
        uint32_t largestDelta;

        hash ^= (uint16_t)left;
        hash *= 1099511628211ULL;
        hash ^= (uint16_t)right;
        hash *= 1099511628211ULL;

        if (hasPreviousFrame) {
            leftDelta = flower_coreaudio_sample_delta(left, previousLeft);
            rightDelta = flower_coreaudio_sample_delta(right, previousRight);
            largestDelta = (leftDelta > rightDelta) ? leftDelta : rightDelta;
            if (largestDelta > maximumDelta) {
                maximumDelta = largestDelta;
            }

            if (largestDelta >= FLOWER_COREAUDIO_ABRUPT_SAMPLE_DELTA) {
                abruptFrames += 1;
            }
        }

        previousLeft = left;
        previousRight = right;
        hasPreviousFrame = 1;
    }

    flower_coreaudio_update_maximum_u64(&pSlot->maxSampleDelta, maximumDelta);
    if (abruptFrames != 0) {
        atomic_fetch_add_explicit(&pSlot->abruptFrameCount, abruptFrames, memory_order_relaxed);
    }

    if (atomic_load_explicit(&pSlot->previousPcmHash, memory_order_relaxed) == hash) {
        uint32_t repeatedCount = atomic_fetch_add_explicit(
            &pSlot->consecutiveRepeatedBufferCount, 1, memory_order_relaxed) + 1;
        atomic_fetch_add_explicit(&pSlot->repeatedBufferCount, 1, memory_order_relaxed);
        flower_coreaudio_update_maximum_u32(&pSlot->maxRepeatedBufferRun, repeatedCount);
    } else {
        atomic_store_explicit(&pSlot->consecutiveRepeatedBufferCount, 0, memory_order_relaxed);
    }

    atomic_store_explicit(&pSlot->previousPcmHash, hash, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousLeftSample, (uint16_t)previousLeft, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousRightSample, (uint16_t)previousRight, memory_order_relaxed);
    atomic_store_explicit(&pSlot->hasPreviousFrame, 1, memory_order_relaxed);
}

void flower_coreaudio_diagnostics_pcm_submitted_f32_interleaved(
    ma_device* pDevice,
    const float* pFrames,
    uint32_t frameCount,
    uint32_t channelCount)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);
    uint64_t hash = 14695981039346656037ULL;
    uint32_t frameIndex;
    uint32_t maximumDelta = 0;
    uint64_t abruptFrames = 0;
    uint32_t previousLeftBits;
    uint32_t previousRightBits;
    float previousLeft;
    float previousRight;
    int hasPreviousFrame;

    if (pSlot == NULL || pFrames == NULL || frameCount == 0 || channelCount != 2) {
        return;
    }

    previousLeftBits = atomic_load_explicit(&pSlot->previousLeftSample, memory_order_relaxed);
    previousRightBits = atomic_load_explicit(&pSlot->previousRightSample, memory_order_relaxed);
    MA_COPY_MEMORY(&previousLeft, &previousLeftBits, sizeof(previousLeft));
    MA_COPY_MEMORY(&previousRight, &previousRightBits, sizeof(previousRight));
    hasPreviousFrame = atomic_load_explicit(&pSlot->hasPreviousFrame, memory_order_relaxed) != 0;

    for (frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
        float left = pFrames[frameIndex * 2];
        float right = pFrames[frameIndex * 2 + 1];
        uint32_t leftBits;
        uint32_t rightBits;

        MA_COPY_MEMORY(&leftBits, &left, sizeof(leftBits));
        MA_COPY_MEMORY(&rightBits, &right, sizeof(rightBits));
        hash ^= leftBits;
        hash *= 1099511628211ULL;
        hash ^= rightBits;
        hash *= 1099511628211ULL;

        if (hasPreviousFrame) {
            float leftDelta = left - previousLeft;
            float rightDelta = right - previousRight;
            float largestDelta;
            uint32_t largestDeltaS16;

            if (leftDelta < 0.0f) {
                leftDelta = -leftDelta;
            }

            if (rightDelta < 0.0f) {
                rightDelta = -rightDelta;
            }

            largestDelta = (leftDelta > rightDelta) ? leftDelta : rightDelta;
            largestDeltaS16 = (largestDelta >= 1.0f) ? 32768U : (uint32_t)(largestDelta * 32768.0f);
            if (largestDeltaS16 > maximumDelta) {
                maximumDelta = largestDeltaS16;
            }

            if (largestDeltaS16 >= FLOWER_COREAUDIO_ABRUPT_SAMPLE_DELTA) {
                abruptFrames += 1;
            }
        }

        previousLeft = left;
        previousRight = right;
        previousLeftBits = leftBits;
        previousRightBits = rightBits;
        hasPreviousFrame = 1;
    }

    flower_coreaudio_update_maximum_u64(&pSlot->maxSampleDelta, maximumDelta);
    if (abruptFrames != 0) {
        atomic_fetch_add_explicit(&pSlot->abruptFrameCount, abruptFrames, memory_order_relaxed);
    }

    if (atomic_load_explicit(&pSlot->previousPcmHash, memory_order_relaxed) == hash) {
        uint32_t repeatedCount = atomic_fetch_add_explicit(
            &pSlot->consecutiveRepeatedBufferCount, 1, memory_order_relaxed) + 1;
        atomic_fetch_add_explicit(&pSlot->repeatedBufferCount, 1, memory_order_relaxed);
        flower_coreaudio_update_maximum_u32(&pSlot->maxRepeatedBufferRun, repeatedCount);
    } else {
        atomic_store_explicit(&pSlot->consecutiveRepeatedBufferCount, 0, memory_order_relaxed);
    }

    atomic_store_explicit(&pSlot->previousPcmHash, hash, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousLeftSample, previousLeftBits, memory_order_relaxed);
    atomic_store_explicit(&pSlot->previousRightSample, previousRightBits, memory_order_relaxed);
    atomic_store_explicit(&pSlot->hasPreviousFrame, 1, memory_order_relaxed);
}

void flower_coreaudio_diagnostics_callback_completed(ma_device* pDevice, uint32_t actionFlags)
{
    flower_coreaudio_diagnostics_slot* pSlot = flower_coreaudio_slot_for_device(pDevice);
    uint64_t startedAt;
    uint64_t now;

    if (pSlot == NULL) {
        return;
    }

    now = mach_continuous_time();
    startedAt = atomic_load_explicit(&pSlot->callbackStartedAt, memory_order_relaxed);
    if (startedAt != 0 && now > startedAt) {
        flower_coreaudio_update_maximum_u64(&pSlot->maxCallbackDurationNanoseconds, flower_coreaudio_nanoseconds(now - startedAt));
    }

    atomic_fetch_or_explicit(&pSlot->actionFlags, actionFlags, memory_order_relaxed);
    flower_coreaudio_update_maximum_u32(&pSlot->maxActionFlags, actionFlags);
}
#else
int flower_coreaudio_diagnostics_register(ma_device* pDevice)
{
    (void)pDevice;
    return 0;
}

void flower_coreaudio_diagnostics_unregister(ma_device* pDevice)
{
    (void)pDevice;
}

int flower_coreaudio_diagnostics_take_snapshot(
    ma_device* pDevice,
    flower_coreaudio_diagnostics_snapshot* pSnapshot)
{
    (void)pDevice;
    (void)pSnapshot;
    return 0;
}

void flower_coreaudio_diagnostics_callback_started(
    ma_device* pDevice,
    uint32_t frameCount,
    uint64_t hostTime,
    uint32_t actionFlags)
{
    (void)pDevice;
    (void)frameCount;
    (void)hostTime;
    (void)actionFlags;
}

void flower_coreaudio_diagnostics_frames_submitted(ma_device* pDevice, uint32_t frameCount)
{
    (void)pDevice;
    (void)frameCount;
}

void flower_coreaudio_diagnostics_pcm_submitted_s16_interleaved(
    ma_device* pDevice,
    const int16_t* pFrames,
    uint32_t frameCount,
    uint32_t channelCount)
{
    (void)pDevice;
    (void)pFrames;
    (void)frameCount;
    (void)channelCount;
}

void flower_coreaudio_diagnostics_pcm_submitted_f32_interleaved(
    ma_device* pDevice,
    const float* pFrames,
    uint32_t frameCount,
    uint32_t channelCount)
{
    (void)pDevice;
    (void)pFrames;
    (void)frameCount;
    (void)channelCount;
}

void flower_coreaudio_diagnostics_callback_completed(ma_device* pDevice, uint32_t actionFlags)
{
    (void)pDevice;
    (void)actionFlags;
}
#endif
/* ------------------------------------------------------------------ */
/* flower_audio_bridge - see flower_audio_bridge.h for what and why.   */
/* ------------------------------------------------------------------ */

#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY 4
#define FLOWER_AUDIO_BRIDGE_FINGERPRINT_STRIDE 64

/* The pipeline is stereo end to end (GaplessFormat.Channels), and the EQ's
   delay lines are laid out for exactly two channels. */
#define FLOWER_AUDIO_BRIDGE_CHANNELS 2

/* The output stage works on a scratch copy of what it reads, so the callback
   needs room for one device period without allocating. Sized well past the
   conservative profile's period at any rate a phone opens at (about 2048
   frames at 48kHz); a callback asking for more is processed in pieces, which
   only differs from one pass in where an EQ crossfade ends. */
#define FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES 4096

/* Equalizer.BandCount. */
#define FLOWER_AUDIO_BRIDGE_EQ_MAX_BANDS 10

/* Set on eqMiddle when the producer has published a slot the callback has
   not picked up yet. Slot indices are 0..2, so bit 2 is free. */
#define FLOWER_AUDIO_BRIDGE_EQ_FRESH 4u
#define FLOWER_AUDIO_BRIDGE_EQ_INDEX 3u

/* The render callback touches nothing but these atomics, so an atomic that
   falls back to a lock - libatomic's, on a target without a native 64-bit
   exclusive pair - would put a lock back on the real-time thread. Every
   shipped ABI is lock-free today (armeabi-v7a inlines LDREXD/STREXD); this
   makes a target where it is not refuse to build rather than ship it. */
_Static_assert(ATOMIC_LLONG_LOCK_FREE == 2, "64-bit atomics must be lock-free on the render thread");
_Static_assert(ATOMIC_INT_LOCK_FREE == 2, "32-bit atomics must be lock-free on the render thread");
_Static_assert(ATOMIC_POINTER_LOCK_FREE == 2, "pointer atomics must be lock-free on the render thread");

/* The output stage below is held to Flower/Audio/OutputStage.cs sample for
   sample, which the C# can only promise if neither side fuses a multiply
   and an add the other does not: RyuJIT never contracts, and clang would
   by default wherever the target has an FMA. */
#pragma STDC FP_CONTRACT OFF

/* One EQ curve as Equalizer.BuildFrom computed it: RBJ peaking biquads,
   already normalised so a0 is 1, in b0 b1 b2 a1 a2 order. present is 0 for
   a true bypass, which is not the same as a curve with no bands - that one
   still applies its preamp. */
typedef struct
{
    uint32_t present;
    uint32_t bandCount;
    float preamp;
    float coefficients[FLOWER_AUDIO_BRIDGE_EQ_MAX_BANDS][5];
} flower_audio_bridge_eq_curve;

/* A curve plus the delay lines running it - two floats per band per channel,
   transposed direct form II. Callback-owned. */
typedef struct
{
    flower_audio_bridge_eq_curve curve;
    float state[FLOWER_AUDIO_BRIDGE_EQ_MAX_BANDS][FLOWER_AUDIO_BRIDGE_CHANNELS][2];
} flower_audio_bridge_eq;

struct flower_audio_bridge
{
    /* The queue itself, in the pipeline's float frames: what the shared ring
       holds, unprocessed, so that everything audible about a control change
       happens on the far side of it. */
    uint8_t* pData;
    uint32_t capacity;
    uint32_t bytesPerFrame;

    _Atomic uint64_t writeIndex;
    _Atomic uint64_t readIndex;
    _Atomic uint64_t flushRequest;
    _Atomic uint64_t flushAcked;
    _Atomic uint32_t primed;

    /* The device's integer format, fixed for the bridge's life because a
       bridge lives exactly as long as the device it was made for. scale is
       2^(bits-1), what a normalised sample is multiplied by to land in the
       destination's units; a power of two, so the multiply is exact. */
    uint32_t deviceBytesPerSample;
    float scale;
    float negativeFullScale;
    float positiveFullScale;

    /* The transport envelope. Targets and step come from any thread as
       float bits; gain itself is touched only by the render callback. */
    _Atomic uint32_t fadeTargetBits;
    _Atomic uint32_t fadeStepBits;
    _Atomic uint32_t fadeOutCompleted;
    float gain;

    /* Output stage controls, from any thread. */
    _Atomic uint32_t targetGainBits;
    _Atomic uint32_t gainRampFrames;
    _Atomic uint32_t declickFadeFrames;

    /* EQ hand-off: a triple buffer of curves. The producer fills eqSlots[eqBack]
       and swaps it into eqMiddle; the callback swaps eqMiddle for its own
       eqFront when it sees the fresh bit. Each side only ever touches the slot
       it holds, so neither can be mid-read of a curve the other is writing,
       and nothing waits. */
    flower_audio_bridge_eq_curve eqSlots[3];
    _Atomic uint32_t eqMiddle;
    uint32_t eqBack;
    uint32_t eqFront;

    /* Callback-owned from here down. */
    flower_audio_bridge_eq eqActive;
    flower_audio_bridge_eq eqOutgoing;
    float* pSamples;
    float* pCrossfade;
    float currentGain;
    int hasRendered;
    int wasPrimed;
    uint32_t fadeInRemaining;
    uint32_t fadeInLength;
    uint32_t ditherState;
    float previousDither;

    _Atomic uint64_t callbackCount;
    _Atomic uint64_t requestedBytes;
    _Atomic uint64_t realBytes;
    _Atomic uint64_t silenceBytes;
    _Atomic uint64_t shortReadCount;
    _Atomic uint64_t underrunCount;
    _Atomic uint64_t lastPcmFingerprint;
    _Atomic uint64_t lastReadBytes;
    _Atomic uint32_t consecutiveIdenticalCallbacks;
    _Atomic uint32_t maxIdenticalCallbackRun;
};

typedef struct
{
    _Atomic(ma_device*) pDevice;
    _Atomic(flower_audio_bridge*) pBridge;
} flower_audio_bridge_slot;

static flower_audio_bridge_slot g_flowerAudioBridges[FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY];

static float flower_audio_bridge_bits_to_float(uint32_t bits)
{
    float value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

static uint32_t flower_audio_bridge_float_to_bits(float value)
{
    uint32_t bits;
    memcpy(&bits, &value, sizeof(bits));
    return bits;
}

flower_audio_bridge* flower_audio_bridge_create(uint32_t capacityFrames, uint32_t channels, uint32_t deviceBytesPerSample)
{
    flower_audio_bridge* pBridge;
    uint32_t bytesPerFrame = FLOWER_AUDIO_BRIDGE_CHANNELS * (uint32_t)sizeof(float);
    uint32_t bits;

    /* Refusing rather than guessing, deliberately: a bridge that rendered a
       width it does not write, or a channel count its EQ is not laid out for,
       would play noise that sounds like a decoder bug. A NULL is a startup
       log line instead. */
    if (capacityFrames == 0 || channels != FLOWER_AUDIO_BRIDGE_CHANNELS) {
        return NULL;
    }

    if (deviceBytesPerSample != 2 && deviceBytesPerSample != 3) {
        return NULL;
    }

    if (capacityFrames > UINT32_MAX / bytesPerFrame) {
        return NULL;
    }

    pBridge = (flower_audio_bridge*)calloc(1, sizeof(*pBridge));
    if (pBridge == NULL) {
        return NULL;
    }

    pBridge->pData = (uint8_t*)calloc(capacityFrames, bytesPerFrame);
    pBridge->pSamples = (float*)calloc(FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES * FLOWER_AUDIO_BRIDGE_CHANNELS, sizeof(float));
    pBridge->pCrossfade = (float*)calloc(FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES * FLOWER_AUDIO_BRIDGE_CHANNELS, sizeof(float));
    if (pBridge->pData == NULL || pBridge->pSamples == NULL || pBridge->pCrossfade == NULL) {
        free(pBridge->pData);
        free(pBridge->pSamples);
        free(pBridge->pCrossfade);
        free(pBridge);
        return NULL;
    }

    pBridge->capacity = capacityFrames * bytesPerFrame;
    pBridge->bytesPerFrame = bytesPerFrame;

    bits = deviceBytesPerSample * 8;
    pBridge->deviceBytesPerSample = deviceBytesPerSample;
    pBridge->scale = (float)(1 << (bits - 1));
    pBridge->negativeFullScale = -(float)(1 << (bits - 1));
    pBridge->positiveFullScale = (float)((1 << (bits - 1)) - 1);

    pBridge->gain = 0.0f;
    atomic_store_explicit(&pBridge->fadeTargetBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    atomic_store_explicit(&pBridge->fadeStepBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);

    atomic_store_explicit(&pBridge->targetGainBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    pBridge->currentGain = 1.0f;

    /* Every slot starts as a bypass, which calloc already made it. */
    pBridge->eqFront = 0;
    atomic_store_explicit(&pBridge->eqMiddle, 1u, memory_order_relaxed);
    pBridge->eqBack = 2;

    /* OutputStage's seed, so the two dither the same. */
    pBridge->ditherState = 0x9E3779B9u;
    pBridge->previousDither = 0.0f;
    return pBridge;
}

void flower_audio_bridge_destroy(flower_audio_bridge* pBridge)
{
    if (pBridge == NULL) {
        return;
    }

    flower_audio_bridge_detach(pBridge);
    free(pBridge->pData);
    free(pBridge->pSamples);
    free(pBridge->pCrossfade);
    free(pBridge);
}

static flower_audio_bridge* flower_audio_bridge_for_device(ma_device* pDevice)
{
    uint32_t i;

    if (pDevice == NULL) {
        return NULL;
    }

    for (i = 0; i < FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY; ++i) {
        if (atomic_load_explicit(&g_flowerAudioBridges[i].pDevice, memory_order_acquire) == pDevice) {
            return atomic_load_explicit(&g_flowerAudioBridges[i].pBridge, memory_order_acquire);
        }
    }

    return NULL;
}

int flower_audio_bridge_attach(flower_audio_bridge* pBridge, ma_device* pDevice)
{
    uint32_t i;

    if (pBridge == NULL || pDevice == NULL) {
        return 0;
    }

    flower_audio_bridge_detach(pBridge);

    for (i = 0; i < FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY; ++i) {
        ma_device* expected = NULL;
        /* The bridge goes in first but only on the slot this claims: writing
           it before the exchange would clobber a slot already taken by
           another device if the exchange then failed. */
        if (atomic_compare_exchange_strong_explicit(
            &g_flowerAudioBridges[i].pDevice, &expected, pDevice,
            memory_order_release, memory_order_relaxed)) {
            atomic_store_explicit(&g_flowerAudioBridges[i].pBridge, pBridge, memory_order_release);
            return 1;
        }
    }

    return 0;
}

void flower_audio_bridge_detach(flower_audio_bridge* pBridge)
{
    uint32_t i;

    if (pBridge == NULL) {
        return;
    }

    for (i = 0; i < FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY; ++i) {
        if (atomic_load_explicit(&g_flowerAudioBridges[i].pBridge, memory_order_acquire) == pBridge) {
            atomic_store_explicit(&g_flowerAudioBridges[i].pDevice, NULL, memory_order_release);
            atomic_store_explicit(&g_flowerAudioBridges[i].pBridge, NULL, memory_order_release);
        }
    }
}

uint32_t flower_audio_bridge_capacity(const flower_audio_bridge* pBridge)
{
    return (pBridge == NULL) ? 0 : pBridge->capacity;
}

uint32_t flower_audio_bridge_available(const flower_audio_bridge* pBridge)
{
    uint64_t writeIndex;
    uint64_t readIndex;

    if (pBridge == NULL) {
        return 0;
    }

    readIndex = atomic_load_explicit(&pBridge->readIndex, memory_order_acquire);
    writeIndex = atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire);
    return (writeIndex > readIndex) ? (uint32_t)(writeIndex - readIndex) : 0;
}

uint32_t flower_audio_bridge_write(flower_audio_bridge* pBridge, const void* pData, uint32_t byteCount)
{
    uint64_t writeIndex;
    uint64_t readIndex;
    uint32_t used;
    uint32_t free_;
    uint32_t toWrite;
    uint32_t offset;
    uint32_t firstChunk;

    if (pBridge == NULL || pData == NULL || byteCount == 0) {
        return 0;
    }

    /* A flush the consumer has not acknowledged yet drops everything up to
       the current write index. Anything written before that lands would be
       dropped with it, so the producer waits instead. */
    if (atomic_load_explicit(&pBridge->flushRequest, memory_order_acquire)
        != atomic_load_explicit(&pBridge->flushAcked, memory_order_acquire)) {
        return 0;
    }

    readIndex = atomic_load_explicit(&pBridge->readIndex, memory_order_acquire);
    writeIndex = atomic_load_explicit(&pBridge->writeIndex, memory_order_relaxed);
    used = (writeIndex > readIndex) ? (uint32_t)(writeIndex - readIndex) : 0;
    free_ = pBridge->capacity - used;
    toWrite = (byteCount < free_) ? byteCount : free_;

    /* Whole frames only, so the callback never reads half of one. */
    toWrite -= toWrite % pBridge->bytesPerFrame;
    if (toWrite == 0) {
        return 0;
    }

    offset = (uint32_t)(writeIndex % pBridge->capacity);
    firstChunk = pBridge->capacity - offset;
    if (firstChunk > toWrite) {
        firstChunk = toWrite;
    }

    memcpy(pBridge->pData + offset, pData, firstChunk);
    if (toWrite > firstChunk) {
        memcpy(pBridge->pData, (const uint8_t*)pData + firstChunk, toWrite - firstChunk);
    }

    atomic_store_explicit(&pBridge->writeIndex, writeIndex + toWrite, memory_order_release);
    return toWrite;
}

/* OutputStage.ArmFadeIn: the next audio rendered comes from somewhere else
   entirely - after a flush, or after a gap the stream itself left - so it is
   faded in over DeclickFadeMs rather than stepped in at full level. */
static void flower_audio_bridge_arm_declick(flower_audio_bridge* pBridge)
{
    uint32_t length = atomic_load_explicit(&pBridge->declickFadeFrames, memory_order_relaxed);
    if (length == 0) {
        length = 1;
    }

    pBridge->fadeInLength = length;
    pBridge->fadeInRemaining = length;
}

uint64_t flower_audio_bridge_request_flush(flower_audio_bridge* pBridge)
{
    if (pBridge == NULL) {
        return 0;
    }

    atomic_store_explicit(&pBridge->primed, 0, memory_order_release);
    return atomic_fetch_add_explicit(&pBridge->flushRequest, 1, memory_order_release) + 1;
}

uint64_t flower_audio_bridge_flush_acked(const flower_audio_bridge* pBridge)
{
    return (pBridge == NULL) ? 0 : atomic_load_explicit(&pBridge->flushAcked, memory_order_acquire);
}

void flower_audio_bridge_flush_now(flower_audio_bridge* pBridge)
{
    uint64_t request;

    if (pBridge == NULL) {
        return;
    }

    /* Callback-owned state, touched here only because there is no callback:
       the contract is that the device is stopped. */
    request = atomic_load_explicit(&pBridge->flushRequest, memory_order_acquire);
    atomic_store_explicit(
        &pBridge->readIndex,
        atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire),
        memory_order_release);
    pBridge->gain = 0.0f;
    flower_audio_bridge_arm_declick(pBridge);
    atomic_store_explicit(&pBridge->flushAcked, request, memory_order_release);
}

void flower_audio_bridge_set_primed(flower_audio_bridge* pBridge, int primed)
{
    if (pBridge != NULL) {
        atomic_store_explicit(&pBridge->primed, primed ? 1u : 0u, memory_order_release);
    }
}

int flower_audio_bridge_is_primed(const flower_audio_bridge* pBridge)
{
    return (pBridge == NULL) ? 0 : (atomic_load_explicit(&pBridge->primed, memory_order_acquire) != 0);
}

static void flower_audio_bridge_begin_fade(flower_audio_bridge* pBridge, uint32_t fadeFrames, float target)
{
    float step;

    if (pBridge == NULL) {
        return;
    }

    step = (fadeFrames == 0) ? 1.0f : (1.0f / (float)fadeFrames);
    atomic_store_explicit(&pBridge->fadeStepBits, flower_audio_bridge_float_to_bits(step), memory_order_relaxed);
    atomic_store_explicit(&pBridge->fadeOutCompleted, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->fadeTargetBits, flower_audio_bridge_float_to_bits(target), memory_order_release);
}

void flower_audio_bridge_begin_fade_in(flower_audio_bridge* pBridge, uint32_t fadeFrames)
{
    flower_audio_bridge_begin_fade(pBridge, fadeFrames, 1.0f);
}

void flower_audio_bridge_begin_fade_out(flower_audio_bridge* pBridge, uint32_t fadeFrames)
{
    flower_audio_bridge_begin_fade(pBridge, fadeFrames, 0.0f);
}

int flower_audio_bridge_fade_out_completed(const flower_audio_bridge* pBridge)
{
    return (pBridge == NULL) ? 1 : (atomic_load_explicit(&pBridge->fadeOutCompleted, memory_order_acquire) != 0);
}

void flower_audio_bridge_set_target_gain(flower_audio_bridge* pBridge, float gain)
{
    if (pBridge == NULL) {
        return;
    }

    /* OutputStage.TargetGain's clamp, written so that a NaN lands on
       silence rather than passing both comparisons. */
    if (!(gain > 0.0f)) {
        gain = 0.0f;
    } else if (gain > 1.0f) {
        gain = 1.0f;
    }

    atomic_store_explicit(&pBridge->targetGainBits, flower_audio_bridge_float_to_bits(gain), memory_order_relaxed);
}

void flower_audio_bridge_set_timing(flower_audio_bridge* pBridge, uint32_t gainRampFrames, uint32_t declickFadeFrames)
{
    if (pBridge == NULL) {
        return;
    }

    atomic_store_explicit(&pBridge->gainRampFrames, gainRampFrames, memory_order_relaxed);
    atomic_store_explicit(&pBridge->declickFadeFrames, declickFadeFrames, memory_order_relaxed);
}

int flower_audio_bridge_set_equalizer(
    flower_audio_bridge* pBridge,
    const float* pCoefficients,
    uint32_t bandCount,
    float preamp)
{
    flower_audio_bridge_eq_curve* pCurve;
    uint32_t previous;

    if (pBridge == NULL || bandCount > FLOWER_AUDIO_BRIDGE_EQ_MAX_BANDS) {
        return 0;
    }

    if (pCoefficients == NULL && bandCount != 0) {
        return 0;
    }

    pCurve = &pBridge->eqSlots[pBridge->eqBack];
    memset(pCurve, 0, sizeof(*pCurve));
    pCurve->present = (pCoefficients != NULL) ? 1u : 0u;
    pCurve->bandCount = bandCount;
    pCurve->preamp = preamp;
    if (bandCount > 0) {
        memcpy(pCurve->coefficients, pCoefficients, (size_t)bandCount * 5 * sizeof(float));
    }

    /* Release publishes the curve with the index. The producer's next slot
       is whatever was in the middle, which the callback is not holding: it
       only ever reads its own front. */
    previous = atomic_exchange_explicit(
        &pBridge->eqMiddle, pBridge->eqBack | FLOWER_AUDIO_BRIDGE_EQ_FRESH, memory_order_acq_rel);
    pBridge->eqBack = previous & FLOWER_AUDIO_BRIDGE_EQ_INDEX;
    return 1;
}

void flower_audio_bridge_take_snapshot(flower_audio_bridge* pBridge, flower_audio_bridge_snapshot* pSnapshot)
{
    if (pBridge == NULL || pSnapshot == NULL) {
        return;
    }

    pSnapshot->callbackCount = atomic_exchange_explicit(&pBridge->callbackCount, 0, memory_order_relaxed);
    pSnapshot->requestedBytes = atomic_exchange_explicit(&pBridge->requestedBytes, 0, memory_order_relaxed);
    pSnapshot->realBytes = atomic_exchange_explicit(&pBridge->realBytes, 0, memory_order_relaxed);
    pSnapshot->silenceBytes = atomic_exchange_explicit(&pBridge->silenceBytes, 0, memory_order_relaxed);
    pSnapshot->shortReadCount = atomic_exchange_explicit(&pBridge->shortReadCount, 0, memory_order_relaxed);
    pSnapshot->underrunCount = atomic_exchange_explicit(&pBridge->underrunCount, 0, memory_order_relaxed);
    pSnapshot->lastPcmFingerprint = atomic_load_explicit(&pBridge->lastPcmFingerprint, memory_order_relaxed);
    pSnapshot->lastReadBytes = atomic_load_explicit(&pBridge->lastReadBytes, memory_order_relaxed);
    pSnapshot->maxIdenticalCallbackRun = atomic_exchange_explicit(&pBridge->maxIdenticalCallbackRun, 0, memory_order_relaxed);
}

/* ------------------------------------------------------------------ */
/* The output stage: Flower/Audio/OutputStage.cs and Equalizer.cs, in   */
/* the same order and the same arithmetic. The C# is the reference -   */
/* it is what the tests hold to the source - and a change to either    */
/* belongs in both.                                                     */
/* ------------------------------------------------------------------ */

/* Equalizer.ProcessInPlace. */
static void flower_audio_bridge_eq_process(flower_audio_bridge_eq* pEq, float* pSamples, uint32_t frameCount)
{
    const flower_audio_bridge_eq_curve* pCurve = &pEq->curve;
    uint32_t frame;
    uint32_t band;

    if (!pCurve->present) {
        return;
    }

    for (frame = 0; frame < frameCount; ++frame) {
        float l = pSamples[frame * 2] * pCurve->preamp;
        float r = pSamples[frame * 2 + 1] * pCurve->preamp;

        for (band = 0; band < pCurve->bandCount; ++band) {
            const float* c = pCurve->coefficients[band];
            float* zl = pEq->state[band][0];
            float* zr = pEq->state[band][1];
            float y;

            /* BiquadCoefficients.Process: transposed direct form II. */
            y = c[0] * l + zl[0];
            zl[0] = c[1] * l - c[3] * y + zl[1];
            zl[1] = c[2] * l - c[4] * y;
            l = y;

            y = c[0] * r + zr[0];
            zr[0] = c[1] * r - c[3] * y + zr[1];
            zr[1] = c[2] * r - c[4] * y;
            r = y;
        }

        pSamples[frame * 2] = l;
        pSamples[frame * 2 + 1] = r;
    }
}

/* OutputStage.ApplyEqualizer: a new curve is crossfaded in over this buffer
   from the old one's output, rather than dropped onto the running signal
   with zeroed delay lines, which clicks. */
static void flower_audio_bridge_apply_equalizer(flower_audio_bridge* pBridge, float* pSamples, uint32_t frameCount)
{
    uint32_t sampleCount = frameCount * FLOWER_AUDIO_BRIDGE_CHANNELS;
    uint32_t previous;
    uint32_t i;

    if ((atomic_load_explicit(&pBridge->eqMiddle, memory_order_relaxed) & FLOWER_AUDIO_BRIDGE_EQ_FRESH) == 0) {
        flower_audio_bridge_eq_process(&pBridge->eqActive, pSamples, frameCount);
        return;
    }

    /* Acquire pairs with the producer's release: the curve in the slot this
       hands back is complete. */
    previous = atomic_exchange_explicit(&pBridge->eqMiddle, pBridge->eqFront, memory_order_acq_rel);
    pBridge->eqFront = previous & FLOWER_AUDIO_BRIDGE_EQ_INDEX;

    /* The outgoing filter keeps the delay lines it was running with; the
       incoming one starts from zero, as a freshly built Equalizer does. */
    pBridge->eqOutgoing = pBridge->eqActive;
    pBridge->eqActive.curve = pBridge->eqSlots[pBridge->eqFront];
    memset(pBridge->eqActive.state, 0, sizeof(pBridge->eqActive.state));

    memcpy(pBridge->pCrossfade, pSamples, (size_t)sampleCount * sizeof(float));
    flower_audio_bridge_eq_process(&pBridge->eqOutgoing, pSamples, frameCount);
    flower_audio_bridge_eq_process(&pBridge->eqActive, pBridge->pCrossfade, frameCount);

    for (i = 0; i < sampleCount; ++i) {
        float t = (float)i / (float)sampleCount;
        pSamples[i] += (pBridge->pCrossfade[i] - pSamples[i]) * t;
    }
}

/* OutputStage.ApplyGainAndEnvelope, plus the transport envelope that stage
   has its own version of. The volume ramp and the declick fade are the
   stage's; the transport fade is the bridge's, and was applied after the
   requantiser when the stage ran on the feeder. Multiplied in last, so that
   with no transport fade running the product is the stage's to the bit. */
static void flower_audio_bridge_apply_gain(flower_audio_bridge* pBridge, float* pSamples, uint32_t frameCount)
{
    float target = flower_audio_bridge_bits_to_float(
        atomic_load_explicit(&pBridge->targetGainBits, memory_order_relaxed));
    uint32_t rampFrames = atomic_load_explicit(&pBridge->gainRampFrames, memory_order_relaxed);
    float transportTarget = flower_audio_bridge_bits_to_float(
        atomic_load_explicit(&pBridge->fadeTargetBits, memory_order_acquire));
    float transportStep = flower_audio_bridge_bits_to_float(
        atomic_load_explicit(&pBridge->fadeStepBits, memory_order_relaxed));
    float transport = pBridge->gain;
    float current = pBridge->currentGain;
    float step;
    uint32_t frame;

    if (rampFrames == 0) {
        current = target;
    }

    step = (rampFrames == 0) ? 0.0f : (target - current) / (float)rampFrames;

    for (frame = 0; frame < frameCount; ++frame) {
        float envelope = 1.0f;
        float gain;

        if (current != target) {
            current += step;
            if ((step > 0.0f && current > target) || (step < 0.0f && current < target)) {
                current = target;
            }
        }

        if (pBridge->fadeInRemaining > 0) {
            envelope = (float)(pBridge->fadeInLength - pBridge->fadeInRemaining) / (float)pBridge->fadeInLength;
            pBridge->fadeInRemaining--;
        }

        if (transport < transportTarget) {
            transport += transportStep;
            if (transport > transportTarget) {
                transport = transportTarget;
            }
        } else if (transport > transportTarget) {
            transport -= transportStep;
            if (transport < transportTarget) {
                transport = transportTarget;
            }
        }

        gain = current * envelope;
        gain *= transport;
        pSamples[frame * 2] *= gain;
        pSamples[frame * 2 + 1] *= gain;
    }

    pBridge->currentGain = current;
    pBridge->gain = transport;
    if (transportTarget == 0.0f && transport == 0.0f) {
        atomic_store_explicit(&pBridge->fadeOutCompleted, 1, memory_order_release);
    }
}

/* OutputStage.NextDither: xorshift32, scaled to +-0.5 LSB. */
static float flower_audio_bridge_next_dither(flower_audio_bridge* pBridge)
{
    uint32_t x = pBridge->ditherState;
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    pBridge->ditherState = x;
    return ((float)x / (float)UINT32_MAX) - 0.5f;
}

/* OutputStage.Requantise - read its remarks for why a sample that is
   already an exact integer is left undithered, and why the clamp is hard.
   rintf rounds half to even under the default rounding mode, as MathF.Round
   does. */
static void flower_audio_bridge_requantise(flower_audio_bridge* pBridge, const float* pSamples, uint8_t* pDestination, uint32_t sampleCount)
{
    uint32_t i;

    for (i = 0; i < sampleCount; ++i) {
        float value = pSamples[i] * pBridge->scale;
        float rounded;
        int32_t quantised;

        if (value != rintf(value)) {
            float dither = flower_audio_bridge_next_dither(pBridge);
            value += dither - pBridge->previousDither;
            pBridge->previousDither = dither;
        }

        rounded = rintf(value);
        if (rounded < pBridge->negativeFullScale) {
            rounded = pBridge->negativeFullScale;
        } else if (rounded > pBridge->positiveFullScale) {
            rounded = pBridge->positiveFullScale;
        }

        /* A NaN passed both comparisons above; converting one to an integer
           is undefined, where .NET gives zero. */
        quantised = (rounded == rounded) ? (int32_t)rounded : 0;

        if (pBridge->deviceBytesPerSample == 2) {
            int16_t narrow = (int16_t)quantised;
            memcpy(pDestination + (size_t)i * 2, &narrow, sizeof(narrow));
        } else {
            uint8_t* pSample = pDestination + (size_t)i * 3;
            pSample[0] = (uint8_t)(quantised & 0xFF);
            pSample[1] = (uint8_t)((quantised >> 8) & 0xFF);
            pSample[2] = (uint8_t)((quantised >> 16) & 0xFF);
        }
    }
}

/* Reports whether any of the sampled bytes was non-zero through pAudible, so
   the caller can tell "the same buffer again" from "silence again". They are
   the same fingerprint, and only the first of them is worth a warning: a run
   of silent callbacks is a pause, a fade-out or a starved ring, all of which
   have their own counters, while a run of identical *audible* buffers is the
   repeated-buffer static this exists to catch. Folded across the pieces of
   one callback through pHash. */
static void flower_audio_bridge_fingerprint(const uint8_t* pData, uint32_t byteCount, uint64_t* pHash, int* pAudible)
{
    uint64_t hash = *pHash;
    uint8_t seen = 0;
    uint32_t i;

    for (i = 0; i < byteCount; i += FLOWER_AUDIO_BRIDGE_FINGERPRINT_STRIDE) {
        hash ^= pData[i];
        hash *= 1099511628211ULL;
        seen |= pData[i];
    }

    *pHash = hash;
    if (seen != 0) {
        *pAudible = 1;
    }
}

/* Reads up to frameCount frames into pSamples, silence-padding the rest, and
   returns the bytes it read. */
static uint32_t flower_audio_bridge_read(flower_audio_bridge* pBridge, float* pSamples, uint32_t frameCount)
{
    uint32_t byteCount = frameCount * pBridge->bytesPerFrame;
    uint8_t* pDestination = (uint8_t*)pSamples;
    uint64_t readIndex = atomic_load_explicit(&pBridge->readIndex, memory_order_relaxed);
    uint64_t writeIndex = atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire);
    uint32_t available = (writeIndex > readIndex) ? (uint32_t)(writeIndex - readIndex) : 0;
    uint32_t toRead = (byteCount < available) ? byteCount : available;
    uint32_t offset;
    uint32_t firstChunk;

    if (toRead > 0) {
        offset = (uint32_t)(readIndex % pBridge->capacity);
        firstChunk = pBridge->capacity - offset;
        if (firstChunk > toRead) {
            firstChunk = toRead;
        }

        memcpy(pDestination, pBridge->pData + offset, firstChunk);
        if (toRead > firstChunk) {
            memcpy(pDestination + firstChunk, pBridge->pData, toRead - firstChunk);
        }

        atomic_store_explicit(&pBridge->readIndex, readIndex + toRead, memory_order_release);
    }

    if (toRead < byteCount) {
        memset(pDestination + toRead, 0, byteCount - toRead);
    }

    return toRead;
}

static void flower_audio_bridge_on_data(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount)
{
    flower_audio_bridge* pBridge = flower_audio_bridge_for_device(pDevice);
    uint8_t* pDestination = (uint8_t*)pOutput;
    uint32_t deviceBytesPerFrame;
    uint32_t sourceByteCount;
    uint32_t totalRead = 0;
    uint32_t done = 0;
    uint64_t flushRequest;
    uint64_t fingerprint = 14695981039346656037ULL;
    int audible = 0;
    int primed;

    (void)pInput;

    if (pBridge == NULL || pOutput == NULL) {
        return;
    }

    deviceBytesPerFrame = pBridge->deviceBytesPerSample * FLOWER_AUDIO_BRIDGE_CHANNELS;
    sourceByteCount = frameCount * pBridge->bytesPerFrame;

    flushRequest = atomic_load_explicit(&pBridge->flushRequest, memory_order_acquire);
    if (flushRequest != atomic_load_explicit(&pBridge->flushAcked, memory_order_relaxed)) {
        atomic_store_explicit(
            &pBridge->readIndex,
            atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire),
            memory_order_release);
        /* Silence on both sides of a flush, so the far side fades in from
           zero rather than stepping in at whatever level it left off. The
           target is left alone: a flush during a pause must stay quiet. The
           declick is OutputStage's generation change, here. */
        pBridge->gain = 0.0f;
        flower_audio_bridge_arm_declick(pBridge);
        atomic_store_explicit(&pBridge->flushAcked, flushRequest, memory_order_release);
    }

    /* Nothing is rendered or processed while unprimed - not the envelope,
       not the EQ, not the ramp - exactly as the managed callback skips
       OutputStage.Process. Running any of them over the silence would spend
       the declick on nothing, and the first real audio would then arrive at
       full gain, which is the click it exists to remove.

       A gap the feeder un-primed for is a rebuffer, which no flush marks, so
       the declick is armed on the way back in as well: FadeInNextAudio. */
    primed = atomic_load_explicit(&pBridge->primed, memory_order_acquire) != 0;
    if (primed && !pBridge->wasPrimed) {
        flower_audio_bridge_arm_declick(pBridge);
    }

    pBridge->wasPrimed = primed;

    if (primed && !pBridge->hasRendered) {
        /* A bridge is made for each device open, unlike the C# stage, which
           outlives its devices; starting the ramp from unity would sweep the
           volume down from full at the start of every one. */
        pBridge->currentGain = flower_audio_bridge_bits_to_float(
            atomic_load_explicit(&pBridge->targetGainBits, memory_order_relaxed));
        pBridge->hasRendered = 1;
    }

    if (!primed) {
        memset(pDestination, 0, (size_t)frameCount * deviceBytesPerFrame);
    } else {
        while (done < frameCount) {
            uint32_t frames = frameCount - done;
            uint32_t read;

            if (frames > FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES) {
                frames = FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES;
            }

            read = flower_audio_bridge_read(pBridge, pBridge->pSamples, frames);
            if (read > 0) {
                flower_audio_bridge_fingerprint((const uint8_t*)pBridge->pSamples, read, &fingerprint, &audible);
            }

            /* The whole piece, silence padding included: the EQ's delay
               lines and the gain ramp have to keep advancing across a gap,
               or they resume from pre-gap state on the other side of it. */
            flower_audio_bridge_apply_equalizer(pBridge, pBridge->pSamples, frames);
            flower_audio_bridge_apply_gain(pBridge, pBridge->pSamples, frames);
            flower_audio_bridge_requantise(
                pBridge, pBridge->pSamples, pDestination + (size_t)done * deviceBytesPerFrame,
                frames * FLOWER_AUDIO_BRIDGE_CHANNELS);

            totalRead += read;
            done += frames;
        }
    }

    /* In the bridge's bytes - the pipeline's - like the managed callback's
       counters, which the watchdog sets against the ring's own. */
    atomic_fetch_add_explicit(&pBridge->callbackCount, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&pBridge->requestedBytes, sourceByteCount, memory_order_relaxed);
    atomic_fetch_add_explicit(&pBridge->realBytes, totalRead, memory_order_relaxed);
    if (totalRead < sourceByteCount) {
        atomic_fetch_add_explicit(&pBridge->silenceBytes, sourceByteCount - totalRead, memory_order_relaxed);
        if (primed) {
            atomic_fetch_add_explicit(&pBridge->shortReadCount, 1, memory_order_relaxed);
            if (totalRead == 0) {
                atomic_fetch_add_explicit(&pBridge->underrunCount, 1, memory_order_relaxed);
            }
        }
    }

    if (totalRead > 0) {
        uint64_t previousFingerprint;
        uint64_t previousRead;

        previousFingerprint = atomic_exchange_explicit(&pBridge->lastPcmFingerprint, fingerprint, memory_order_relaxed);
        previousRead = atomic_exchange_explicit(&pBridge->lastReadBytes, totalRead, memory_order_relaxed);
        if (audible && previousFingerprint == fingerprint && previousRead == totalRead) {
            uint32_t repeated = atomic_fetch_add_explicit(&pBridge->consecutiveIdenticalCallbacks, 1, memory_order_relaxed) + 1;
            uint32_t current = atomic_load_explicit(&pBridge->maxIdenticalCallbackRun, memory_order_relaxed);
            while (repeated > current && !atomic_compare_exchange_weak_explicit(
                &pBridge->maxIdenticalCallbackRun, &current, repeated, memory_order_relaxed, memory_order_relaxed)) {
            }
        } else {
            atomic_store_explicit(&pBridge->consecutiveIdenticalCallbacks, 0, memory_order_relaxed);
        }
    }
}

void* flower_audio_bridge_data_callback(void)
{
    return (void*)flower_audio_bridge_on_data;
}
