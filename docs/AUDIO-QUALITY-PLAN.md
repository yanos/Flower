# Audio quality: fix the render-path defects, then prove it with PCM-level tests

**Status: Phases 1–5 are built. Findings A–H and J are fixed; I is deferred to
`AUDIOPHILE-PLAN.md` §5, which now owns it. Phase 5 (below) is the mobile-only
one, added after device logs showed a defect none of A–J covers. What is left is
listening: the suite (1572 tests) including the real-decode tests is green, but "does it still click" is a question only ears answer — see
Verification below. Phase 6 is Phase 5's fork collapsed back into one render
path across every platform; it is not started, and deliberately waits on that
listening.**

Findings A–J below were read out of the code, not reproduced from logs; A, B, C
and D each map onto a symptom that was actually reported. They are kept in the
past tense they were written in, because the reasoning is what makes the fixes
legible; each carries a note saying what was done.

## Why this exists

Playback exhibits clicks, noise, a short fragment repeating in a loop, and
tracks that stop before they should. This is the read-through of the whole
render path (`GaplessRingBuffer` → `MiniaudioSink.DataCallback` → miniaudio),
the decode path (`TrackDecoder` → `RetargetableRingWriter` →
`GaplessCoordinator`), the control path (`PlaylistControlViewModel` →
`GaplessAudioManager`), the vendored miniaudio source, and the existing test
suite.

The architecture is already what a glitch-free player should look like: a
lock-free SPSC ring decoupling decode from render, a pull-model real-time
callback that allocates nothing, backpressure pacing decode to playback. What is
broken is a correctness bug *inside* the lock-free protocol, a lock that does
reach the real-time thread, a missing tail-drain at track boundaries, and the
complete absence of gain ramping.

Scope decisions taken up front: Phases 1–4 are in; the double-resample fidelity
problem (finding I) is deliberately deferred to `AUDIOPHILE-PLAN.md`; the
latency/declick trade is exposed as settings rather than hardcoded.

## What is actually wrong

### A. The ring replays stale audio after every flush — the "loop" symptom

`Flower/Audio/GaplessRingBuffer.cs:111-154`. `Read()` notices a generation
change, rebases **its own** index to 0, then reads `_writeIndex` raw without
checking that the writer has rebased too:

```csharp
if (generation != _readerGeneration) { _readerGeneration = generation; _readIndex = 0; }
var readIdx = _readIndex;                              // 0
var writeIdx = Volatile.Read(ref _writeIndex);         // still the PRE-reset value
var available = writeIdx - readIdx;                    // enormous
```

Until the producer's next `TryWrite`, `available` is the whole pre-flush write
count. The render callback reads the ring from offset 0 — pre-flush audio — and
because a long-played `_writeIndex` far exceeds capacity, it keeps wrapping and
**replaying the same 2 s over and over**. When the writer finally zeroes
`_writeIndex`, the reader's index is already ahead, `available` goes negative,
and playback is silent until the writer catches up — skipping that many bytes of
the new track's start.

Every flush hits this: `GaplessCoordinator.Play` (`:265`), `Stop` (`:297`), and
every seek via `TrackDecoder.OnFlush` → `RetargetableRingWriter.ResetTarget`. On
a manual track change the window is the whole media-open + decode-start latency
— hundreds of milliseconds of looped stale audio.

`AvailableBytes` (`:62-71`) already performs exactly the dual-generation check
that makes it safe. `Read()` and `TryWrite()` simply don't.

Reproducible in three lines. The existing test
`Reset_discards_buffered_data_and_lets_new_writes_start_from_empty`
(`Flower.Tests/GaplessRingBufferTests.cs:88`) masks it only because it writes
before it reads.

### B. The render callback takes a lock — at the gapless seam, of all places

`GaplessRingBuffer.Read` ends with `_progressSignal.Set()` (`:151`), on the
real-time audio thread. `ManualResetEventSlim.Set()` acquires an internal
monitor and can lazily allocate a kernel event whenever a waiter exists — and a
waiter exists on the shared ring for the whole of every track handover, because
`RetargetableRingWriter.PromoteTarget` (`:124`) calls the shared ring's blocking
`Write`, which does `_progressSignal.Reset()` / `Wait(50)` (`:210-214`).

So during every handover the audio thread contends with the promoting thread on
a monitor, at exactly the moment gapless continuity is measured. This is the
most likely direct cause of clicks at track transitions.

### C. Track tails are truncated — the "stops before it should" symptom

`EndReached` is raised at *decode* exhaustion, while up to `RingCapacityBytes`
(2 s — `GaplessAudioManager.cs:30`) of that track is still unplayed. Any path
that reaches `GaplessCoordinator.Play` with a *different* track then calls
`_sharedRing.Reset()` (`:265`) and discards it.

That happens whenever nothing was armed: the last track before a queue ends, a
track with a saved resume position (deliberately not armed —
`PlaylistControlViewModel.cs:546-547`), shuffle/repeat toggled mid-track, or a
manual Next. `GaplessCoordinator` already detects and logs this exact condition
at `:570-575`, then does nothing about it.

