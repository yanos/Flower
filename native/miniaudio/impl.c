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

#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY 4
#define FLOWER_AUDIO_BRIDGE_FINGERPRINT_STRIDE 64

struct flower_audio_bridge
{
    uint8_t* pData;
    uint32_t capacity;
    uint32_t bytesPerFrame;

    _Atomic uint64_t writeIndex;
    _Atomic uint64_t readIndex;
    _Atomic uint64_t flushRequest;
    _Atomic uint64_t flushAcked;
    _Atomic uint32_t primed;

    /* The transport envelope. Targets and step come from any thread as
       float bits; gain itself is touched only by the render callback. */
    _Atomic uint32_t fadeTargetBits;
    _Atomic uint32_t fadeStepBits;
    _Atomic uint32_t fadeOutCompleted;
    float gain;

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

flower_audio_bridge* flower_audio_bridge_create(uint32_t capacityBytes, uint32_t bytesPerFrame)
{
    flower_audio_bridge* pBridge;
    uint32_t capacity;

    if (bytesPerFrame == 0 || capacityBytes < bytesPerFrame) {
        return NULL;
    }

    capacity = (capacityBytes / bytesPerFrame) * bytesPerFrame;

    pBridge = (flower_audio_bridge*)calloc(1, sizeof(*pBridge));
    if (pBridge == NULL) {
        return NULL;
    }

    pBridge->pData = (uint8_t*)calloc(1, capacity);
    if (pBridge->pData == NULL) {
        free(pBridge);
        return NULL;
    }

    pBridge->capacity = capacity;
    pBridge->bytesPerFrame = bytesPerFrame;
    pBridge->gain = 0.0f;
    atomic_store_explicit(&pBridge->fadeTargetBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    atomic_store_explicit(&pBridge->fadeStepBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    return pBridge;
}

void flower_audio_bridge_destroy(flower_audio_bridge* pBridge)
{
    if (pBridge == NULL) {
        return;
    }

    flower_audio_bridge_detach(pBridge);
    free(pBridge->pData);
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

    request = atomic_load_explicit(&pBridge->flushRequest, memory_order_acquire);
    atomic_store_explicit(
        &pBridge->readIndex,
        atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire),
        memory_order_release);
    pBridge->gain = 0.0f;
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

/* Applies the transport envelope in place over interleaved 16-bit frames,
   silence padding included: a fade-out that lands in a starved callback
   still has to reach zero and stay there. */
static void flower_audio_bridge_apply_envelope(flower_audio_bridge* pBridge, int16_t* pSamples, uint32_t frameCount, uint32_t channels)
{
    float target = flower_audio_bridge_bits_to_float(
        atomic_load_explicit(&pBridge->fadeTargetBits, memory_order_acquire));
    float step = flower_audio_bridge_bits_to_float(
        atomic_load_explicit(&pBridge->fadeStepBits, memory_order_relaxed));
    float gain = pBridge->gain;
    uint32_t frameIndex;
    uint32_t channelIndex;

    if (gain == target && target == 1.0f) {
        return;
    }

    for (frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
        if (gain < target) {
            gain += step;
            if (gain > target) {
                gain = target;
            }
        } else if (gain > target) {
            gain -= step;
            if (gain < target) {
                gain = target;
            }
        }

        for (channelIndex = 0; channelIndex < channels; ++channelIndex) {
            pSamples[frameIndex * channels + channelIndex] =
                (int16_t)((float)pSamples[frameIndex * channels + channelIndex] * gain);
        }
    }

    pBridge->gain = gain;
    if (target == 0.0f && gain == 0.0f) {
        atomic_store_explicit(&pBridge->fadeOutCompleted, 1, memory_order_release);
    }
}

/* Reports whether any of the sampled bytes was non-zero through pAudible, so
   the caller can tell "the same buffer again" from "silence again". They are
   the same fingerprint, and only the first of them is worth a warning: a run
   of silent callbacks is a pause, a fade-out or a starved ring, all of which
   have their own counters, while a run of identical *audible* buffers is the
   repeated-buffer static this exists to catch. */
static uint64_t flower_audio_bridge_fingerprint(const uint8_t* pData, uint32_t byteCount, int* pAudible)
{
    uint64_t hash = 14695981039346656037ULL;
    uint8_t seen = 0;
    uint32_t i;

    for (i = 0; i < byteCount; i += FLOWER_AUDIO_BRIDGE_FINGERPRINT_STRIDE) {
        hash ^= pData[i];
        hash *= 1099511628211ULL;
        seen |= pData[i];
    }

    if (pAudible != NULL) {
        *pAudible = (seen != 0);
    }

    return hash;
}

static void flower_audio_bridge_on_data(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount)
{
    flower_audio_bridge* pBridge = flower_audio_bridge_for_device(pDevice);
    uint8_t* pDestination = (uint8_t*)pOutput;
    uint32_t byteCount;
    uint64_t readIndex;
    uint64_t writeIndex;
    uint64_t flushRequest;
    uint32_t available;
    uint32_t toRead = 0;
    uint32_t offset;
    uint32_t firstChunk;
    uint64_t fingerprint;
    uint32_t channels;

    (void)pInput;

    if (pBridge == NULL || pOutput == NULL) {
        return;
    }

    byteCount = frameCount * pBridge->bytesPerFrame;

    flushRequest = atomic_load_explicit(&pBridge->flushRequest, memory_order_acquire);
    if (flushRequest != atomic_load_explicit(&pBridge->flushAcked, memory_order_relaxed)) {
        atomic_store_explicit(
            &pBridge->readIndex,
            atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire),
            memory_order_release);
        /* Silence on both sides of a flush, so the far side fades in from
           zero rather than stepping in at whatever level it left off. The
           target is left alone: a flush during a pause must stay quiet. */
        pBridge->gain = 0.0f;
        atomic_store_explicit(&pBridge->flushAcked, flushRequest, memory_order_release);
    }

    if (atomic_load_explicit(&pBridge->primed, memory_order_acquire) != 0) {
        readIndex = atomic_load_explicit(&pBridge->readIndex, memory_order_relaxed);
        writeIndex = atomic_load_explicit(&pBridge->writeIndex, memory_order_acquire);
        available = (writeIndex > readIndex) ? (uint32_t)(writeIndex - readIndex) : 0;
        toRead = (byteCount < available) ? byteCount : available;

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
    }

    if (toRead < byteCount) {
        memset(pDestination + toRead, 0, byteCount - toRead);
    }

    /* The envelope is deliberately not run while unprimed. The buffer is pure
       silence there, and ramping a fade-in across it would spend the fade on
       nothing - the first real audio after a flush would then arrive at full
       gain, which is the click the fade exists to remove. */
    if (atomic_load_explicit(&pBridge->primed, memory_order_relaxed) != 0) {
        channels = pBridge->bytesPerFrame / (uint32_t)sizeof(int16_t);
        if (channels > 0) {
            flower_audio_bridge_apply_envelope(pBridge, (int16_t*)pOutput, frameCount, channels);
        }
    }

    atomic_fetch_add_explicit(&pBridge->callbackCount, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&pBridge->requestedBytes, byteCount, memory_order_relaxed);
    atomic_fetch_add_explicit(&pBridge->realBytes, toRead, memory_order_relaxed);
    if (toRead < byteCount) {
        atomic_fetch_add_explicit(&pBridge->silenceBytes, byteCount - toRead, memory_order_relaxed);
        if (atomic_load_explicit(&pBridge->primed, memory_order_relaxed) != 0) {
            atomic_fetch_add_explicit(&pBridge->shortReadCount, 1, memory_order_relaxed);
            if (toRead == 0) {
                atomic_fetch_add_explicit(&pBridge->underrunCount, 1, memory_order_relaxed);
            }
        }
    }

    if (toRead > 0) {
        uint64_t previousFingerprint;
        uint64_t previousRead;
        int audible = 0;

        fingerprint = flower_audio_bridge_fingerprint(pDestination, toRead, &audible);
        previousFingerprint = atomic_exchange_explicit(&pBridge->lastPcmFingerprint, fingerprint, memory_order_relaxed);
        previousRead = atomic_exchange_explicit(&pBridge->lastReadBytes, toRead, memory_order_relaxed);
        if (audible && previousFingerprint == fingerprint && previousRead == toRead) {
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
