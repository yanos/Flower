// flower-ffmpeg - Flower's own narrow façade over FFmpeg's decode libraries.
//
// The point of this file is that it is small. Flower does not want FFmpeg's
// API; it wants one sentence of it - "open this, tell me its PCM format, give
// me interleaved samples, seek, close" - and it wants that sentence to have a
// stable ABI across five platform heads and two AOT runtimes. Every AVFrame,
// AVPacket, AVChannelLayout and ownership rule stays on the C side of this
// header, so the managed side is plain P/Invoke over ints and byte buffers
// and nothing in it has to track an FFmpeg struct layout.
//
// That is also why this is not FFmpeg.AutoGen: generated bindings would move
// the whole of FFmpeg's ABI into C#, and Flower would still have to build and
// ship the libraries. See docs/AUDIOPHILE-PLAN.md, "Decoder/backend spike".
//
// Links only against LGPL FFmpeg: avformat, avcodec, avutil, swresample. No
// GPL component may be enabled in the FFmpeg this is built against.

#ifndef FLOWER_FFMPEG_H
#define FLOWER_FFMPEG_H

#include <stdint.h>

#if defined(_WIN32)
#  define FLOWER_API __declspec(dllexport)
#else
#  define FLOWER_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Bumped whenever anything below changes shape. The managed side checks it at
// load and refuses a library it was not built against, because the failure
// mode of a silent mismatch is a struct read at the wrong offsets.
#define FLOWER_FFMPEG_ABI_VERSION 1

// What the caller wants out. FLOWER_SAMPLE_S24 is packed 3-byte little-endian,
// which is what miniaudio's ma_format_s24 expects and is not a format
// swresample can produce - the façade packs it from S32 (see
// flower_decoder_read). The others are swresample's own.
typedef enum {
    FLOWER_SAMPLE_S16 = 0,
    FLOWER_SAMPLE_S24 = 1,
    FLOWER_SAMPLE_S32 = 2,
    FLOWER_SAMPLE_F32 = 3
} flower_sample_format;

// 0 is success. Negative values below FLOWER_ERR_BASE are Flower's own;
// anything else negative is an AVERROR passed through untouched, so that
// flower_error_string can hand back FFmpeg's own diagnosis rather than
// flattening every failure into "could not open".
#define FLOWER_OK              0
#define FLOWER_EOF             1
#define FLOWER_ERR_BASE        (-10000)
#define FLOWER_ERR_ARGUMENT    (FLOWER_ERR_BASE - 1)
#define FLOWER_ERR_NO_AUDIO    (FLOWER_ERR_BASE - 2)
#define FLOWER_ERR_NO_MEMORY   (FLOWER_ERR_BASE - 3)
#define FLOWER_ERR_ABI         (FLOWER_ERR_BASE - 4)
#define FLOWER_ERR_IO          (FLOWER_ERR_BASE - 5)

// Read at most buf_size bytes. Returns the count, 0 at end of stream, or a
// negative value for an error. This is FFmpeg's own AVIOContext read
// signature on purpose: on the managed side it is SeekableHttpStream.Read
// with the arguments rearranged, which is what makes the streaming work built
// for LibVLC carry over unchanged.
typedef int  (*flower_read_fn)(void *opaque, uint8_t *buffer, int buf_size);
// whence is SEEK_SET/SEEK_CUR/SEEK_END, or FLOWER_SEEK_SIZE to be asked for
// the total length without moving. Returns the new position, or negative.
typedef int64_t (*flower_seek_fn)(void *opaque, int64_t offset, int whence);

#define FLOWER_SEEK_SIZE 0x10000

typedef struct {
    int32_t sample_rate;      // of the delivered PCM, after any resample
    int32_t channels;         // of the delivered PCM
    int32_t sample_format;    // flower_sample_format actually being delivered
    int32_t source_bit_depth; // meaningful bits in the source: 16, 24, 32...
    int32_t source_sample_rate;
    int32_t source_channels;
    int64_t duration_ms;      // -1 when the container does not say
} flower_decoder_format;

typedef struct flower_decoder flower_decoder;

// requested_sample_rate/channels of 0 mean "whatever the source is", which is
// how a bit-perfect direct-mode open asks for no conversion at all.
FLOWER_API int flower_decoder_open_path(const char *path,
                                        int32_t requested_format,
                                        int32_t requested_sample_rate,
                                        int32_t requested_channels,
                                        flower_decoder **out_decoder);

// size may be -1 when unknown. seekable 0 makes this a forward-only stream,
// and flower_decoder_seek will then refuse. format_hint may be NULL; when set
// it names a demuxer to force (FFmpeg's short name, e.g. "mp4"), skipping
// probing on a stream whose container is already known from the catalog.
FLOWER_API int flower_decoder_open_io(void *opaque,
                                      flower_read_fn read,
                                      flower_seek_fn seek,
                                      int64_t size,
                                      int32_t seekable,
                                      const char *format_hint,
                                      int32_t requested_format,
                                      int32_t requested_sample_rate,
                                      int32_t requested_channels,
                                      flower_decoder **out_decoder);

FLOWER_API int flower_decoder_get_format(flower_decoder *decoder,
                                         flower_decoder_format *out_format);

// Fills up to buffer_bytes of interleaved PCM. Writes the byte count to
// out_bytes, which is short only at end of stream. Returns FLOWER_OK,
// FLOWER_EOF once nothing more will come, or a negative error.
FLOWER_API int flower_decoder_read(flower_decoder *decoder,
                                   uint8_t *buffer,
                                   int32_t buffer_bytes,
                                   int32_t *out_bytes);

// Lands on or before position_ms - the demuxer is keyframe-bound, so the
// caller has to be told where it actually landed rather than assuming.
// Writes that to out_landed_ms.
FLOWER_API int flower_decoder_seek(flower_decoder *decoder,
                                   int64_t position_ms,
                                   int64_t *out_landed_ms);

FLOWER_API void flower_decoder_close(flower_decoder *decoder);

// Into caller-owned storage; never allocates, always NUL-terminates.
FLOWER_API void flower_error_string(int code, char *buffer, int32_t buffer_bytes);

FLOWER_API int32_t flower_abi_version(void);

#ifdef __cplusplus
}
#endif

#endif