### D. Seek plays ~2.3 s of stale audio, then a gap

`GaplessCoordinator.Seek` (`:335-372`) sets the position baseline and calls
`_current.Seek(position)`. **Nothing resets the shared ring synchronously.** The
ring is only cleared later, when LibVLC's `OnFlush` arrives on the decode thread
(`TrackDecoder.cs:450`). Until then the callback keeps draining up to 2 s of
pre-seek audio plus ~300 ms already in the device buffer; then the generation
flips and it emits silence until the decoder refills.

### E. Nothing ramps a gain; every discontinuity is a hard cut

- Underrun handling is an abrupt drop to digital silence and back
  (`MiniaudioSink.cs:621-622`) — a click at both edges.
- `Pause`/`Stop` (`:699`, `:712`) call `ma_device_stop` mid-buffer at an
  arbitrary sample value → click. (`ma_device_stop` also blocks until the
  backend drains, and it is called from the UI thread under `_gate`.)
- `Volume` (`:243-255`) sets miniaudio's master gain, applied as a flat
  per-buffer multiply *after* our callback
  (`native/miniaudio/vendor/miniaudio.h:18944-18946`), unramped. With the
  conservative profile the period is 100 ms (`miniaudio.h:12176`), so a slider
  drag steps ten times a second — zipper noise. The scale is also raw linear
  amplitude in 101 integer steps, not dB.
- Per-track `VolumeAdjustment` (`PlaylistControlViewModel.cs:749`) lands as a
  step **exactly at the gapless seam**.
- An EQ change swaps in a fresh `Equalizer` with zeroed biquad state — a
  transient click, currently documented as accepted (`Equalizer.cs:10-13`).
- A ring `Reset()` cuts the waveform at an arbitrary amplitude.

### F. Requantisation to S16 is hard-clipped and undithered

`Equalizer.cs:70-71` does `(short)Math.Clamp(MathF.Round(l), -32768f, 32767f)` —
no headroom, so any positive band gain clips outright. Then miniaudio's own
volume stage is `(ma_int16)(sample * factor)` (`miniaudio.h:43188`) — truncation
toward zero, no rounding, no dither, audible quantisation distortion and a
truncation bias at low volume. miniaudio's converter also runs with
`ditherMode = none`.

Related: the EQ's delay lines don't advance across silence-padded regions
(`MiniaudioSink.cs:657` passes `dest[..read]`), so after an underrun the filter
resumes from pre-gap state — a filter discontinuity on top of the gap.

### G. The gapless seam is spent doing database writes

`GaplessCoordinator.HandleDrainedOrFaulted` raises `EndReached` at `:629` and
only then calls `PromoteTarget` at `:650`. The subscriber
(`PlaylistControlViewModel.cs:187-229`) runs on the **LibVLC decode-callback
thread** and does `_library.IncrementPlayCount` (a synchronous SQLite UPDATE
under `Library._lock`), `ForgetResumePosition` (possibly a second write), and a
shuffle-candidate allocation over the whole queue — all before the promoted
track's first byte can reach the ring. Only the 2 s ring covers that, and
`ReportHandoverSeam` then attributes any resulting underrun to the splice.

### H. No prebuffer before the device starts

`GaplessAudioManager.Play` (`:212-214`) calls `_coordinator.Play` then
`_sink.Resume()` immediately. The device begins pulling while LibVLC is still
opening the file, so every fresh start underruns for the media-open latency —
and today plays looped stale audio through it, per (A).

### I. Two sample-rate conversions, the second with a linear interpolator — addressed

The original S16 path used to pin the pipeline to 48 kHz, so a 44.1 kHz source
could be upsampled by LibVLC and then downsampled again by miniaudio's default
linear resampler. The bounded fix now negotiates `GaplessFormat.SampleRate`
from the first opened output device before either decoder is constructed. A
44.1 kHz source on a 44.1 kHz endpoint consequently has only the decoder's one
conversion — `swresample`'s now, LibVLC's when this was written. The negotiated session rate remains fixed when the output device
changes, so an active or armed decoder never changes timing mid-track.

### J. Smaller, worth fixing while in there

- `long` ring indices are read with `Volatile.Read`, which is **not atomic on
  32-bit runtimes** — `Flower.Android` still ships an `armeabi-v7a`
  `libminiaudio.so`. A torn index read yields a garbage `available`.
- `DataCallback` has **no exception guard** around an `UnmanagedCallersOnly`
  boundary — anything thrown aborts the process.
- ~9 unconditional interlocked RMWs plus a `Fingerprint` pass per callback,
  purely for diagnostics, with no debug gate.
- `PlaylistControlViewModel`'s `Stopped` handler (`:164-170`) raises
  `PropertyChanged` off the UI thread when the stop comes from
  `MiniaudioSink.HandleUnexpectedStop`, and `NowPlayingIntegrationService.cs:62-77`
  invokes transport commands on the OS media-key thread, both unmarshalled.
