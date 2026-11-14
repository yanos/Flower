# Native Security Plan

A memory-safety review of Flower's own native code: `libflowerbridge`
(`native/miniaudio/flower_audio_bridge.c/.h`), `native/miniaudio/impl.c` (the
iOS CoreAudio diagnostics), the Flower patches in
`native/miniaudio/vendor/miniaudio.h` (`ma_on_output__coreaudio`), and the
managed side that drives them (`NativeAudioBridge`, `AudioFeeder`,
`MiniaudioSink`). FFAudio.NET is out of scope.

**Status (November 2026): reviewed; all five findings fixed.** No
buffer overflow in the C itself: every copy is bounded by checks it makes,
and nothing a song carries (sizes, tags) reaches a length or an index -
decoded audio enters only as float sample values. The bridge still relies
on its caller to stop callbacks before destroying a bridge, at the
managed/native boundary. Anything in the bridge's C means rerunning
`native/miniaudio/{desktop,android,ios}/build.sh` (the source stamps fail CI
otherwise).

## Fixed

- **1. Use-after-free when the feeder outlives its join.** `AudioFeeder.Stop`
  reports whether its thread exited; when it did not, `MiniaudioSink.CloseDevice`
  detaches the bridge but leaks it rather than freeing it under a live
  writer, and logs a warning. `AudioFeederTests` holds a feeder inside a
  `Write` (`FakeAudioBridge.OnWrite`) to check the report.
- **2. A flush fallback could push the output to full scale.**
  `flower_audio_bridge_apply_gain` clamps `fadeInRemaining` to
  `fadeInLength` before it computes the envelope, so whatever races
  `flush_now`, the worst outcome is a restarted fade. All three platforms
  rebuilt. No test reaches it: the state needs a callback racing
  `flush_now` mid-write.

### 3. Guard device-registry reads against slot reuse (low, defensive) — fixed

`flower_audio_bridge_for_device` matches a slot's `pDevice`, then loads its
`pBridge` separately. A slot detached and reused in between would hand a
callback another device's bridge, and with a different
`deviceBytesPerSample` (S24 against S16) it would write 1.5 times past the end
of the output buffer. Today `device_uninit` always runs before detach, so no
callback for that device can be in flight; the C should not depend on that.

After loading `pBridge`, the lookup now loads `pDevice` again and returns
NULL if it changed. This is a defensive check; it does not replace the
requirement to stop callbacks before destroying a bridge.

### 4. Reject capacities past INT32_MAX bytes (very low) — fixed

`flower_audio_bridge_create` previously allowed a byte capacity up to `UINT32_MAX`;
`NativeAudioBridge.Capacity` and `Available` cast it to `int`. Past about
268M frames the feeder sees a negative capacity and stops writing - not
unsafe, just wrong. `create` now refuses a capacity over `INT32_MAX` bytes
before allocating. `NativeAudioBridgeTests` checks the first overflowing
frame count and a larger one through the managed/native boundary, for
both S16 and S24 output.

### 5. Build hardening (low) — fixed

The Linux bridge and both Android libraries now explicitly enable
`-fstack-protector-strong`, `-D_FORTIFY_SOURCE=2` and
`-Wl,-z,relro,-z,now`. The bridge has almost no
stack buffers, but `impl.c` compiles all of miniaudio, which does. Fortify
adds `__memcpy_chk`, which libc provides, so the dependency checks in
`desktop/build.sh` still pass; the arm64 stack guard also needs glibc's
loader, allowed at the same version floor. Both build scripts check the
resulting ELF files for a RELRO segment and eager symbol binding.

## Validation

- Rebuilt all six desktop RIDs, all three Android ABIs, and the iOS device
  and simulator frameworks; all three source-checksum checks pass.
- Desktop export, dependency, glibc-floor and floating-point checks pass;
  Linux and Android full-RELRO checks pass.
- `Flower.Tests`: 2,596 passed, including four capacity-regression cases.
- iOS NativeAOT simulator and Android emulator decoding/native-output
  runners: 106 passed each, zero failures.

## Checked and clean

- **Ring** (`flower_audio_bridge_write`, `_read`): indices are 64-bit and
  only increase, used never exceeds capacity, the wrap-around split copies
  are right, writes are whole frames only, and `create` checks the capacity
  multiplication for overflow.
- **Callback scratch:** each chunk is at most
  `FLOWER_AUDIO_BRIDGE_CHUNK_FRAMES` (4096) frames, and `pSamples` and
  `pCrossfade` are sized for exactly that.
- **EQ:** `bandCount` is capped at 10; `NativeAudioBridge.SetEqualizer`
  always passes a 50-float buffer; the triple-buffer indices stay in 0..2.
- **Requantiser:** NaN and infinities are clamped or zeroed before the
  integer cast; destination offsets are `size_t`. The bytes written match
  miniaudio's buffer because `MiniaudioSink` sets `playback.format` and
  `playback.channels` explicitly, so miniaudio converts to what the bridge
  was created for.
- **Teardown order** (finding 1 aside): feeder, `device_uninit`, detach,
  destroy.
- **CoreAudio diagnostics** (`impl.c`, the patched `ma_on_output__coreaudio`):
  read only buffers sized from the buffer list, and only after checking the
  internal format and channel count; the deinterleaved path stays inside its
  4KB `tempBuffer`. One harmless quirk:
  `flower_coreaudio_diagnostics_register` can register one device in a
  second slot.
