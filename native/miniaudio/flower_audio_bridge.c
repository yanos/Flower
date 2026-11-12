/* flower_audio_bridge - see flower_audio_bridge.h for what and why.

   Its own translation unit, and on purpose: none of this depends on
   miniaudio. It takes struct ma_device* only as an opaque key for its device
   registry and never dereferences one, and the callback's signature is
   ma_device_data_proc's spelled in stdint types. So it compiles against
   nothing but its own header and the C standard library, and can be built
   for a platform whose miniaudio comes from somewhere else - desktop's,
   from the Miniaudio-CS NuGet (AUDIO-QUALITY-PLAN.md, Phase 6 steps 5-6).
   Keep it that way: an #include "miniaudio.h" here ends that. */

/* clock_gettime, which -std=c11 hides without asking for POSIX. */
#if !defined(_WIN32)
#define _POSIX_C_SOURCE 200809L
#endif

#include "flower_audio_bridge.h"

#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <time.h>
#endif

/* How many bridges can be attached at once. The app opens one device at a
   time; the tests attach several in parallel, each to a dummy device. The
   callback scans this linearly, which at 16 is still nothing. */
#define FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY 16
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

/* A callback arriving this long after the previous one started is counted
   as an interesting gap - see flower_audio_bridge_snapshot. */
#define FLOWER_AUDIO_BRIDGE_INTERESTING_GAP_NS 60000000ULL

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

/* No fused multiply-adds, so every platform renders the same bytes from the
   same input: clang fuses by default wherever the target has an FMA, which
   is every arm64 and only some x64 builds. Fusing would buy accuracy around
   -138dBFS at S24, below the dither, and speed nothing needs. GCC also needs
   -ffp-contract=off on the command line, which every build passes. */
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

    /* The device's rate, for the timing diagnostics' idea of a period. */
    uint32_t sampleRate;
#if defined(_WIN32)
    uint64_t counterFrequency;
#endif

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

    /* Callback timing. The previous callback's start and size are the
       callback's own; the rest is windowed and reset by take_snapshot, which
       is why it is atomic. timingResetRequested is how reset_timing reaches
       the callback-owned half from another thread. */
    uint64_t previousStartedAt;
    uint32_t previousFrameCount;
    _Atomic uint32_t timingResetRequested;
    _Atomic uint64_t timedCallbackCount;
    _Atomic uint64_t totalFrames;
    _Atomic uint64_t interestingGapCount;
    _Atomic uint64_t renderOverrunCount;
    _Atomic uint64_t maxGapNanoseconds;
    _Atomic uint64_t expectedNanosecondsAtMaxGap;
    _Atomic uint64_t maxLateNanoseconds;
    _Atomic uint64_t maxRenderNanoseconds;
    _Atomic uint32_t minFrames;
    _Atomic uint32_t maxFrames;
    _Atomic uint32_t precedingFramesAtMaxGap;
    _Atomic uint32_t currentFramesAtMaxGap;
};