- `Time`/`Length`/`CurrentTrack` take `GaplessCoordinator._gate` (`:177`, `:186`),
  a lock also held across the verbose logging in `LogDiagnosticSnapshot`
  (`:197-238`) and the whole locked section of `HandleDrainedOrFaulted`
  (`:535-624`), so the 250 ms UI poll stalls at every transition.

### What does *not* need changing

The usual .NET-audio advice mostly describes what this codebase already does, so
it is recorded here to stop it being re-proposed:

- **Double-buffering / pull model / `ISampleProvider`** — `GaplessRingBuffer` is
  the circular buffer and miniaudio's `ma_device` data callback is the pull
  model. Already correct.
- **A float internal format** — worth doing *inside the render callback only*
  (Phase 2). The ring should stay integer PCM. The reasoning at the time was
  that LibVLC 3.0's `amem` hardcoded S16N and never honoured a requested
  fourcc, so a float ring would store an exact widening of S16 at twice the
  memory for zero benefit. **The premise is gone and the conclusion survives
  it:** the ring now carries packed S24 because that is what
  `FfmpegTrackDecoder` delivers, and `PcmSampleFormat` stops at S24 on purpose
  — `OutputStage` works in float, whose mantissa is exactly 24 bits, so
  widening the ring further would be a widening that quietly narrows again.
- **`ArrayPool` / GC-pause avoidance** — the RT callback already allocates
  nothing, and `TrackDecoder.OnPlay`'s `_scratch` (`:396-399`) is grow-only and
  reused. The useful artifact is a regression test asserting zero allocation,
  not a rewrite.
- **Lock-free synchronisation** — correct in shape. The problems are (A), a bug
  *in* the protocol, and (B), a lock that reaches the RT thread anyway.

## What shipped, finding by finding

| # | Fix | Where |
|---|---|---|
| A | `Read()`/`TryWrite()` now treat a counterpart that has not rebased into the current generation as empty, and every rebase publishes its index *before* the generation that acknowledges it. Indices moved to `Interlocked` for 32-bit atomicity. | `GaplessRingBuffer.cs` |
| B | `Read()` no longer signals anything. The progress event is writer→reader only; a writer under backpressure polls at 1ms instead. | `GaplessRingBuffer.cs` |
| C | `Play(track, immediate)`. An auto-advance with the outgoing decoder already drained keeps the buffered tail and appends after it, splitting elapsed time at the ring's write cursor exactly as a promotion does. A manual skip still flushes — behind a fade. | `GaplessCoordinator.cs`, `IAudioManager`, `PlaylistControlViewModel` |
| D | `Seek` resets the shared ring synchronously instead of waiting for LibVLC's `OnFlush`. | `GaplessCoordinator.cs` |
| E | `OutputStage` ramps every gain (volume, per-track offset, EQ preamp) over `GainRampMs`, fades in over `DeclickFadeMs` after any flush, and fades out over `TransportFadeMs` before `ma_device_stop`. The volume scale is a cubic perceptual taper, not raw linear percent. | `OutputStage.cs`, `MiniaudioSink.cs` |
| F | One requantisation, at the end, with TPDF dither — and only for samples that are not already exact integers, so a unity-gain pass is bit-identical. miniaudio's master volume is pinned at 1.0 so its truncating integer path is never used. The EQ is float in/out and no longer clamps mid-chain, and runs over the silence padding so its delay lines stay continuous. | `OutputStage.cs`, `Equalizer.cs`, `MiniaudioSink.cs` |
| G | `PrimeTarget` — a non-blocking half of the handover that fills whatever room the shared ring has before `EndReached`'s subscribers get the decode thread. The play-count and resume-position writes then go to the pool rather than running on it. | `RetargetableRingWriter.cs`, `GaplessCoordinator.cs`, `PlaylistControlViewModel.cs` |
| H | A prime latch in the render callback: after a flush it renders silence, uncounted as an underrun, until the ring holds `PrebufferMs` or a 1.5s deadline passes. | `MiniaudioSink.cs` |
| I | **Deferred**, by decision — see "Out of scope" below. `AUDIOPHILE-PLAN.md` §5 now owns it. | — |
| J | Atomic 64-bit index access; a `try/catch` around the whole `UnmanagedCallersOnly` boundary; `CurrentTrack`/`CurrentTrackBytesProduced` read from lock-free published fields; diagnostics formatted outside `_gate`; the `Stopped` handler and `NowPlayingIntegrationService`'s command handler marshalled onto the UI thread. | several |

Not done as originally sketched: the soft-knee limiter. It was built, and removed
again — the source is already S16, so a unity-gain pass with no EQ has to come
back out bit-identical, and any knee that starts below full scale attenuates real
signal to buy headroom nothing needs. (A knee that starts *at* full scale is
arithmetically a clamp; a monotone map that is the identity on [-1,1] and bounded
by 1 has nowhere else to go.) The clamp stayed, and the real fix is that no stage
before it can clip any more.

