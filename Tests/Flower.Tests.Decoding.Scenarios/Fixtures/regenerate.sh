#!/usr/bin/env bash
# Regenerates the 48kHz fixture set. Run from this directory; needs ffmpeg
# (with libmp3lame, libvorbis and libopus), afconvert (macOS), and Monkey's
# Audio's own `mac` console encoder - FFmpeg decodes APE but cannot encode it,
# and `mac` builds with CMake from the SDK at monkeysaudio.com/developers.html. The fixtures are committed because a phone has no
# encoder, and because a fixture that is generated at run time can drift
# between the two platforms the checks exist to compare.
#
# All hold the same two seconds of 440Hz stereo sine at 48kHz, which is the
# pipeline's own rate and channel count - so nothing legitimately resamples
# on the way, and the lossless ones decode back to sine.wav's samples byte for
# byte. Verified: flac, alac, aiff and ape all round-trip bit-exact.
#
# sine.dsf is the one written here rather than encoded, since nothing on a
# Mac encodes DSD: the same tone through a second-order sigma-delta modulator
# at DSD64, in a DSF container. Its PCM is a filter's reading of one-bit
# samples, so it can only be held to the tone, never to sine.wav's bytes.
set -euo pipefail

python3 - <<'PY'
import math, struct
rate, ch, secs, hz, amp = 48000, 2, 2, 440.0, 16384
data = bytearray()
for f in range(rate * secs):
    s = max(-32768, min(32767, int(round(amp * math.sin(2 * math.pi * hz * f / rate)))))
    data += struct.pack('<h', s) * ch
header = (b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVEfmt '
          + struct.pack('<IHHIIHH', 16, 1, ch, rate, rate * ch * 2, ch * 2, 16)
          + b'data' + struct.pack('<I', len(data)))
open('sine.wav', 'wb').write(header + bytes(data))
PY

python3 - <<'PY'
import math, struct
rate, secs, hz, amp, block = 64 * 44100, 2, 440.0, 0.5, 4096
count = rate * secs
# One bit per sample, LSB first (DSF's "bits per sample" 1). Half scale, as
# sine.wav is, which also keeps a second-order loop well inside its stable range.
bits = bytearray((count + 7) // 8)
i1 = i2 = 0.0
y = -1.0
w = 2 * math.pi * hz / rate
for n in range(count):
    i1 += amp * math.sin(w * n) - y
    i2 += i1 - y
    y = 1.0 if i2 >= 0 else -1.0
    if y > 0:
        bits[n >> 3] |= 1 << (n & 7)
# Channels interleave by 4096-byte block, the last one padded. With 0x69, DSD's
# idle pattern, because 0x00 is not silence but full negative scale.
per_channel = -(-len(bits) // block) * block
bits += b'\x69' * (per_channel - len(bits))
data = bytearray()
for b in range(0, per_channel, block):
    data += bits[b:b + block] * 2
fmt = struct.pack('<IIIIIIQII', 1, 0, 2, 2, rate, 1, count, block, 0)
body = (b'fmt ' + struct.pack('<Q', 12 + len(fmt)) + fmt
        + b'data' + struct.pack('<Q', 12 + len(data)) + bytes(data))
open('sine.dsf', 'wb').write(b'DSD ' + struct.pack('<QQQ', 28, 28 + len(body), 0) + body)
PY

ffmpeg -v error -y -i sine.wav -c:a flac sine.flac
ffmpeg -v error -y -i sine.wav -c:a libmp3lame -b:a 192k sine.mp3
afconvert -f m4af -d alac sine.wav sine-alac.m4a
afconvert -f m4af -d aac -b 192000 sine.wav sine-aac.m4a
ffmpeg -v error -y -i sine.wav -c:a pcm_s16be sine.aiff
ffmpeg -v error -y -i sine.wav -c:a libvorbis -q:a 6 sine.ogg
ffmpeg -v error -y -i sine.wav -c:a libopus -b:a 160k sine.opus
mac sine.wav sine.ape -c2000
