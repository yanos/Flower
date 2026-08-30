# Audiophile Playback Features Plan

Five requested features: gapless playback, multi-channel/hi-res sample rate support (24-bit/192kHz+), DSD/APE format support, a low-latency playback engine, and EQ with true bypass. Output-device selection itself is `AIRPLAY-BLUETOOTH-PLAN.md` territory, referenced here only where a feature depends on it. **#1 (EQ) and #6 (true sample-accurate gapless) are done. #4 was superseded by #6 and will never be built. #2, #3 and #5 are open, and all three are scoped against a render path that no longer exists** — see the note below.

## A note on what is stale here

Items #2 and #5 below are written against LibVLC as the *output* stage — its
`aout` modules, its `file-caching`, its device selection. That stopped being
true. LibVLC is decode-only now: every platform renders through `MiniaudioSink`
pulling canonical S16/48k/stereo PCM out of `GaplessRingBuffer`. Neither item is
wrong about *what the user wants*; both are wrong about where the knob lives, so
each needs re-scoping against miniaudio before it is actionable. #1 already hit
this and was re-scoped when it was built; #6 hit it and was built somewhere else
entirely.

## Key findings

- Today's stack: one plain `VlcAudioManager` (`Flower/Manager/VlcAudioManager.cs`) — no equalizer, no preloading, no output-format config. Auto-advance only starts the next track after `EndReached`, so the gap includes full demux/codec-open latency.
- Confirmed in installed `LibVLCSharp.dll` (3.10.0): `SetEqualizer(null)`/`UnsetEqualizer()` is a **true bypass**, not a flat 0dB filter. `SetAudioCallbacks`/`SetAudioFormat` exist (same seam `AIRPLAY-BLUETOOTH-PLAN.md` uses). No independent "pass multichannel through untouched" toggle exists in the API. **No longer the implementation path for #1 below** — this API applied to `LibVlcRawStreamSink`'s render `MediaPlayer`, which every platform's render path has since moved off (see `MiniaudioSink`'s own class comment); the finding itself still stands as a fact about LibVLC, just not one anything in this codebase calls anymore.
- Confirmed in `TagLibSharp.dll` (2.3.0): `.ape` and `.dsf` tag reading works today via the existing `TagLib.File.Create` call; `.dff` (DSDIFF) is not supported by this version.
- **Confirmed by inspecting the installed macOS VLC's native plugin directory: no Monkey's Audio or DSD demux/decode plugins exist.** Mainline VLC does not ship native `.ape`/`.dsf` playback support — a real gap, not an assumption. Android/iOS's LibVLC NuGets haven't been checked yet and could be in the same position.

## 1. EQ with true bypass — Done

Implemented as a 10-band graphic EQ, not against LibVLC (see above) — LibVLC is decode-only now, and every platform renders through `MiniaudioSink`, a plain `ma_device` pulling raw PCM from `GaplessRingBuffer`. The EQ is a pure-C# RBJ-cookbook peaking/bell biquad cascade (`Equalizer`/`EqualizerSettings`, `Flower/Manager/`), spliced directly into `MiniaudioSink.DataCallback` after the ring read. `IAudioSink.ApplyEqualizer(Equalizer? equalizer)` carries a rebuilt-and-atomically-swapped processor down from `IAudioManager`/`GaplessAudioManager`; passing `null` is **true bypass** — `DataCallback` skips the processing call entirely rather than running an all-zero-dB filter, preserving this section's original bypass requirement under the new render pipeline.

Fixed at 10 bands (31Hz–16kHz, ISO-ish spacing, `Q≈1.41`), ±12dB per band, plus a preamp stage applied before the cascade — no presets, no parametric (frequency/Q) adjustment; out of scope for what was asked. `EqualizerSettings` (enabled flag, preamp, 10 band gains) persists via `AppSettings.EqualizerSettings`/`AppSettingsStore`, and is eagerly re-applied at startup in `App.axaml.cs` — not only when the Equalizer window happens to be opened. UI: `EqualizerWindow`/`EqualizerViewModel`, reachable via **View → Equalizer…**, live-apply with no "Apply" button. A settings change rebuilds the whole processor (fresh coefficients and fresh filter delay-line state together), which can produce a minor transient click on a slider change — an accepted simplicity tradeoff, not coefficient-smoothed/crossfaded.