Also not done as sketched: moving `PromoteTarget` wholesale above `EndReached`.
Its blocking drain is paced by real-time playback over as much as a minute of
backlog, so the now-playing UI would have waited that long. The split into
`PrimeTarget` + `PromoteTarget` gets the seam closed without that.

## Plan (delivered)

### Phase 1 — the defects behind the reported symptoms

**1.1 Fix the ring's post-`Reset()` race** — `Flower/Audio/GaplessRingBuffer.cs`.

Keep the generation design; close the hole by making each side treat a
counterpart that has not yet rebased as *empty*, and publish the rebased index
**before** the acknowledgement:

```csharp
// Read(): the writer has not rebased yet -> nothing in this epoch is readable.
if (Volatile.Read(ref _writerGeneration) != generation) return 0;

// TryWrite(): mirror image, conservative.
var readIdx = Volatile.Read(ref _readerGeneration) == generation
    ? Volatile.Read(ref _readIndex) : 0;

// Both rebase paths publish the index first, then acknowledge:
Volatile.Write(ref _writeIndex, 0);
Volatile.Write(ref _writerGeneration, generation);
```

Same check `AvailableBytes` already makes; release/acquire ordering makes it
airtight. `TotalBytesRead`/`TotalBytesWritten` semantics are untouched, so
`GaplessCoordinator._currentTrackReadSplit` needs no change. While here, switch
the index reads/writes to `Interlocked.Read`/`Interlocked.Exchange` so
`armeabi-v7a` is safe (J).

**1.2 Get the lock off the real-time thread** — same file.

Delete `_progressSignal.Set()` from `Read()`. Writers stop waiting on an event
the reader owns and instead poll (`Thread.Sleep(1)` inside their existing
timeout loops) — they are decode/promotion threads where a 1 ms poll costs
nothing. `Write`, `ReadBlocking` and `RetargetableRingWriter.PromoteTarget` all
keep their current external behaviour and timeouts.

**1.3 Stop discarding the buffered tail** — `Flower/Audio/GaplessCoordinator.cs`.

Split "the user skipped" from "the queue advanced": add an `immediate` parameter
to `Play`, threaded through `IAudioManager.Play`.

- **Auto-advance (`immediate: false`)** — when the outgoing decoder has already
  drained and the shared ring still holds its audio, wait for `AvailableBytes`
  to reach zero (bounded by a ~2.5 s deadline, off `_gate`) before `Reset()`.
- **Manual skip (`immediate: true`)** — flush at once, preceded by the short
  fade-out from Phase 2 so the cut is inaudible.

Call sites: `PlaylistControlViewModel.cs:225` (auto-advance) and `:247`
(skip-on-failure) pass `false`; `Next`/`Previous`/row activation pass `true`.

**1.4 Flush the ring synchronously on seek** — `GaplessCoordinator.Seek` calls
`_sharedRing.Reset()` itself instead of waiting for LibVLC's asynchronous
`OnFlush`. The later `OnFlush` reset becomes a harmless second generation bump.

**1.5 Close the seam before running subscribers** — in
`HandleDrainedOrFaulted`, move the `PromoteTarget` call (`:650`) *above* the
`EndReached`/`TrackFailed` invocation (`:626-629`). Then in
`PlaylistControlViewModel`'s `EndReached` handler (`:187-229`) keep only the
queue decision inline and move `IncrementPlayCount` / `ForgetResumePosition` off
the decode thread.

### Phase 2 — a real output stage: float, ramps, dither

New `Flower/Audio/OutputStage.cs`, owned by `MiniaudioSink` and driven from
`DataCallback`. Per callback, on the bytes read from the ring:

1. Widen S16 → float into a preallocated scratch buffer (allocated in `Start`,
   grow-only, never grows in steady state).
2. Run the EQ in float, **including the silence-padded tail**, so the delay lines
   stay continuous (fixes the (F) discontinuity).
3. Apply gain per sample with a linear ramp from `_currentGain` toward
   `_targetGain` over `GainRampMs` — covering user volume, per-track
   `VolumeOffset` and the EQ preamp in one place (fixes E). Map the slider
   through a perceptual (dB) curve rather than raw linear percent.
4. Apply the declick envelope: fade in over `DeclickFadeMs` when the ring's
   `Generation` changed since the last callback (covers seek and skip), fade out
   over `TransportFadeMs` on request.
5. Round with TPDF dither and clamp back to S16.

`MiniaudioSink` then pins miniaudio's master volume at 1.0 so its truncating
integer path is never used; `Volume`/`ApplyEqualizer` become target updates.
`Pause`/`Stop`/`CloseDevice` set a fade-to-silence target and wait on a
`ManualResetEventSlim` the callback sets — bounded by `FadeOutWaitMs` — before
`ma_device_stop`. Wrap the whole callback body in a `try/catch` that logs
nothing and clears the buffer (J).

