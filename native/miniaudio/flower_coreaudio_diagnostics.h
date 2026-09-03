#ifndef flower_coreaudio_diagnostics_h
#define flower_coreaudio_diagnostics_h

#include <stdint.h>

struct ma_device;

/*
 * Flower-owned, iOS-only diagnostics around miniaudio's CoreAudio render
 * callback. The callback path only performs atomic counter updates; managed
 * code snapshots and formats them later from its watchdog thread.
 */
typedef struct
{
    uint64_t callbackCount;
    uint64_t requestedFrames;
    uint64_t submittedFrames;
    uint64_t actionFlags;
    uint64_t maxCallbackGapNanoseconds;
    uint64_t maxHostTimeGapNanoseconds;
    uint64_t maxCallbackDurationNanoseconds;
    uint64_t maxSampleDelta;
    uint64_t abruptFrameCount;
    uint64_t repeatedBufferCount;
    uint32_t minFrames;
    uint32_t maxFrames;
    uint32_t maxActionFlags;
    uint32_t maxRepeatedBufferRun;
} flower_coreaudio_diagnostics_snapshot;

int flower_coreaudio_diagnostics_register(struct ma_device* pDevice);
void flower_coreaudio_diagnostics_unregister(struct ma_device* pDevice);
int flower_coreaudio_diagnostics_take_snapshot(
    struct ma_device* pDevice,
    flower_coreaudio_diagnostics_snapshot* pSnapshot);

void flower_coreaudio_diagnostics_callback_started(
    struct ma_device* pDevice,
    uint32_t frameCount,
    uint64_t hostTime,
    uint32_t actionFlags);
void flower_coreaudio_diagnostics_frames_submitted(
    struct ma_device* pDevice,
    uint32_t frameCount);
void flower_coreaudio_diagnostics_pcm_submitted_s16_interleaved(
    struct ma_device* pDevice,
    const int16_t* pFrames,
    uint32_t frameCount,
    uint32_t channelCount);
void flower_coreaudio_diagnostics_pcm_submitted_f32_interleaved(
    struct ma_device* pDevice,
    const float* pFrames,
    uint32_t frameCount,
    uint32_t channelCount);
void flower_coreaudio_diagnostics_callback_completed(
    struct ma_device* pDevice,
    uint32_t actionFlags);

#endif