typedef struct
{
    _Atomic(struct ma_device*) pDevice;
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

flower_audio_bridge* flower_audio_bridge_create(uint32_t capacityFrames, uint32_t channels, uint32_t deviceBytesPerSample, uint32_t sampleRate)
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

    if (sampleRate == 0) {
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

    pBridge->sampleRate = sampleRate;
#if defined(_WIN32)
    {
        LARGE_INTEGER frequency;
        QueryPerformanceFrequency(&frequency);
        pBridge->counterFrequency = (uint64_t)frequency.QuadPart;
    }
#endif

    pBridge->gain = 0.0f;
    atomic_store_explicit(&pBridge->fadeTargetBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    atomic_store_explicit(&pBridge->fadeStepBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);

    atomic_store_explicit(&pBridge->targetGainBits, flower_audio_bridge_float_to_bits(1.0f), memory_order_relaxed);
    pBridge->currentGain = 1.0f;

    /* Every slot starts as a bypass, which calloc already made it. */
    pBridge->eqFront = 0;
    atomic_store_explicit(&pBridge->eqMiddle, 1u, memory_order_relaxed);
    pBridge->eqBack = 2;

    /* Any non-zero seed: xorshift never leaves zero once it is there. */
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

static flower_audio_bridge* flower_audio_bridge_for_device(struct ma_device* pDevice)
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

int flower_audio_bridge_attach(flower_audio_bridge* pBridge, struct ma_device* pDevice)
{
    uint32_t i;

    if (pBridge == NULL || pDevice == NULL) {
        return 0;
    }

    flower_audio_bridge_detach(pBridge);

    for (i = 0; i < FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY; ++i) {
        struct ma_device* expected = NULL;
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

    /* The bridge is cleared before the device, which is what frees the
       slot: the other way round, an attach could claim the slot in between
       and have its bridge cleared from under it. */
    for (i = 0; i < FLOWER_AUDIO_BRIDGE_DEVICE_CAPACITY; ++i) {
        if (atomic_load_explicit(&g_flowerAudioBridges[i].pBridge, memory_order_acquire) == pBridge) {
            atomic_store_explicit(&g_flowerAudioBridges[i].pBridge, NULL, memory_order_release);
            atomic_store_explicit(&g_flowerAudioBridges[i].pDevice, NULL, memory_order_release);
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

/* The declick: the next audio rendered comes from somewhere else entirely -
   after a flush, or after a gap the stream itself left - and its first
   sample is rarely zero, so it is faded in over DeclickFadeMs rather than
   stepped in at full level. */
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

    /* Written so that a NaN lands on silence rather than passing both
       comparisons. */
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

    pSnapshot->timedCallbackCount = atomic_exchange_explicit(&pBridge->timedCallbackCount, 0, memory_order_relaxed);
    pSnapshot->totalFrames = atomic_exchange_explicit(&pBridge->totalFrames, 0, memory_order_relaxed);
    pSnapshot->interestingGapCount = atomic_exchange_explicit(&pBridge->interestingGapCount, 0, memory_order_relaxed);
    pSnapshot->renderOverrunCount = atomic_exchange_explicit(&pBridge->renderOverrunCount, 0, memory_order_relaxed);
    pSnapshot->maxGapNanoseconds = atomic_exchange_explicit(&pBridge->maxGapNanoseconds, 0, memory_order_relaxed);
    pSnapshot->expectedNanosecondsAtMaxGap = atomic_exchange_explicit(&pBridge->expectedNanosecondsAtMaxGap, 0, memory_order_relaxed);
    pSnapshot->maxLateNanoseconds = atomic_exchange_explicit(&pBridge->maxLateNanoseconds, 0, memory_order_relaxed);
    pSnapshot->maxRenderNanoseconds = atomic_exchange_explicit(&pBridge->maxRenderNanoseconds, 0, memory_order_relaxed);
    pSnapshot->minFrames = atomic_exchange_explicit(&pBridge->minFrames, 0, memory_order_relaxed);
    pSnapshot->maxFrames = atomic_exchange_explicit(&pBridge->maxFrames, 0, memory_order_relaxed);
    pSnapshot->precedingFramesAtMaxGap = atomic_exchange_explicit(&pBridge->precedingFramesAtMaxGap, 0, memory_order_relaxed);
    pSnapshot->currentFramesAtMaxGap = atomic_exchange_explicit(&pBridge->currentFramesAtMaxGap, 0, memory_order_relaxed);
}

/* The timing half of the window only: the byte counters belong to the
   watchdog's own reckoning, which a pause does not interrupt. */
void flower_audio_bridge_reset_timing(flower_audio_bridge* pBridge)
{
    if (pBridge == NULL) {
        return;
    }

    atomic_store_explicit(&pBridge->timingResetRequested, 1, memory_order_release);
    atomic_store_explicit(&pBridge->timedCallbackCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->totalFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->interestingGapCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->renderOverrunCount, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->maxGapNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->expectedNanosecondsAtMaxGap, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->maxLateNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->maxRenderNanoseconds, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->minFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->maxFrames, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->precedingFramesAtMaxGap, 0, memory_order_relaxed);
    atomic_store_explicit(&pBridge->currentFramesAtMaxGap, 0, memory_order_relaxed);
}

/* ------------------------------------------------------------------ */
/* The output stage: everything that happens to PCM between the queue  */
/* and the sound card, in float, ending in the one requantisation to   */
/* the device's format. Its only implementation; the EQ's coefficients */
/* come from Equalizer.BuildFrom in C#. Tests/Flower.Tests/Audio holds */
/* it to its properties (OutputStageTests, CanonicalFormatTests,       */
/* EqualizerTests), on each desktop and in the iOS suite.              */
/* ------------------------------------------------------------------ */

/* The EQ: RBJ peaking biquads, run on every sample rendered, silence
   padding included, so the delay lines never resume from pre-gap state on
   the far side of a gap. Float in, float out, and no clipping of its own:
   a boost past full scale is the requantiser's to clamp, at the end. */
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

            /* Transposed direct form II: y = b0*x + z1;
               z1' = b1*x - a1*y + z2; z2' = b2*x - a2*y. */
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

/* A new curve is crossfaded in over this buffer from the old one's output,
   rather than dropped onto the running signal with zeroed delay lines,
   which clicks. A buffer is a few milliseconds, inaudible as a blend, and
   by its end the new filter's state has been driven by real signal. */
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

/* Every gain in one pass, since they multiply: the ramp toward the volume
   target (a step change is zipper noise), the declick fade-in, and the
   transport envelope that makes a pause or a resume click-free and
   immediate however deep the queue is. */
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

/* xorshift32 - a handful of instructions and plenty of quality for a dither
   source - scaled to +-0.5 LSB. */
static float flower_audio_bridge_next_dither(flower_audio_bridge* pBridge)
{
    uint32_t x = pBridge->ditherState;
    x ^= x << 13;
    x ^= x >> 17;
    x ^= x << 5;
    pBridge->ditherState = x;
    return ((float)x / (float)UINT32_MAX) - 0.5f;
}

/* Float -> the device's integer format, once, at the end.

   Scaled to the destination's integer units first, so the dither, the
   clamp and the "already an exact integer" test are the same code at 16
   bits and at 24. The scale is a power of two, so a sample that came from
   an integer source no wider than the destination lands back on exactly
   that integer: at unity gain with no EQ, the round trip is bit-identical.

   Only what needs rounding is dithered. A sample already on an integer was
   changed by nothing upstream, and noise added to it would break that
   transparency; it also keeps digital silence silent and lets a fade-out
   reach zero. The dither is TPDF - successive rectangular draws
   differenced, a triangle one LSB wide - which decorrelates the error from
   the signal, so it is a flat noise floor rather than distortion, and
   leaves no noise modulation.

   The clamp is hard, at the destination's own full scale, deliberately and
   having tried a soft knee: a source has to come back bit-identical from a
   unity-gain pass, and a knee starting below full scale attenuates real
   signal for headroom nothing needs (one starting at full scale is a clamp).
   Nothing before this can clip, since it all runs in float; only an EQ
   boost past full scale reaches it, which is the listener asking for it.

   rintf rounds half to even under the default rounding mode. */
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

/* Monotonic nanoseconds. Both are cheap enough for the render thread and
   neither blocks: clock_gettime is a vDSO read wherever Flower runs, and
   QueryPerformanceCounter reads the TSC. */
static uint64_t flower_audio_bridge_now(const flower_audio_bridge* pBridge)
{
#if defined(_WIN32)
    LARGE_INTEGER counter;
    uint64_t ticks;
    uint64_t frequency = pBridge->counterFrequency;

    QueryPerformanceCounter(&counter);
    ticks = (uint64_t)counter.QuadPart;
    /* Split, so ticks * 1e9 cannot overflow after a few days of uptime. */
    return (ticks / frequency) * 1000000000ULL + (ticks % frequency) * 1000000000ULL / frequency;
#else
    struct timespec now;

    (void)pBridge;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000000000ULL + (uint64_t)now.tv_nsec;
#endif
}

/* How long frameCount frames last at the device's rate, never zero. */
static uint64_t flower_audio_bridge_period_ns(const flower_audio_bridge* pBridge, uint32_t frameCount)
{
    uint64_t ns = (uint64_t)frameCount * 1000000000ULL / pBridge->sampleRate;
    return (ns == 0) ? 1 : ns;
}

/* Raises *pTarget to value, against take_snapshot resetting it to zero
   from the watchdog's thread meanwhile. Reports whether it did. */
static int flower_audio_bridge_raise_u64(_Atomic uint64_t* pTarget, uint64_t value)
{
    uint64_t current = atomic_load_explicit(pTarget, memory_order_relaxed);
    while (value > current) {
        if (atomic_compare_exchange_weak_explicit(pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
            return 1;
        }
    }

    return 0;
}

static void flower_audio_bridge_raise_u32(_Atomic uint32_t* pTarget, uint32_t value)
{
    uint32_t current = atomic_load_explicit(pTarget, memory_order_relaxed);
    while (value > current && !atomic_compare_exchange_weak_explicit(
        pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
    }
}

/* Zero means none seen yet this window. */
static void flower_audio_bridge_lower_u32(_Atomic uint32_t* pTarget, uint32_t value)
{
    uint32_t current = atomic_load_explicit(pTarget, memory_order_relaxed);
    while ((current == 0 || value < current) && !atomic_compare_exchange_weak_explicit(
        pTarget, &current, value, memory_order_relaxed, memory_order_relaxed)) {
    }
}

/* One callback's timing, recorded after it rendered. The gap from callback
   N-1's start to N's covers the frames N-1 asked for, not N's, which is
   what tells a stall from a deliberately larger callback. */
static void flower_audio_bridge_record_timing(flower_audio_bridge* pBridge, uint64_t startedAt, uint64_t completedAt, uint32_t frameCount)
{
    uint64_t previousStartedAt;
    uint32_t previousFrames;

    if (atomic_exchange_explicit(&pBridge->timingResetRequested, 0, memory_order_acquire) != 0) {
        pBridge->previousStartedAt = 0;
        pBridge->previousFrameCount = 0;
    }

    previousStartedAt = pBridge->previousStartedAt;
    previousFrames = pBridge->previousFrameCount;
    pBridge->previousStartedAt = startedAt;
    pBridge->previousFrameCount = frameCount;

    flower_audio_bridge_lower_u32(&pBridge->minFrames, frameCount);
    flower_audio_bridge_raise_u32(&pBridge->maxFrames, frameCount);
    atomic_fetch_add_explicit(&pBridge->timedCallbackCount, 1, memory_order_relaxed);
    atomic_fetch_add_explicit(&pBridge->totalFrames, frameCount, memory_order_relaxed);

    if (previousStartedAt != 0 && startedAt > previousStartedAt) {
        uint64_t gap = startedAt - previousStartedAt;
        uint64_t expected = flower_audio_bridge_period_ns(pBridge, previousFrames == 0 ? 1 : previousFrames);

        if (flower_audio_bridge_raise_u64(&pBridge->maxGapNanoseconds, gap)) {
            atomic_store_explicit(&pBridge->expectedNanosecondsAtMaxGap, expected, memory_order_relaxed);
            atomic_store_explicit(&pBridge->precedingFramesAtMaxGap, previousFrames, memory_order_relaxed);
            atomic_store_explicit(&pBridge->currentFramesAtMaxGap, frameCount, memory_order_relaxed);
        }

        if (gap > expected) {
            flower_audio_bridge_raise_u64(&pBridge->maxLateNanoseconds, gap - expected);
        }

        if (gap >= FLOWER_AUDIO_BRIDGE_INTERESTING_GAP_NS) {
            atomic_fetch_add_explicit(&pBridge->interestingGapCount, 1, memory_order_relaxed);
        }
    }

    if (completedAt > startedAt) {
        uint64_t render = completedAt - startedAt;
        flower_audio_bridge_raise_u64(&pBridge->maxRenderNanoseconds, render);
        if (render > flower_audio_bridge_period_ns(pBridge, frameCount)) {
            atomic_fetch_add_explicit(&pBridge->renderOverrunCount, 1, memory_order_relaxed);
        }
    }
}

static void flower_audio_bridge_on_data(struct ma_device* pDevice, void* pOutput, const void* pInput, uint32_t frameCount)
{
    flower_audio_bridge* pBridge = flower_audio_bridge_for_device(pDevice);
    uint8_t* pDestination = (uint8_t*)pOutput;
    uint32_t deviceBytesPerFrame;
    uint32_t sourceByteCount;
    uint32_t totalRead = 0;
    uint32_t done = 0;
    uint64_t flushRequest;
    uint64_t fingerprint = 14695981039346656037ULL;
    uint64_t startedAt;
    int audible = 0;
    int primed;

    (void)pInput;

    if (pBridge == NULL || pOutput == NULL) {
        return;
    }

    startedAt = flower_audio_bridge_now(pBridge);

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
           target is left alone: a flush during a pause must stay quiet. */
        pBridge->gain = 0.0f;
        flower_audio_bridge_arm_declick(pBridge);
        atomic_store_explicit(&pBridge->flushAcked, flushRequest, memory_order_release);
    }

    /* Nothing is rendered or processed while unprimed - not the envelope,
       not the EQ, not the ramp. Running any of them over the silence would
       spend the declick on nothing, and the first real audio would then
       arrive at full gain, which is the click it exists to remove.

       A gap the feeder un-primed for is a rebuffer, which no flush marks, so
       the declick is armed on the way back in as well. */
    primed = atomic_load_explicit(&pBridge->primed, memory_order_acquire) != 0;
    if (primed && !pBridge->wasPrimed) {
        flower_audio_bridge_arm_declick(pBridge);
    }

    pBridge->wasPrimed = primed;

    if (primed && !pBridge->hasRendered) {
        /* A bridge is made for each device open, so the ramp starts at the
           target: starting it from unity would sweep the volume down from
           full at the start of every one. */
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

    /* In the bridge's bytes - the pipeline's - which the watchdog sets
       against the ring's own. */
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

    flower_audio_bridge_record_timing(pBridge, startedAt, flower_audio_bridge_now(pBridge), frameCount);
}

void* flower_audio_bridge_data_callback(void)
{
    return (void*)flower_audio_bridge_on_data;
}
