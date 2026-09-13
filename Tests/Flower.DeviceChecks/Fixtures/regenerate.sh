#!/usr/bin/env bash
# Regenerates the fixture set. Run from this directory; needs ffmpeg and
# afconvert (macOS). The fixtures are committed because a phone has no
# encoder, and because a fixture that is generated at run time can drift
# between the two platforms the checks exist to compare.
#
# All five hold the same two seconds of 440Hz stereo sine at 48kHz, which is
# the pipeline's own rate and channel count - so nothing legitimately
# resamples on the way, and the lossless three decode back to sine.wav's
# samples byte for byte. Verified: flac and alac both round-trip bit-exact.
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

ffmpeg -v error -y -i sine.wav -c:a flac sine.flac
ffmpeg -v error -y -i sine.wav -c:a libmp3lame -b:a 192k sine.mp3
afconvert -f m4af -d alac sine.wav sine-alac.m4a
afconvert -f m4af -d aac -b 192000 sine.wav sine-aac.m4a
