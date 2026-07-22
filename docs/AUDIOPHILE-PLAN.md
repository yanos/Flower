# Audiophile Playback Features Plan

Five requested features: gapless playback, multi-channel/hi-res sample rate support (24-bit/192kHz+), DSD/APE format support, a low-latency playback engine, and EQ with true bypass. Output-device selection itself is `AIRPLAY-BLUETOOTH-PLAN.md` territory, referenced here only where a feature depends on it. Not yet started.

## Key findings

- Today's stack: one plain `VlcAudioManager` (`Flower/Manager/VlcAudioManager.cs`) — no equalizer, no preloading, no output-format config. Auto-advance only starts the next track after `EndReached`, so the gap includes full demux/codec-open latency.
- Confirmed in installed `LibVLCSharp.dll` (3.10.0): `SetEqualizer(null)`/`UnsetEqualizer()` is a **true bypass**, not a flat 0dB filter. `SetAudioCallbacks`/`SetAudioFormat` exist (same seam `AIRPLAY-BLUETOOTH-PLAN.md` uses). No independent "pass multichannel through untouched" toggle exists in the API.
- Confirmed in `TagLibSharp.dll` (2.3.0): `.ape` and `.dsf` tag reading works today via the existing `TagLib.File.Create` call; `.dff` (DSDIFF) is not supported by this version.
- **Confirmed by inspecting the installed macOS VLC's native plugin directory: no Monkey's Audio or DSD demux/decode plugins exist.** Mainline VLC does not ship native `.ape`/`.dsf` playback support — a real gap, not an assumption. Android/iOS's LibVLC NuGets haven't been checked yet and could be in the same position.

## 1. EQ with true bypass — Small effort, Low risk

Extend `IAudioManager` with `SetEqualizer(EqualizerSettings? settings)` on top of the confirmed `Equalizer`/`UnsetEqualizer` API — `null` must call `UnsetEqualizer()`, not push an all-zero-dB filter. Persist an `EqualizerSettings` model (preamp, band gains, enabled flag, preset) via `AppSettings`, default disabled. New EQ panel: band sliders sized to `BandCount`, preamp, preset dropdown, an Enabled toggle that is the bypass control. Live-apply, no "apply" button.

## 2. Low-latency playback engine — Small effort, Low risk

Pass a lean explicit option set into `new LibVLC(options)` (skip video/subpicture/OSD/stats subsystems this audio-only app never uses; exact flags need validating against LibVLC 3.x at implementation time). Lower `file-caching` from LibVLC's ~300ms default since Flower only plays local files. Everything else (no `Media.Parse()` blocking on play, single long-lived `LibVLC`/`MediaPlayer`) is already fine — verified, no change needed.

## 3. DSD (`.dsf`) + Monkey's Audio (`.ape`) — Small effort for tagging, Medium-Large + Medium-High risk for playback

Add `.ape`/`.dsf` to `Importer._validExtensions` — tagging works today regardless of playback, so library browsing/sorting works immediately. Skip `.dff` until TagLib# supports it or a real library needs it. **Playback requires real engineering**, since no native plugins exist: either (a) build/source third-party VLC demux/decode plugins per platform, or (b) decode outside LibVLC (managed/native decoders feeding PCM via `SetAudioCallbacks`, same seam as gapless/AirPlay work). Until either lands, wrap `Play()` for these formats in a try/catch with a user-facing "unsupported format" message. Once playback exists, be clear in UI copy that DSD is decoded to PCM, not passed through natively.

## 4. Near-gapless playback (pragmatic step) — Medium effort, Low risk

Add `IAudioManager.Preload(Track)` (constructs + `Parse()`s the next `Media` ahead of time) and `PlayPreloaded()`. In `PlaylistControlViewModel`, once `PositionChanged` shows ~2-3s remaining, compute the next track once (respecting shuffle/repeat) and preload it — freeze that choice so `EndReached` plays exactly the preloaded track rather than re-rolling shuffle. This shortens the gap but isn't sample-accurate — still a hand-off between two `Media` instances.

## 5. Multi-channel / hi-res passthrough — Medium effort, Medium risk

Hard dependency on `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's device picker — VLC's aout modules only attempt sample-rate matching against an explicitly selected device, not the OS default. Once a device is selected: Windows WASAPI has an exclusive-mode setting (confirm exact confvar at implementation time), macOS `auhal` can match nominal sample rate to the stream, Linux is best-effort only (PulseAudio resamples; would need ALSA/PipeWire directly). Multichannel has no explicit toggle to add — just verify it already works when source and device both support >2 channels. This is a spike, not a scoped task yet.

## 6. True sample-accurate gapless (stretch) — Large effort, Medium-High risk

Needs a custom PCM pipeline via `SetAudioCallbacks`/`SetAudioFormat` that decodes ahead and feeds one continuous stream across track boundaries. **Don't build this twice** — if `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's `AVAudioEngine` bridge gets built, design its buffer/callback plumbing to also serve gapless transitions on Apple platforms. Until then, #4 is the standing solution.

## Suggested order

1. #1 EQ bypass — self-contained, no open questions.
2. #2 Low-latency tuning — alongside #1.
3. #3 DSD/APE — ship tagging/import any time; playback is its own much larger effort.
4. #4 Near-gapless preload — no external dependencies.
5. #5 Multi-channel/hi-res — after AirPlay/Bluetooth Phase 1 ships.
6. #6 True gapless — fold into AirPlay/Bluetooth Phase 2 if/when built, rather than a second custom pipeline.