Also give `Equalizer` headroom and a soft clip instead of the bare clamp, and
crossfade a coefficient swap over one callback instead of resetting state.

Every timing constant named above comes from settings, not source — see Phase 3.

### Phase 3 — prebuffer, tunable timings, and the UI off the audio lock

- **Prime latch in `MiniaudioSink`**: after a ring generation change, render
  silence (not counted as an underrun) until either `AvailableBytes` reaches the
  prebuffer threshold or a deadline passes; then latch to normal and fade in.
  Removes the start-of-track and post-seek glitch (H) and keeps every caller
  synchronous.
- **`AudioTimingSettings` on `AppSettings`** (`Flower/Persistence/AppSettingsStore.cs:33`,
  alongside the existing `EqualizerSettings`), so the quality/snappiness trade
  can be retuned by editing `settings.json` without a rebuild. Fields and
  quality-first defaults: `PrebufferMs = 200`, `TransportFadeMs = 15`
  (pause/stop/skip), `DeclickFadeMs = 8` (post-flush fade-in), `GainRampMs = 20`
  (volume, per-track offset, EQ preamp), `FadeOutWaitMs = 30` (the bounded wait
  before `ma_device_stop`). Every value clamped to a sane range on load.
  Delivered to the sink the way the EQ already is — an
  `ApplyTiming(AudioTimingSettings)` method publishing an immutable snapshot to a
  `volatile` field the callback reads — so it takes effect live. No
  Settings-window UI this round; the file is the surface.
- **`GaplessCoordinator`**: publish `CurrentTrack`, `Length` and
  `_currentTrackReadSplit` into volatile fields so `Time`/`Position` never take
  `_gate`; build log values under the lock and log outside it (J).
- Marshal the `Stopped` handler and `NowPlayingIntegrationService`'s command
  handler onto the UI thread (J).

### Phase 4 — the test suite

Audio quality is the top priority for this app, and today nothing verifies it at
the sample level: `FakeAudioSink` uses `ReadBlocking`, so starvation and stale
data are invisible to every existing test, and `SyntheticWav.Ramp()` — the
generator built for continuity checking — has no audio caller at all.

**New shared harness** `Flower.Tests/TestSupport/Pcm.cs` — analysis and
assertions over S16 stereo spans:

- `AssertBitExact(expected, actual)` reporting the first differing frame.
- `MaxStep` / `AssertContinuous(bytes, maxStep)` — a click *is* a step
  discontinuity, so this is the direct detector.
- `LongestSilentRun`, `Rms`, `PeakDb`.
- `AssertRampSequence(bytes, startFrame)` — validates a `SyntheticWav.Ramp()`
  stream frame-by-frame, catching dropped or duplicated blocks.
- `Thd(bytes, fundamentalHz)` via Goertzel — for EQ and gain-stage quality.

**New faithful render fake** `TestSupport/RenderPumpSink` — pulls fixed-size
periods with `Read()` (not `ReadBlocking`), silence-pads short reads exactly as
`MiniaudioSink.DataCallback` does, and records per-period short reads. Keep
`FakeAudioSink` for the existing tests; add this alongside it.

**Unit tests**

- `GaplessRingBufferTests`: reader-first-after-`Reset()` returns 0 (**fails
  today**); a post-reset read yields only post-reset bytes; a concurrent
  SPSC + `Reset()` fuzz over a `Ramp()` stream asserting the output is always a
  gap-free subsequence with no repeats; `Read()` performs no blocking wait.
- `EqualizerTests`: analytic magnitude response per band within 0.5 dB; THD on a
  full-scale sine at 0 dB gain at the quantisation floor; no clipping at +12 dB
  with headroom; **delay-line continuity across successive `ProcessInPlace` calls
  of different lengths** — a per-call state reset passes every test that exists
  today.
- New `OutputStageTests`: a step gain change never exceeds a per-sample delta
  threshold; a fade-out ends at exactly zero; dither bounded to ±1 LSB and
  removing the truncation bias; zero managed allocation per callback (via
  `GC.GetAllocatedBytesForCurrentThread`).

**Integration tests** (`FakeTrackDecoder` + real coordinator + `RenderPumpSink`,
no native decoder, CI-runnable)

- Manual `Play(B)` while A still has buffered PCM: every byte A produced appears
  before B's first byte (proves 1.3).
- Seek mid-track: no captured frame after the flush carries a pre-flush value
  (proves 1.1 and 1.4).
- Deliberate starvation: the captured stream contains silence but never repeated
  or stale content.

**Real-decode tests** (`RequiresLibVLC` then, `RequiresFfmpeg` now — and no longer excluded from CI, which builds the façade on all three desktops and requires it via `FLOWER_REQUIRE_DECODERS`)

