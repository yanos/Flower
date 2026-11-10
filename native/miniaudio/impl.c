/* Compiles miniaudio's implementation into a shared library exposing the
   ma_* C API that Miniaudio-CS's generated P/Invoke bindings call into.
   Pinned to the exact miniaudio commit (0.11.22, see vendor/miniaudio.h)
   that Miniaudio-CS 1.0.4's own bindings were generated against - a
   mismatched miniaudio version here would silently desync struct layouts
   (ma_context/ma_device) from what the C# side expects.

   flower_audio_bridge.c is the library's second translation unit, and
   deliberately not included here: it does not depend on miniaudio at all
   (see that file), which is what lets it be built on its own. */
#include "flower_coreaudio_diagnostics.h"

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