## 2. Low-latency playback engine — Small effort, Low risk

Pass a lean explicit option set into `new LibVLC(options)` (skip video/subpicture/OSD/stats subsystems this audio-only app never uses; exact flags need validating against LibVLC 3.x at implementation time). Lower `file-caching` from LibVLC's ~300ms default since Flower only plays local files. Everything else (no `Media.Parse()` blocking on play, single long-lived `LibVLC`/`MediaPlayer`) is already fine — verified, no change needed.

## 3. DSD (`.dsf`) + Monkey's Audio (`.ape`) — Small effort for tagging, Medium-Large + Medium-High risk for playback

Add `.ape`/`.dsf` to `Importer._validExtensions` — tagging works today regardless of playback, so library browsing/sorting works immediately. Skip `.dff` until TagLib# supports it or a real library needs it. **Playback requires real engineering**, since no native plugins exist: either (a) build/source third-party VLC demux/decode plugins per platform, or (b) decode outside LibVLC (managed/native decoders feeding PCM via `SetAudioCallbacks`, same seam as gapless/AirPlay work). Until either lands, wrap `Play()` for these formats in a try/catch with a user-facing "unsupported format" message. Once playback exists, be clear in UI copy that DSD is decoded to PCM, not passed through natively.

## 4. Near-gapless playback (pragmatic step) — Superseded, never built

This was the hedge: `IAudioManager.Preload(Track)` constructing and `Parse()`ing
the next `Media` early, `PlayPreloaded()` to start it, and
`PlaylistControlViewModel` freezing its choice of next track at ~2-3s remaining
so `EndReached` played exactly what had been preloaded rather than re-rolling
shuffle. It would have shortened the gap without closing it — still a hand-off
between two `Media` instances.

**It was never built, and should not be.** #6 went straight to the real thing,
so there is no gap left for this to shorten. The one piece of it that turned out
to matter independently — committing to the next track early rather than
re-rolling shuffle at the boundary — is what `GaplessCoordinator.SetUpcoming`
does, and it has to: the armed decoder *is* the frozen choice.

Kept here rather than deleted because the reasoning is the useful part. A
pragmatic step that gets skipped because the full version landed first is a
better outcome than one that ships and then has to be unpicked.

## 5. Multi-channel / hi-res passthrough — Medium effort, Medium risk

Hard dependency on `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's device picker — VLC's aout modules only attempt sample-rate matching against an explicitly selected device, not the OS default. Once a device is selected: Windows WASAPI has an exclusive-mode setting (confirm exact confvar at implementation time), macOS `auhal` can match nominal sample rate to the stream, Linux is best-effort only (PulseAudio resamples; would need ALSA/PipeWire directly). Multichannel has no explicit toggle to add — just verify it already works when source and device both support >2 channels. This is a spike, not a scoped task yet.

## 6. True sample-accurate gapless — Done

Built, and not the way this section sketched. The plan was a custom PCM pipeline
hung off LibVLC's `SetAudioCallbacks`/`SetAudioFormat`, folded into
`AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's `AVAudioEngine` bridge so it wouldn't be
built twice. What shipped needs neither: LibVLC decodes, and **every** platform
renders through `MiniaudioSink`, so there is exactly one render path to be
gapless on and no Apple-specific bridge to wait for. The "don't build this
twice" warning was answered by deleting the second path, not by sharing it.

The pipeline (`Flower/Manager/`):

- `GaplessFormat` — one canonical PCM format (S16N/48kHz/stereo) every track is
  decoded to, so a track boundary is never a format change and the render sink
  never reconfigures mid-stream. S16N specifically, because LibVLC 3.0.x's
  `amem` module hardcodes it and silently ignores the requested fourcc.
- `TrackDecoder` — one track's decode, writing canonical PCM through a
  `RetargetableRingWriter`.