- **Bit-exactness**: decode a `SyntheticWav` file end to end and assert the
  captured PCM is byte-identical to `SyntheticWav.Build(...)`'s data chunk, modulo
  a bounded lead-in/tail. This is the known-perfect-PCM oracle;
  `SyntheticWav`'s header comment (`:13-15`) already says the fixtures were
  designed for it, and nothing currently does it.
- **Frame-count exactness at a handover**: `firstBFrame == frames(A)` exactly.
  `GaplessCoordinatorRealDecodeTests.cs:113-128` only checks that B eventually
  appears and never reverts, so a 200 ms hole inside A's marker region passes today.
- **Seam continuity**: `AssertContinuous` across the splice with `Ramp()` fixtures
  on both tracks.
- **Seek**: lands within one frame of the reported `SeekSettled` offset, with no
  discontinuity beyond the fade-in envelope.

### Out of scope this round

**Hi-res/direct output.** Written when the fixed session-rate path received S16
PCM from LibVLC's `amem` callback and so could not preserve a 24-bit source.
**That half is since built**: the FFmpeg façade shipped, the pipeline carries
packed S24 end to end, and `MiniaudioSink` narrows back only if the device
refuses `ma_format_s24`. What remains out of scope here is the rest of it — a
per-track native format rather than one negotiated at startup. The
decoder/backend spike is recorded in `AUDIOPHILE-PLAN.md` §5: a narrow native
FFmpeg façade was the selected route, with direct mode choosing a
track's native format only when the device accepts it. That is a separate
format-aware pipeline, not an extension of this quality pass.

## Phase 5 — the render thread itself (mobile only)

Added after Phases 1–4 shipped, from a real iPhone's pushed logs during one
playthrough of *Generique*. Nothing in A–J explains what they show, and the
symptom — occasional blips — survived all of it.

**What the logs say.** For the whole track the ring stayed roughly 1.3s ahead,
`ShortReads=0 Underruns=0 SilenceBytes=0`, and the native continuity check on
what was handed to CoreAudio found `AbruptFrames=0 RepeatedBuffers=0`. The
render callback never took more than ~3ms. But the callback itself arrived late
six times in two minutes — 66, 70, 84, 159, 189 and once **668** ms against a
42.7ms period — and the window after the worst one reported
`MaxHostTimeGapMs=442`: CoreAudio's own timestamps skipped between two
consecutive 1024-frame buffers, so the hardware played ~420ms of nothing.

Two things say it is a whole-process stall rather than an audio-thread problem.
The watchdog timer was late in the same window (1.27s after the previous tick on
a 1s schedule, then catching up), and there was nothing else running — 125 log
lines in the entire two minutes, none near any spike. On Mono, a few hundred
milliseconds of the whole process stopping with no work to explain it is a GC
stop-the-world pause; every managed thread is suspended at a safepoint, and the
miniaudio data callback *was* managed code. macOS, same code on CoreCLR, has not
recorded a single late callback.

`performance_profile_conservative` was the earlier answer to this and is not
enough: 42.7ms of period slack does not cover a 626ms pause, and raising the
period does not help on iOS, where the real IO buffer rises with it.

**The fix.** A thread that never enters managed code is never suspended, so the
render callback became pure C. `native/miniaudio/flower_audio_bridge.h` is a
single-producer/single-consumer PCM buffer with a `ma_device_data_proc` that
does a memcpy, a fade and some counters, and nothing else.
`Flower/Audio/AudioFeeder.cs` is the ordinary managed thread that fills it —
prime latch, EQ, gain ramp, dither, everything the callback used to do, now
running `NativeBufferMs` ahead of the speaker. A GC pause that suspends the
feeder is then a pause in refilling a buffer deep enough to play through it.

Details worth keeping:

- **The flush handshake.** A seek or a skip has to drop what is already
  buffered, and "drop everything queued" cannot tell pre-flush audio from
  post-flush audio written a microsecond later. So the producer requests, the
  callback acknowledges by dropping, and the producer writes nothing in
  between — with a 120ms timeout after which it applies the flush itself, for
  the case where the device stopped and no callback will ever run again.
  Indices are monotonic and never rebased, so there is no epoch to get wrong.
- **The transport envelope moved into C.** A fade applied on the producer side
  would not reach the speaker for a bridge-depth, so pause would keep playing.
  The callback owns it, and pause/resume stay immediate. The cost is that the
  fade multiplies already-dithered S16 — quantisation error during a ramp to
  silence, which is where it cannot be heard.
- **Position.** `IAudioSink.BufferedBytes` reports what the sink has taken but
  not played, and `GaplessAudioManager.Time` subtracts it; otherwise the seek
  bar runs a bridge-depth ahead of the music.
- **Where it applies.** Only where Flower builds its own miniaudio — Android and
  iOS. Desktop's binary is the `Miniaudio-CS` NuGet and carries none of these
  symbols, so `NativeAudioBridge.IsAvailable` is false there and the managed
  callback stays. That is probed rather than switched on
  `OperatingSystem.IsIOS()`, so the decision follows the binary that actually
  loaded.

