# Bluetooth (all platforms) + AirPlay (macOS/iOS) — Output Routing Plan

Scope: **Bluetooth output** on Windows, macOS, Linux, Android (iOS gets it as a side effect of Phase 2). **AirPlay sender** on macOS/iOS only — Windows/Linux have no viable AirPlay sender path (no first-party access; third-party reverse-engineered senders like `pyatv`/`shairplay` are stuck on legacy AirPlay 1/RAOP and don't work against AirPlay 2 receivers), dropped from scope by decision.

## Key findings

- Bluetooth and AirPlay (via macOS CoreAudio routing) already work today with zero code as long as the user sets the OS default output manually — this plan adds an in-app picker instead of sending users to System Settings.
- LibVLC's `RendererDiscoverer` API only surfaces Chromecast/UPnP — dead end for AirPlay.
- LibVLCSharp 3.10.0 (used on every target) exposes `MediaPlayer.SetAudioCallbacks`/`SetAudioFormatCallback` as managed API — lets Flower keep LibVLC for decode/seek/metadata and redirect only the output stage into `AVAudioEngine` on Apple platforms.
- `AVRoutePickerView` (AVKit) is Apple's native Bluetooth+AirPlay route picker (same one Music uses) — Phase 2 hangs its UI on this rather than a custom picker.
- Android/Windows need no Phase 2 work: OS-level A2DP pairing already transparently redirects all app audio once Bluetooth is the active output.

## Phase 1 — In-app output-device picker (Windows/macOS/Linux)

- Extend `IAudioManager` with `GetOutputDevices()` and `SetOutputDevice(id)`, backed by LibVLCSharp's existing `AudioOutputDeviceEnum`/`AudioOutputDeviceSet` — no native interop needed.
- Small picker flyout next to `VolumeControl`, re-enumerated on open (no hotplug subscription). No persistence across relaunches for v1.
- Desktop-only, gated like `NativeMenu`; hides itself on macOS once Phase 2's `AVRoutePickerView` ships.
- Effort: Small–Medium. Risk: Low (purely additive).

## Phase 2 — macOS + iOS: real AirPlay + Bluetooth via `AVAudioEngine`

`AVRoutePickerView` requires audio to actually play through `AVAudioEngine`, not LibVLC's own output module.

- Hook `SetAudioFormatCallback`/`SetAudioCallbacks` on macOS/iOS only; feed PCM frames into a ring buffer that an `AVAudioSourceNode` drains. **Riskiest piece**: bridging LibVLC's push-based callback to `AVAudioEngine`'s pull-based render cadence needs a real buffering/underrun strategy.
- Host `AVRoutePickerView` via Avalonia's `NativeControlHost`, macOS/iOS only, replacing Phase 1's picker on macOS (iOS has no Phase 1 picker to replace).
- iOS also needs `AVAudioSession` (`.playback` category) and a route-change handler — no mobile audio-session code exists yet.
- Open question to resolve before implementing: once LibVLC's output is bypassed, does `MediaPlayer.Volume` still work, or does volume need to move into the callback/mixer node? Verify before wiring up UI.
- Effort: Large — first time `IAudioManager` needs a real per-platform fork. Risk: Medium–High.

## Phase 3 — Android: verify, don't build

No work expected. Once a Bluetooth device is the active output, Android routes all app audio through it automatically. Confirm with a smoke test once a device/emulator is available (rides on `CROSS-PLATFORM-PLAN.md`'s mobile-verification gaps). Effort/Risk: None.

## Suggested order

1. Phase 1 (cheap, no risk, ships first).
2. Phase 3 (trivial, whenever a device is available).
3. Phase 2 (biggest/riskiest; also depends on mobile LibVLC playback being confirmed working end-to-end first).