- `GaplessRingBuffer` — single-producer/single-consumer byte ring. `Read()` is
  lock-free and never blocks, so it is safe to call from a real-time render
  callback; `Reset()` bumps a generation rather than touching either index,
  which closed a real corruption window.
- `RetargetableRingWriter` — makes "drain the staging ring, then write to the
  shared one" atomic against the decoder's own producer thread, so the handover
  loses and duplicates nothing.
- `GaplessCoordinator` — the current/armed decoder pair, the handover, and the
  playback position.
- `GaplessAudioManager` — the `IAudioManager` over all of it.

Two things were learned the hard way and are load-bearing; both are written up
in `CLAUDE.md` and in the code's own remarks:

- **The armed decoder gets its own independent LibVLC core.** Two `MediaPlayer`s
  sharing one core silently dropped `OnDrain`/`EndReached` under real decode
  load. Fixing that exposed a second bug — a fast handover racing `ArmAsync`'s
  own `PrepareAsync` — fixed inside `ArmAsync`.
- **Playback position comes from bytes *read* out of the shared ring**, not from
  a decoder's `BytesProduced`. A decoder that finished decoding ahead before its
  track was promoted produces no new bytes at all, so a decode-side counter
  reads as frozen at zero for that entire track.

Tested in three layers: fake-decoder unit tests (fast, no LibVLC), real-LibVLC
decode against synthetic WAV fixtures (`RequiresLibVLC`), and full-playlist
integration tests over `Avalonia.Headless` for the `Dispatcher`-driven
auto-advance path.

**What is not measured:** whether a given handover was *audibly* gapless. See
"Instrumentation" below.

## Instrumentation

The gapless pipeline logs heavily, because almost every bug in it has been a
race that only appeared under real decode load and could not be reproduced on
demand. `GaplessCoordinator.LogDiagnosticSnapshot` prints a full state line
every ten seconds while the render sink runs and warns on impossible states
(render running with no current decoder; no PCM consumed for ten seconds).
`TrackDecoder` and `MiniaudioSink` each carry a once-a-second watchdog that
stays silent while healthy.

The one question none of that answered was the only one that defines the
feature: **did the shared ring run dry across a handover?** An underrun in that
window is an audible gap between two tracks, and `MiniaudioSink`'s once-a-second
watchdog samples far too coarsely to attribute a sub-100ms dropout to the
handover that caused it. Measuring it over the whole of
`RetargetableRingWriter.PromoteTarget` does not work either: that call is paced
by playback backpressure and blocks for as long as the staged backlog lasts — up
to `DefaultStagingCapacityBytes`, a full minute — so its underrun total is
mostly the new track's, not the seam's.

So the window is bounded to what it should be: from entering `PromoteTarget` to
the instant the promoted track's first bytes land in the shared ring.
`PromoteTarget` returns a `PromotionSplice` recording that window, and
`GaplessCoordinator` logs the verdict against a baseline taken at the moment the
old decoder completed — `Debug` when the seam was clean, `Warning` with the
underrun count when it was not.

## Suggested order

1. #1 EQ bypass — **done**.
2. #6 True sample-accurate gapless — **done**, ahead of everything below it and
   in place of #4.
3. #4 Near-gapless preload — **dropped**, superseded by #6.
4. #2 Low-latency tuning — next up, but re-scope against `MiniaudioSink` first;
   the LibVLC-option tuning it describes now applies only to the decode side.
5. #3 DSD/APE — ship tagging/import any time (`Importer._validExtensions` is
   still `.mp3/.m4a/.wav/.flac/.alac`); playback is its own much larger effort,
   and the decode-outside-LibVLC option it floats is more attractive now that
   the pipeline already speaks raw PCM.
6. #5 Multi-channel/hi-res — after AirPlay/Bluetooth Phase 1 ships, and after
   the same re-scope as #2. The format is currently pinned to S16/48k by
   `GaplessFormat`, which is a deliberate gapless requirement, so hi-res
   passthrough is now in tension with #6 rather than independent of it — that
   tension is the actual design question and it is not yet answered.