**The trade, stated plainly.** `NativeBufferMs` defaults to 300. That is how
long a stall has to be before it is audible, and equally how long a volume or EQ
change takes to reach the speaker, because both are applied on the feeder. The
iPhone's own numbers pick the value: across a full day, seven stalls over 100ms
and exactly one over 250ms.

Untested by the suite, and knowingly so: the C itself. `AudioFeederTests` covers
every byte-conservation and flush-ordering rule against `FakeAudioBridge`, which
keeps the same refuse-writes-until-acknowledged contract, but no test on a
desktop can exercise a callback that only exists in an iOS/Android binary.

### K. Decode-ahead never armed for a streamed track — fixed

Found while reading a phone's pushed log for something else: 32 tracks played,
32 `Decode-ahead prepare failed` warnings, zero successful arms. Not a server
problem — the same session streamed all 32 tracks fine.

`TrackDecoder.PrepareAsync` parsed with `MediaParseOptions.ParseLocal`, which
LibVLC documents as "parse media if it's a *local* file". A track streamed from
a server was therefore skipped without a single network request, came back
not-`Done`, and `GaplessCoordinator` read that as a failed prepare and cleared
the armed slot. So decode-ahead was off for all streamed playback, always, and
every handover that gapless exists to cover was an ordinary gap. Fixed by
parsing with `ParseNetwork`, which covers local media too ("parse media *even
if* it's a network file"), so there is one path rather than a branch.

It hid for this long because every decode fixture was a file on disk.
`TestSupport/LocalFileServer` serves one over loopback HTTP so a test can hand
LibVLC a real URL; against the old code the new test fails with exactly
`NotAttempted`.

The second half was that the failure could not be diagnosed from the log at
all. `PrepareAsync` returned a bool, so "the server stopped answering", "the
track is broken" and "we never tried" were one indistinguishable warning with
no cause attached. It now returns `DecodePrepareResult`
(`Ready`/`NotAttempted`/`TimedOut`/`Failed`/`Retired`) and the warning names the
reason — `TimedOut` being the only one of them that says anything about the
network. A decoder retired underneath a prepare is an ordinary skip and drops
to Trace.

## Phase 6 — the same path everywhere (not started)

Phase 5 left a fork: pure-C render callback on Android and iOS, managed
callback on desktop. That was scoped by evidence — a full day of macOS client
logs contains no late render callback at all, against seven on the phone, and
CoreCLR does not suspend a thread the way Mono does — but the fork itself has a
cost the evidence does not weigh. Two render paths means the one covered by
`AudioFeederTests` is not the one desktop ships, and `MiniaudioSink` carries a
`_bridge is { } bridge / else` branch through `Resume`, `Pause`,
`FadeOutAndWait` and the watchdog. Collapsing to one path deletes the managed
`DataCallback` and its fingerprint helper outright, along with every branch.

**The bridge does not depend on miniaudio.** This is the fact that makes it
affordable, and it was checked rather than assumed: the bridge takes
`ma_device*` purely as an opaque key for its device registry and never
dereferences it, and `ma_uint32` is `uint32_t`. Extracted into a standalone
file and compiled against nothing but its own header, it builds clean, exports
all 17 `flower_audio_bridge_*` symbols, and links against `libSystem` alone.

So desktop does *not* mean rebuilding miniaudio from source and dropping the
`Miniaudio-CS` NuGet, which is what made this look expensive. Split the bridge
out of `impl.c` into its own `flower_audio_bridge.c`, ship it as a small
standalone `libflowerbridge` alongside the NuGet's `libminiaudio`, and hand
miniaudio a `dataCallback` pointer that lives in a different library — it has no
opinion about that. Mobile keeps compiling the same source into its existing
single library, so there is no second variant to hold in sync.

What it takes:

- Split `flower_audio_bridge.c` out of `impl.c`; both mobile builds pick it up
  as a second translation unit.
- A desktop build script producing six RIDs. `osx-arm64` and `osx-x64` both
  build on a Mac today (the x64 cross was verified). `linux-x64`/`linux-arm64`
  need a container. `win-x64`/`win-arm64` are the real snag: MSVC only supports
  C11 `stdatomic` on VS 17.5+ behind `/experimental:c11atomics`, so it is clang
  or a recent-VS floor, and neither Linux nor Windows can be produced from a
  Mac without extra tooling. This is the whole cost of the phase, and it is
  build infrastructure rather than code.
- `NativeAudioBridge`'s `DllImport("miniaudio")` becomes
  `DllImport("flowerbridge")`. The `IsAvailable` probe needs no change and
  keeps its value: a RID nobody built for degrades to the managed callback
  instead of failing to start — which is also what makes this landable one
  platform at a time.
- Delete the managed `DataCallback` once every shipped RID has a binary.

**`NativeBufferMs` should not stay at 300 on desktop.** That number is sized to
survive a Mono GC pause and buys nothing on CoreCLR, while costing 300ms of lag
on the volume slider and every EQ change, because both are applied on the
feeder. Desktop wants something like 60–80ms: the same architecture without the
insurance premium.

**Sequencing.** After mobile listening testing settles, not before. If more
testing turns up a bridge bug it is worth fixing in one place rather than after
the bridge is the only path on six more RIDs — and unlike the phone, desktop
has no defect waiting on this. `Flower.Web` is out of scope permanently.

## Files

| File | Change |
|---|---|
| `Flower/Audio/GaplessRingBuffer.cs` | 1.1 generation guard + atomic indices, 1.2 no signal from `Read` |
| `Flower/Audio/RetargetableRingWriter.cs` | 1.2 polling instead of event waits |
| `Flower/Audio/GaplessCoordinator.cs` | 1.3 tail drain, 1.4 sync seek flush, 1.5 reorder, Phase 3 lock-free reads |
| `Flower/Audio/GaplessAudioManager.cs`, `IAudioManager.cs` | `Play(track, immediate)` |
| `Flower/ViewModels/PlaylistControlViewModel.cs` | pass `immediate`; DB writes off the decode thread; marshal `Stopped` |
| `Flower/Audio/OutputStage.cs` (new) | float path, ramps, declick, dither |
| `Flower/Audio/MiniaudioSink.cs` | own the output stage; master volume 1.0; fade-then-stop; prime latch; callback exception guard |
| `Flower/Audio/Equalizer.cs` | float in/out, headroom, soft clip, coefficient crossfade |
| `Flower/Persistence/AppSettingsStore.cs` | `AudioTimingSettings` on `AppSettings`, clamped on load |
| `Flower/App.axaml.cs` | apply the timing snapshot to the sink at startup, as the EQ already is (`:587-588`) |
| `Flower.Tests/TestSupport/Pcm.cs`, `RenderPumpSink.cs` (new) | shared PCM harness |
| `Flower.Tests/GaplessRingBufferTests.cs`, `EqualizerTests.cs`, `GaplessCoordinatorRealDecodeTests.cs` | new assertions |
| `Flower.Tests/OutputStageTests.cs`, `RenderStarvationTests.cs` (new) | Phase 2/4 coverage |
| `docs/AUDIOPHILE-PLAN.md` | item #1's "accepted click" note is superseded; hand it the double-resample item |

## Verification

Automated, and passing:

- `dotnet test Flower.Tests/Flower.Tests.csproj --filter Category!=RequiresLibVLC`
  — 1540 tests. (That category is `RequiresFfmpeg` now; the run below is the
  one that matters and is unchanged in spirit.) The new ones were each checked against the pre-fix code: the
  three `GaplessRingBufferTests` epoch tests and both
  `RenderStarvationTests` tail/seek tests fail without their fix and pass with
  it.
- `dotnet test Flower.Tests/Flower.Tests.csproj` — plus the six
  `GaplessCoordinatorRealDecodeTests`, including the two new ones that matter
  most: a decoded track is byte-identical to the file it came from, and a
  handover puts B's first frame at exactly the frame A's last one ended on.

New test surface: `Flower.Tests/TestSupport/Pcm.cs` (bit-exactness, step
discontinuity, silent runs, RMS/peak, ramp sequence, Goertzel THD) and
`TestSupport/RenderPumpSink.cs` (a sink that behaves like the real render
callback — non-blocking `Read()`, silence padding, short-period counting —
rather than `FakeAudioSink`'s `ReadBlocking`, which is why none of these
failures were visible before). `OutputStageTests`, `RenderStarvationTests` and a
rewritten `EqualizerTests` build on them.

Phase 5 adds `AudioFeederTests` (11 tests) and `TestSupport/FakeAudioBridge.cs`.
The native callback it feeds has no automated coverage — see that phase's last
paragraph.

Still to do, and only ears can do it:

1. `dotnet build Flower.Desktop/Flower.Desktop.csproj` and listen: seek
   repeatedly mid-track, press Next hard, let an album auto-advance, play the
   last track of a queue to its end, drag the volume slider during playback,
   toggle EQ bands while playing. None of these should click, loop a fragment,
   or cut the end of a song.
2. Check the volume taper feels right. It is a real change: the slider used to
   be raw linear amplitude, so 50% was -6dB; it is now cubic, so 50% is about
   -18dB. That is the point — linear spent most of the travel in a range the ear
   barely separates — but it will feel different, and the curve is one line in
   `OutputStage.GainForVolumePercent` if it wants adjusting.
3. Set `AudioTiming.PrebufferMs` to 50 and `TransportFadeMs` to 5 in
   `settings.json`, relaunch, and confirm the snappier profile still behaves —
   that the knobs are real. Note the default 200ms prebuffer is added latency on
   a manual skip, deliberately: it is what replaces the starved trickle that
   used to play there.
4. Watch the log at Debug for `Render watchdog`, `Handover was not gapless` and
   `no armed successor` warnings — they should stop appearing.
