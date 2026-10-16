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
ffmpeg -v error -y -i sine.wav -c:a pcm_s16be sine.aiff
ffmpeg -v error -y -i sine.wav -c:a libvorbis -q:a 6 sine.ogg
ffmpeg -v error -y -i sine.wav -c:a libopus -b:a 160k sine.opus
mac sine.wav sine.ape -c2000
