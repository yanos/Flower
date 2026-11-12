# Audio quality: fix the render path, prove it with PCM-level tests, build the audiophile features

One plan for how Flower sounds. It used to be two, and is now two parts. The
first is the render path: the defects behind clicks, loops and truncated
tracks, the phases that fixed them, and the PCM-level tests that hold them
fixed (Phases 1–6, findings A–O). The second, "Audiophile features" at the end,
is the five features asked for as audiophile playback (§1–§6), most of whose
narrative is the move from LibVLC to Flower's own FFmpeg decoder. It was
`AUDIOPHILE-PLAN.md` until November 2026.

**Status.** Phases 1–5 are built, and the listening pass they gated on is done —
no clicks on the desktop, and sustained everyday listening on a real iPhone with
no blips. Findings A–H and J are fixed; I is deferred to feature §5, which now
owns it. Phase 5 (below) is the mobile-only one, added after
device logs showed a defect none of A–J covers, and the phone listening is what
confirms its native render callback survives a GC pause, which no automated test
can reach. (Its arithmetic is now checked automatically, on the phone runners:
Phase 6 step 4.)

Of the features, §1 (EQ), §3 (Monkey's Audio and DSD) and §6 (true
sample-accurate gapless) are done, and §4 was superseded by §6 and will never
be built. §5 is largely done: the FFmpeg
façade shipped on all five heads, and the pipeline carries float32 from the
decoder to the output stage, which requantises once to the device's S24, or
S16 if the device refuses it. §2 needs re-scoping before anything is built.

Phase 6's first seven steps are built (November 2026): the `armeabi-v7a` atomics
question is settled, the pipeline carries float from the decoder to
`OutputStage`, the output stage runs in the C render callback (the fix for M),
that callback has been heard on an iPhone and is held to `OutputStage` byte for
byte by `NativeOutputStageChecks`, and the bridge is `libflowerbridge`, a
library of its own on every platform, built for the six desktop RIDs and run
by desktop too, where it has now been heard on macOS, Linux and Windows
with nothing wrong (November 2026). What is left is step 8: moving the
stage's property tests onto the C and deleting the managed callback and
`OutputStage` with it. The phase was rescoped
while writing an earlier status down. It used to read as "collapse Phase 5's desktop/mobile fork"; working out what
that fork costs turned up **finding M — every interactive control on mobile
lags by the render buffer's depth, today, because Phase 5 put that buffer on
the far side of the output stage.** The fork is a symptom of an ordering
mistake, so Phase 6 is now: move the output stage into the render callback,
carry float end to end, and let the desktop/mobile collapse fall out of that
rather than be the goal. M is real but mild in practice (the phone's hardware
volume buttons bypass it), so none of this is urgent — see Phase 6's own
sequencing.

Findings A–J below were read out of the code, not reproduced from logs; A, B, C
and D each map onto a symptom that was actually reported. They are kept in the
past tense they were written in, because the reasoning is what makes the fixes
legible; each carries a note saying what was done.

## What is left, in order

1. **Listen on a real Android phone** (§5, "Where the table stands now").
   Android is the one platform nobody has heard on the current decoder. Its
   emulator decoding runner is back in CI, now that both Android heads keep
   the host FFAudio package's native assets out (`XA4301`, Tests/README.md,
   "Android").
2. **Retire the C# output stage** (Phase 6, step 8). Move the property tests
   onto the C, delete the managed callback, `OutputStage` and the byte for
   byte comparison, and decide whether the callback timing diagnostics are
   ported into the bridge. Desktop has been heard through the bridge on all
   three platforms, which is what this waited on.
3. **Low latency** (§2). Measure `AudioTimingSettings`, `AudioFeeder` and
   `MiniaudioSink`'s period sizing before changing anything; there may be
   nothing to win, and a shorter period spends the bridge's headroom (Phase
   6, "What it costs, honestly").
4. **The rest of §5: direct mode and multichannel.** A per-track native
   format rather than one negotiated once at startup, which needs device
   capability probing the current name/id-only device list cannot do, plus
   the controlled transitions that come with changing format mid-session.
   After `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's output picker.

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
problem (finding I) is deliberately deferred to feature §5; the
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
  memory for zero benefit. **Reversed in Phase 6:** with the decoder able to
  deliver float natively - most codecs decode to it - an integer ring was a
  round trip through integer in front of the EQ, and its format had to be
  negotiated and frozen. The ring carries float now. 24 bits is still the
  ceiling, as `DeviceSampleFormat`'s comment explains.
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
| I | **Deferred**, by decision — see "Out of scope" below. Feature §5 now owns it. | — |
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
float32 from the decoder to `OutputStage`, and the stage requantises once to
the device's S24, or to S16 only if the device refuses `ma_format_s24`. What remains out of scope here is the rest of it — a
per-track native format rather than one negotiated at startup. The
decoder/backend spike is recorded in feature §5: a narrow native
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
- **Position.** `IAudioSink.BufferedFrames` (bytes, until Phase 6 put the ring
  and the bridge in different formats) reports what the sink has taken but
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

**Since then, partly tested after all**, on the phones rather than a desktop:
Phase 6 step 4's `NativeOutputStageChecks` calls the compiled callback directly
and compares every byte it renders against `OutputStage`'s, through flushes and
rebuffers as well as the controls. It runs single-threaded, so the bridge's
flush and prime protocol under a real concurrent feeder is still covered only by
`FakeAudioBridge`'s copy of its contract, and by listening.

What stands in for that test is use. The bridge has since had sustained everyday
listening on a real iPhone with no blips — which is the right shape of evidence
for the defect it fixes, since a GC stop-the-world pause is intermittent and
shows up over hours rather than in a run. It is not equivalent to a test, and
Phase 6 is partly about not needing it to be: once one render path ships
everywhere, `AudioFeederTests` covers the path desktop actually runs, and the C
stops being the only implementation of its own contract.

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

### L. A track that finished decoding could not be seeked — fixed

Found by a CI failure that looked like slowness and was not.
`StreamedTrackDecodeTests.A_streamed_track_can_be_seeked_mid_decode` timed out
on a contended runner; the reason it timed out is that the seek was never
applied, and on a fast idle machine the test simply won the race.

`FfmpegTrackDecoder`'s decode loop returned the moment `Read` came back empty.
`Seek` posts to that loop — it stores the request in `_pendingSeekMs` and lets
the decode thread do the work, because one FFmpeg decoder belongs to one
thread. So a seek arriving after the loop had returned was accepted, stored,
and read by nobody: `SeekSettled` never fired, the scrubber jumped to where the
listener dropped it, and the audio carried on regardless.

The window this happens in is not small. Draining is not ending —
`GaplessCoordinator` keeps a drained decoder alive while its tail plays out,
which is what `CheckWatchdog`'s closing remark is about. Any track that fits
its ring decodes in milliseconds and then sits fully decoded for its entire
audible length, so for short tracks the window is *the whole track* and every
scrub of one was silently dropped.

Fixed by parking rather than returning: `ParkUntilSeekOrRetire` reports
`Drained` first and unconditionally (the coordinator promotes the next track on
it, so parking must not delay it), then blocks on a handle that both `Seek` and
`Retire` set. `Retire` joins with a five-second budget, which is why this is a
wait handle and not a poll. The watchdog needed to learn the state too: a
parked decoder produces no bytes and waits on no room, which is exactly the
signature `IsStalled` looks for, so it would have logged a wedged decode once a
second for the length of every tail.

`FfmpegTrackDecoderTests.Seeking_after_the_track_has_fully_decoded_still_lands`
pins it, with a ring deliberately larger than the fixture so the decode
finishes with nobody reading a byte — the same shape as decode-ahead having
filled the ring long before the track is over. Against the old code it fails
with a timeout.

### N. Next onto the decode-ahead track threw it away — fixed

From a phone streaming over the public address on 2026-11-04. "To Forget" slowed
to a trickle, the listener pressed Next, and "Junior" — armed, with a full 60 s
staged while the link was still healthy — did not play. `GaplessCoordinator.Play`
treated every Play that was not a no-op as a fresh start: it retired the armed
decoder and opened the same track again over the link that had just failed.

`Play` now promotes the armed decoder when it is the track asked for
(`PromoteArmedNow`): flush, splice its staged audio in, keep its connection.
`_generation` is left alone so a prepare still in flight starts it, and an armed
decoder that already drained has its end reported, as on a natural handover.
`GaplessCoordinatorTests` covers all three.

### O. A stream slower than real time played as blips — fixed

The other half of the same incident. The prime latch closed only after a flush,
so a stream delivering about a third of real time played each scrap the moment
it landed: a blip every second or two. `PrimeLatch` (shared by `MiniaudioSink`'s
managed callback and `AudioFeeder` on the phones) now closes again when the
output runs dry mid-track. It waits for `AudioTimingSettings.RebufferMs` (1500,
0 turns it off), and plays whatever it has after `RebufferDeadlineMs`. A
rebuffer that gets nothing stays closed, so the end of the queue does not loop.
The resume fades in. The watchdog logs each rebuffer.

**Not settled: why the link stalled.** The phone was on Videotron, not on Wi-Fi.
The `/info` check timed out twice in the same minute, and the server saw nothing
but quiet connections, so it looks like the link. But "To Forget" had sat on one
connection for 6.5 minutes with nobody reading it (decode-ahead with full
staging) and was below real time from the moment it resumed. The restart did not
prove anything either way: the relaunched app also got nothing for 7 s before
playing. Logging added to settle it next time:

- `SeekableHttpStream`: "Reading … again after Ns without a read" when a parked
  connection is picked up, and "arriving slower than it plays … Nx real time"
  per 10 s window while the reader is starved. Real time comes from
  `RealTimeBytesPerSecond`, which `FfmpegTrackDecoder` sets.
- `NetworkDiscoveryService`: `/info` answers slower than 1 s, which measures the
  link apart from the audio's own connections.
- `AppleNetworkChange`: the NWPath on every real change, with unchanged repeats
  summed up once a minute. That day's log shows "Network changed" every 3 s for
  hours, and each one re-polls every server address.

If the slow line fires on a long-parked connection while `/info` stays fast,
the fix is to drop a decode-ahead's connection once its staging is full and
reopen at the offset when promoted. If the 3 s loop turns out to be unchanged
repeats, it should be de-duplicated in `AppleNetworkChange`.

## Phase 6 — put the output stage where the controls are (steps 1–7 built)

Phase 5 left a fork: pure-C render callback on Android and iOS, managed callback
on desktop. This phase was originally scoped as collapsing that fork — one
render path, six more RIDs, delete the managed `DataCallback` — and that framing
was wrong. Reading the two paths against each other turned up what the fork is
actually hiding.

**The fork is a symptom. The defect underneath it is an ordering one:** Phase 5
put the render buffer on the far side of the output stage, so everything
interactive — the volume slider, every EQ band — is computed *before* it is
queued and reaches the speaker a buffer-depth late.

### M. Interactive controls lag by the render buffer's depth — mobile, shipping today

`MiniaudioSink.Volume` writes `_outputStage.TargetGain` (`:450`) and
`ApplyEqualizer` swaps `_outputStage.Equalizer` (`:454`). Both are read by
`OutputStage.Process`, and on the bridge path `Process` runs on the **feeder**
(`AudioFeeder.cs:216`), immediately before `_bridge.Write`. So everything queued
in the bridge has already been gained and filtered: up to `NativeBufferMs` (300)
of audio that cannot reflect the knob just turned.

Desktop does not have this. There `useBridge` is false — the `Miniaudio-CS`
NuGet exports none of the bridge symbols, so `NativeAudioBridge.IsAvailable` is
false and the managed `DataCallback` runs `Process` on the device buffer itself
(`MiniaudioSink.cs:1153`), where a change applies to the very next callback.
Confirmed by ear on both: desktop volume and EQ are immediate, the phone's lag.

It went unnoticed because the control anyone reaches for on a phone is the
hardware volume button, which drives the *system* mixer and never touches
`OutputStage` — `ma_device_set_master_volume` is pinned to 1.0
(`MiniaudioSink.cs:711`) and all of Flower's gain goes through the output stage.
The in-app slider is the laggy one, and on a phone it is rarely the one used.

Two notes on severity, because they cut against each other. It is not a playback
defect: nothing clicks, drops or mistimes, and A–L's symptoms are genuinely
gone. But it is worst where it is least expected. Tuning an EQ band is a
listen-adjust-listen loop, and 300ms breaks the link between the gesture and the
result — you cannot tell whether what you are hearing came from the last
movement or the one before. That is more disruptive than the same lag on volume,
where loudness is monotonic and you can navigate by direction alone.

### The rule this is an instance of

> **A buffer's depth costs control latency only when the processing happens
> before the buffer.**

Which is what makes the depths in this pipeline look paradoxical until it is
applied:

| Buffer | Depth | Holds | Control latency it costs |
|---|---|---|---|
| `GaplessRingBuffer` | 2s (`GaplessAudioManager.cs:109`) | raw decoded PCM | none |
| staging ring, per armed track | 60s (`GaplessCoordinator.DefaultStagingCapacityBytes`) | raw decoded PCM | none |
| bridge (`NativeBufferMs`) | 300ms | gained, filtered PCM | all 300ms |

The gapless ring is nearly seven times deeper and free. The bridge is shallow
and costs everything, because it is the only buffer downstream of the
processing.

**So the depth is not the thing to tune.** 300 is not arbitrary — it is the
measured Mono stall distribution on a real iPhone: seven stalls over 100ms
across a full day, exactly one over 250ms. The generic recommendation for a
render buffer is 10–50ms, and it assumes a runtime that does not suspend the
audio thread. Adopting it here would trade the lag back for seven audible
dropouts a day, which is the trade Phase 5 deliberately made in the other
direction. Reordering gets both.

### The fix: the output stage belongs in the callback

Move `OutputStage` to the consumer end and have the bridge carry **raw** PCM.
Then the bridge's depth becomes ordinary decode-side runway like the ring's and
can stay at 300 or go higher for free; every knob applies to the next device
buffer on every platform; and desktop can adopt the C callback with no
regression to trade away — which is what the original framing of this phase was
missing entirely.

**This is not a violation of the "boring callback" rule.** That rule's
prohibitions — decode, allocate, lock, open files, wait on events, run async,
trigger GC — are all either unbounded or unpredictable. A biquad is neither:
fixed multiply-adds per sample, pre-allocated state, no branches worth naming.
It is the same class of work as the final format conversion every version of
that advice explicitly permits, with a larger constant.

And the cost is already measured, on the platform that matters. The desktop
callback runs the whole output stage today, and Phase 5's iPhone logs — captured
from the *managed* callback, before any of this — record it never exceeding
**~3ms against a 42.7ms period**. That number was taken during the failure: the
callbacks that arrived 66, 84, 189 and 668ms late each ran in under 3ms, with
the ring full and `Underruns=0`. The workload was never the problem; a managed
thread being suspended was. A pure-C callback is immune to that *regardless of
how much work it does*, which is the point the original scoping missed —
trivial-ness and GC-immunity were being treated as one property, and only the
second is load-bearing.

### Float end to end

Do this at the same time, because it touches the same buffers and the same
conversion points, and because the callback-side stage wants float anyway
(`ma_peak2` — the same RBJ peaking biquad `Equalizer.BuildFrom` constructs, and
miniaudio's own — is f32-native).

Today `ffaudio` converts the codec's output to packed S24, `Widen` converts S24
back to float, the stage works in float, `Requantise` converts to S24, and the
OS mixer converts it to float again — CoreAudio and WASAPI shared mode are both
float32 internally. Most of a real library is AAC/MP3/Vorbis/Opus, all of which
decode to `AV_SAMPLE_FMT_FLTP` natively, so two of those conversions are a round
trip through integer that happens *before the EQ ever sees the signal*.

Carrying float from decoder to one conversion at the device write collapses
that, and deletes:

- **The format negotiation and its freeze.** `GaplessFormat`'s format is
  negotiated from the decoder capped by the device and then frozen for the
  session, because a decoder already open cannot change format — which is why
  `MiniaudioSink.OpenDevice` carries `_hasNegotiatedFormat`, why a device
  refusing `ma_format_s24` narrows the pipeline and reopens, and why a
  mid-session device change is stuck with the first device's answer. With float
  the decoder never needs to know what the device takes. (The sample *rate*
  stays negotiated and frozen. That one is real, because resampling.)
- **`Widen`**, and the packed-S24 sign-extension trap inside it.
- **`MaxBytesPerSample`**, and `flower_audio_bridge_create`'s `bytesPerSample`
  contract — the 2-or-3 refusal that exists only because the transport envelope
  reads samples rather than bytes. (Step 2 deleted the first. Step 3 changed
  the second rather than deleting it: the transport envelope now works on
  float, but the callback still has to know the width it requantises to, so
  `create` still takes it and still refuses anything but 2 or 3.)

`PcmSampleFormat`'s own comment (it is `DeviceSampleFormat` now) rejected
F32, but it rejected it as a *third* format alongside S16 and S24, on the
grounds that it "means giving `OutputStage` a double path". As the single canonical format it removes a path rather than
adding one, so that reasoning does not carry. The note about S32 still does, and
for the same reason: a float mantissa holds exactly 24 bits.

Three costs, none fatal - all three paid in step 2:

- **`ffaudio` must emit float, and it lives in another repo now**
  (`../FFAudio.NET`, currently producing `pack_s24`). This is the one genuine
  external dependency in the phase.
  `scripts/use-latest-main-ffaudio-package.sh` exists for exactly this: testing
  an unpublished library change against Flower before it is published.
- **Dither loses its integer-units trick.** `Widen` deliberately does not
  normalise, so the dither's one-LSB triangle, the full-scale clamp and the
  "already an exact integer, do not dither it" test are all the same code at 16
  and 24 bits. Normalised float means scaling to the destination LSB first — one
  multiply, with the transparency test becoming "is `value × fullScale` exact?".
  Rework, not redesign.
- **Bit-exactness must be re-proved rather than assumed.**
  `GaplessCoordinatorRealDecodeTests` asserts a decoded track is byte-identical
  to the file it came from, and `PcmOracle` holds lossless-at-pipeline-rate to
  byte for byte. Scaling by 2^15 or 2^23 is a pure exponent change and should
  round-trip exactly — which is a claim those tests should make, not one to
  reason about.

### Does the bridge survive?

Once it carries raw PCM it holds the same thing `GaplessRingBuffer` holds, and
`AudioFeeder` becomes a thread whose entire job is copying between two rings of
identical content. Collapsing to one would delete the feeder, a memcpy of every
sample, `NativeBufferMs`, the bridge's request/acknowledge/120ms-timeout flush
handshake (the ring's generation protocol already solves that problem, and
solves it better — it was built after a real corruption window and handles the
mid-flight write the handshake papers over with a timeout), the second prime
latch, and `IAudioSink.BufferedBytes`, which exists only because of the bridge
(`MiniaudioSink.cs:1291`) and leaks into position arithmetic
(`GaplessAudioManager.cs:250`) and end-of-queue detection (`:354`).

**Not first, though, and possibly not at all.** It needs the ring's storage and
indices in native memory, and the *reader* half of its generation/rebase
protocol reimplemented in C. That protocol is finding A — the one whose failure
*is* the looped-fragment symptom this whole plan started from — and its
correctness argument runs fifty lines of class comment. Rewriting it in a
language with none of its tests is the riskiest single change available in this
codebase, and it buys no latency and negligible CPU. Move the output stage
first, prove it on hardware, and treat the collapse as a separate question asked
later, if ever.

### What it costs, honestly

- **The exception net goes.** `DataCallback` wraps `Render` in a try/catch that
  clears the buffer to silence and increments a counter the watchdog surfaces a
  second later, turning an invisible dropout into a logged error. C has no
  equivalent: a bug there is garbage samples or a crash, in a real-time context,
  on a phone.
- **Headroom shrinks if the period ever does.** 3ms against 42.7ms is
  comfortable because miniaudio's *conservative* profile is in use. On the
  low-latency profile the same work stops being free. That is an argument
  against also shrinking the period, not against the work.
- **The coefficient swap is the genuinely delicate piece.** `ApplyEqualizer`
  crossfades from the outgoing filter's output to the incoming one's across a
  single buffer, because dropping new coefficients onto a zeroed delay line
  clicks — an accepted defect in feature §1 until this fixed it. In C
  that becomes an atomic swap to a pre-built coefficient set plus the same
  crossfade: lock-free and bounded, but it is the one place another thread hands
  the callback new state, so it is where the care goes.
- **64-bit atomics on `armeabi-v7a`.** `GaplessRingBuffer`'s comment flags this
  as why it uses `Interlocked` rather than `Volatile.Read`. The bridge already
  does 64-bit atomics and ships on that ABI, so this is probably fine — but
  "probably" should become a check before anything is designed around it, since
  a non-lock-free atomic on the render thread is finding B over again.

**The mitigation for the first of those is the C# stage itself.** Keep
`OutputStage` and `Equalizer` as a reference implementation and hold the C to
matching them sample for sample — the same arrangement `PcmOracle` already uses
to hold a decoder to its fixture. It is stronger than either alone, it keeps
`OutputStageTests`, `EqualizerTests` and `Pcm.cs`'s Goertzel/step-discontinuity
harness earning their keep, and it belongs on the phone, where the C actually
executes. Built as step 4: `NativeOutputStageChecks`, run by the phone runners
that took over from `Flower.DeviceChecks`. Step 8 now retires this arrangement
once the port has been verified and heard. Those same tests move onto the C
itself, and the reference implementation goes; step 8 says what that gives up.

### Steps

Each is landable on its own. `NativeAudioBridge.IsAvailable` probes for symbols
rather than switching on `OperatingSystem`, so a platform with no binary
degrades to the managed path instead of failing to start. Nothing here is a flag
day.

1. **Check the `armeabi-v7a` 64-bit atomics question.** Cheap, and it can
   invalidate the shape before any of it is designed.

   **Done: lock-free on every shipped ABI.** The shipped `armeabi-v7a`
   `libminiaudio.so` is Thumb-2 with 233 inlined `ldrexd`/`strexd` pairs and
   imports no `libatomic` or `__sync` helper, and NDK 28's clang reports
   `ATOMIC_LLONG_LOCK_FREE == 2` for `armv7a`, `aarch64` and `x86_64` (and
   `i686`, via `cmpxchg8b`). `impl.c` now `_Static_assert`s 64-bit, 32-bit and
   pointer atomics lock-free, so a target where one is not refuses to build
   (checked against `armv5te`, which has no 64-bit exclusive pair) instead of
   shipping a lock on the render thread. Compile-time only, so the vendored
   binaries did not need rebuilding.
2. **Float through the managed pipeline first** — `ffaudio` emitting float, the
   ring carrying it, `Widen` deleted, `Requantise` scaling to the destination
   LSB, the format negotiation removed. Entirely in C#, on every platform, with
   the existing tests as the proof. Desktop gets the fidelity win here and
   mobile's lag is untouched, which makes this independently worth shipping.

   **Built.** What it came to, beyond the list above:

   - **No FFAudio.NET change was needed.** `F32` (`AV_SAMPLE_FMT_FLT`) was
     already in the pinned `1.0.2`, so the "one genuine external dependency"
     was already met. `FfmpegTrackDecoder` asks for it unconditionally.
   - **The pipeline's format is a rate and nothing else.** `PcmFormat` is
     `(SampleRate)`, `GaplessFormat.BytesPerFrame` is a constant 8, and
     `PcmSampleFormat` became `DeviceSampleFormat` - `OutputStage`'s
     destination only. `MiniaudioSink.OpenDevice` asks for S24 and falls back
     to S16 at *every* open, since no decoder depends on the answer; only the
     rate is still frozen (`_hasNegotiatedRate`). `OutputStage.DeviceFormat` is
     settable while the device is stopped, so a reopen keeps the stage
     carrying the EQ and volume.
   - **`Process(Span<float> samples, Span<byte> destination, ...)`** rather
     than in place, since input and output no longer share a width. The
     managed callback reads the ring into a grow-only float scratch; the
     feeder into a fixed float chunk.
   - **Units that used to coincide had to be told apart.** The feeder's prime
     latch adds the bridge's depth to the ring's, and those are now device
     bytes and float bytes, so `PrimeLatch.TryRelease` counts frames.
     `IAudioSink.BufferedBytes` became `BufferedFrames` for the same reason -
     it is subtracted from ring-byte positions.
   - **A 24-bit file on a device that refused S24** is now requantised by
     `OutputStage`, dithered, rather than narrowed by swresample before the
     ring. `CanonicalFormatTests` holds it to the same 1.5-count bound and to
     zero bias, which is the difference from truncating.
   - **Bit-exactness re-proved, not assumed.** The real-decode tests narrow the
     float capture back to 16 bits through `Pcm.ToS16`, which fails on any
     sample off the 16-bit grid rather than rounding it; `PcmOracle` compares
     float to the fixture's exact float image bit for bit. New:
     `A_24_bit_file_comes_out_of_a_24_bit_device_as_the_integers_it_holds`
     decodes a 24-bit WAV through FFmpeg and an S24 `OutputStage` and gets
     every integer back (and fails at frame 1 if the low 8 bits are dropped).
   - **One oracle hole closed.** Float can carry NaN; every comparison against
     one is false, so `PcmOracle.ToneMismatch` would have passed NaN audio. It
     now refuses non-finite samples.
   - **Cost: memory.** Float frames are 8 bytes against S24's 6, so the shared
     ring and every staging ring are a third larger in bytes for the same
     time - the 60s staging ring is about 23MB at 48kHz, from 17MB.
   - **Listened to on the Mac and on an iPhone, both formats** - a
     24-bit/96kHz ALAC album at S24 and again under `FLOWER_DEVICE_FORMAT=s16`,
     with the log confirming each; no difference worth noting either way. The
     S16 case stands in for a device that refuses S24, which now gets
     `OutputStage`'s dithered requantisation - except that in shared mode
     none does, because miniaudio converts whatever it is handed. `FLOWER_DEVICE_FORMAT=s16` opens the
     device at 16 bits anyway, so that path can be heard; on an iPhone, launch
     with `DEVICECTL_CHILD_FLOWER_DEVICE_FORMAT=s16 scripts/deploy-ios-device.sh`,
     which `devicectl` passes through to the app. The log says
     `Rendering the output device's native ...Hz at S16` when it took.
3. **Move the output stage into the C callback**, bridge carrying raw float.
   Mobile only, since mobile is where the bridge runs. This is the step that
   fixes M.

   **Built.** What changed, and what to know before touching it:

   - **The bridge carries the ring's float, unprocessed.** `AudioFeeder` is
     now a copy and the prime latch, nothing else; `flower_audio_bridge_create`
     takes a capacity in pipeline frames, the channel count (2, refused
     otherwise) and the device's width. Write, flush and prime are unchanged.
   - **The callback runs a port of `OutputStage`**: EQ with the one-buffer
     crossfade, volume ramp, declick, TPDF dither, clamp, then S16 or packed
     S24. The transport envelope is the bridge's own as before, now applied
     in float before requantising rather than to the integers after it, which
     deleted the S24 read/write helpers. The controls are
     `flower_audio_bridge_set_target_gain`, `_set_timing` (in frames) and
     `_set_equalizer`, which takes `Equalizer.BuildFrom`'s float
     coefficients as they are, through a triple buffer so neither side waits
     or reads a half-written curve.
   - **The declick is armed where the generation change used to arm it**: on
     a flush acknowledgement and on the prime latch reopening (a rebuffer,
     which was `FadeInNextAudio`).
   - **One deliberate difference:** a bridge is made per device open, so the
     volume ramp starts at the target rather than at unity, or every reopen
     would sweep down from full volume. `OutputStage` outlives its devices
     and keeps starting at unity.
   - **Held to the C# sample for sample, and checked rather than hoped.** A
     throwaway harness built `impl.c` for macOS and ran both through two
     seconds of tone and noise at S16 and S24 - unity, a volume ramp mid-play,
     EQ on, an EQ swap, a clipping boost, swaps to and from bypass, callback
     sizes from 1 to 5000 frames - and every byte matched. A mutation that
     only reassociates one subtraction in the biquad fails the EQ cases, so
     it sees the last bit. Two things make the match possible and must
     stay: `impl.c` turns floating-point contraction off (clang would
     otherwise fuse multiply-adds on arm64; none survive in any shipped ABI,
     checked in the disassembly), and rounding is `rintf`, which rounds
     half to even like `MathF.Round` - which is why Android now links `libm`.
     That harness was throwaway; step 4 put a permanent one in the tree.
   - **Controls reach the native side under `_stageGate`**, a lock held only
     around the store and never across a device open, so a control cannot
     land on a bridge `CloseDevice` is freeing. The watchdog's snapshot takes
     it too, which closes the same race it already had.
   - **An old binary is refused, not misread.** `NativeAudioBridge.IsAvailable`
     probes `flower_audio_bridge_set_target_gain` as well, so a stale build
     with the old `create` signature falls back to the managed callback.
4. **Verify by ear on the phone**: drag the volume slider, sweep an EQ band.
   Plus the sample-for-sample oracle above, on the phone.

   **Done.** By ear, on an iPhone after step 3: nothing wrong heard. The
   oracle is `NativeOutputStageChecks` (`Tests/Flower.Tests.Decoding.Scenarios`):

   - **Where it runs.** Both phone runners, which replaced
     `Flower.DeviceChecks`: `scripts/ios-decoding-tests.sh` (NativeAOT, as
     Flower.iOS ships) and `scripts/android-decoding-tests.sh`. Each now
     carries the miniaudio binary its head ships. Also in the Mono iOS
     suite (`scripts/ios-tests.sh`), as `NativeOutputStageTests`. On desktop
     that fact skips, since the NuGet's miniaudio has no bridge; on a phone
     a missing bridge fails instead of skipping, because there it is a
     packaging fault.
   - **How, with no device.** The bridge uses the `ma_device*` only as a
     key, so the check attaches it to a dummy pointer and calls the real
     compiled callback directly, from the shipped binary, on the phone's
     own CPU. The C# side gets the same input in the same pieces: a
     callback over the native 4096-frame chunk is split for `OutputStage`
     too, since the ramp step and an EQ crossfade are per piece.
   - **What it covers**, at S16 and S24: a 16-bit source at unity, a float
     source's dither, a volume ramp across callbacks, EQ from the first
     buffer, EQ swaps (one of them on a split callback), swaps to and from
     bypass, a boost past full scale, a flush and a rebuffer (the declick),
     and the ramp and declick turned off. Callback sizes run from 1 frame to
     5000. Compared bit for bit; a failure names the callback, frame,
     channel and both values.
   - **It catches what it exists for.** With one subtraction in the biquad
     reassociated (`a - b + c` as `a + (c - b)`), all 12 EQ scenarios failed
     by one LSB and the 6 without EQ passed. Passing on the iOS simulator
     (NativeAOT and Mono) and the arm64-v8a Android emulator. `armeabi-v7a`
     is not run, because an Apple-silicon emulator cannot run it; the
     disassembly check in step 5 covers it.
   - **One latent fault it turned up.** Under Mono on iOS, `NativeAudioBridge`'s
     P/Invokes resolved only because Mono reuses a library already loaded under
     the same name, and only Miniaudio-CS's assembly had a resolver. The app
     always opens a context first, so it never showed there. The check probes
     the bridge first, so it saw no bridge. `MiniaudioSink`'s static constructor
     now registers the resolver for Flower's assembly too, and `NativeAudioBridge`
     runs it before its first call. NativeAOT never consults a resolver.
5. **Split `flower_audio_bridge.c` out of `impl.c`** so both mobile builds take
   it as a second translation unit. No observable change — the refactor the
   desktop half rests on, and where to stop if the phase is interrupted.

   **The bridge does not depend on miniaudio**, which is what makes the rest
   affordable and was checked rather than assumed: it takes `ma_device*` purely
   as an opaque key for its device registry and never dereferences it, and
   `ma_uint32` is `uint32_t`. Extracted and compiled against nothing but its own
   header it builds clean, exports all 17 `flower_audio_bridge_*` symbols, and
   links against `libSystem` alone. So desktop does *not* mean rebuilding
   miniaudio from source or dropping the `Miniaudio-CS` NuGet — ship a small
   standalone `libflowerbridge` beside it and hand miniaudio a `dataCallback`
   pointer that lives in a different library, which it has no opinion about.

   **Done.** `native/miniaudio/flower_audio_bridge.c` holds the bridge, and
   includes no miniaudio header: `struct ma_device*` is an opaque key and the
   callback's signature is spelled in `stdint` types. `impl.c` is back to
   miniaudio plus the CoreAudio diagnostics. Android's CMake and
   `ios/build.sh` compile both files; on iOS the bridge is compiled as plain C,
   not Objective-C. Checked:

   - **Standalone.** It compiles clean under `-Wall -Wextra -pedantic` for
     macOS, iOS and all three Android ABIs. Built as a dylib it exports 20
     `flower_audio_bridge_*` symbols (step 3 added three controls since the
     count of 17 above) and links only `libSystem`.
   - **No change to what ships.** The rebuilt binaries export exactly the
     symbols the old ones did on all five slices (1197 on Android, 1199 on
     iOS), and the Android ones link the same libraries. The oracle passed
     on the iOS simulator before the split and after it, and on Android after
     it. Rebuilding the simulator slice gave a byte-identical binary.
   - **Still no fused multiply-adds**, checked in the bridge's own objects for
     iOS arm64 and every Android ABI. The same check on a build with
     contraction forced on finds 10, so it would see them.
6. **Build `libflowerbridge` for the six desktop RIDs.** `osx-arm64`/`osx-x64`
   build on a Mac today (the x64 cross was verified); `linux-x64`/`linux-arm64`
   need a container; `win-x64`/`win-arm64` need clang or a VS 17.5+ floor, since
   MSVC only supports C11 `stdatomic` behind `/experimental:c11atomics`. Neither
   Linux nor Windows can be produced from a Mac without extra tooling, so this
   wants CI — which makes it a natural companion to `AUTO-UPDATE-PLAN.md` Phase
   4, standing up a matrixed `windows-latest`/`macos-latest`/`ubuntu-latest` job
   over the same six RIDs. Doing them together is the strongest argument for the
   timing of either.

   **Built, and without CI.** The premise was wrong: Docker on a Mac produces
   all six. `native/miniaudio/desktop/build.sh` builds the macOS pair
   natively, Linux with GCC in a Debian container (arm64 natively, x64 by the
   cross compiler, so nothing runs emulated) and Windows with llvm-mingw's
   clang, both images pinned by digest. The binaries are checked in under
   `native/miniaudio/desktop/runtimes/<rid>/native/`, like the phones', which
   keeps every head runnable from a clean clone. What to know:

   - **GCC ignores `#pragma STDC FP_CONTRACT`**, and fuses multiply-adds by
     default on arm64. Every build now passes `-ffp-contract=off` as well
     (the phone builds too), and each script checks the disassembly for
     fused instructions - a build forced to contract finds 13, so the check
     sees them.
   - **Exports are explicit.** `FLOWER_AUDIO_BRIDGE_API` marks the 20
     functions and every build is `-fvisibility=hidden`; the script checks
     the count, the dependencies (libSystem; libc and libm; the UCRT and
     kernel32) and a glibc floor of 2.17 (the x64 build asks for 2.14).
   - **`FlowerBridge.props`** copies the build for the RID (or, with none,
     the SDK's own platform) flat beside the app. It is imported by the two
     desktop heads and the desktop test suites, never by `Flower.csproj`,
     whose references reach the phones. The macOS head bundles it into
     `Contents/MonoBundle` and rewrites its install name, which needed
     `-headerpad_max_install_names`.
   - **`NativeOutputStageTests` is an ordinary desktop test now**, and fails
     rather than skips without a bridge. Passing on macOS arm64 and, in a
     .NET SDK container, Linux arm64 (the GCC build, the one the pragma
     did not cover). CI adds x64 Linux and Windows and macOS. `osx-x64` and
     `win-arm64` are built and checked but not run anywhere.
   - **Drift is caught.** Each of the three build scripts writes
     `flowerbridge-source.sha256` beside its binaries, and CI's Linux job
     fails a tree whose bridge source matches none of them.
7. **Point `NativeAudioBridge`'s `DllImport("miniaudio")` at `"flowerbridge"`**,
   and land one RID at a time, listening on each.

   **Built, all RIDs at once rather than one at a time, and listened to on
   macOS, Linux and Windows with nothing wrong heard (November 2026).** One import name everywhere meant the phones had to stop
   carrying the bridge inside their miniaudio, so they now build it as a
   library of its own too: `libflowerbridge.so` per ABI beside
   `libminiaudio.so` (the head globs `libs/<abi>/*.so`), and
   `flowerbridge.framework` beside `miniaudio.framework` on iOS, with a
   `DirectPInvoke` for NativeAOT and `MiniaudioSink`'s Mono resolver taught
   the second framework name. miniaudio itself lost exactly the bridge's 20
   symbols. Both phone runners pass all 113 checks against the split build
   (iOS on NativeAOT, Android on the arm64 emulator).

   The managed callback is still there, and still taken when the library is
   missing (a RID nobody built; the build warns) or `NativeBufferMs` is 0.
   So listening on the Mac costs nothing to back out of:
   `"AudioTiming": { "NativeBufferMs": 0 }` in `settings.json` puts the
   managed callback back for an A/B, from the next device open (a restart or
   an output switch).
8. **Delete the managed render path: the `DataCallback`, and `OutputStage`
   with it**, once desktop has been listened to.

   This step used to delete only the callback and keep `OutputStage` in C#
   as the oracle. Reconsidered in November 2026: now that every platform
   renders through the bridge, nothing runs `OutputStage` but the fallback
   and the tests, and holding two implementations to each other byte for
   byte means every change to the stage is made twice, in two languages,
   for as long as both exist. The oracle earned its keep while the port was
   new. Now the port is checked, and what the tests should hold the stage to
   is the properties it must have, not agreement with a second copy of
   itself. So the stage goes with the callback, and its tests move onto the C.

   **Not done yet, and no longer waiting.** It waited on desktop being
   listened to through the bridge, since until then the managed callback was
   the A/B (`NativeBufferMs: 0`, above) and deleting it deleted that. All
   three desktops have now been heard through it.

   In order:

   1. **Listen.** Done (November 2026): macOS, Linux and Windows, all good.
   2. **Point the property tests at the C while the C# still exists**, so
      each is seen passing against both before one of them goes. They need
      a harness that drives the bridge's callback the way
      `NativeOutputStageChecks` already does: a dummy `ma_device` the bridge
      uses only as a key, and the callback called directly. It sits behind
      the shape `OutputStage` has, with float frames in, device bytes out,
      and the gain, timing and EQ controls. Then move:
      - `OutputStageTests`: ramps, declick, clamp.
      - `CanonicalFormatTests`: the dither's zero bias and 1.5-count bound.
      - `EqualizerTests`' response half: gain at each band's centre,
        filter state carried across calls of different lengths, headroom,
        stability at every rate. `Equalizer.Process` goes with the stage,
        since `OutputStage` is its only caller. `Equalizer.BuildFrom` stays
        in C#, because the coefficients are computed there and handed to
        the bridge, so its coefficient tests stay as they are.
      - `FfmpegTrackDecoderTests`' trip through a 24-bit device's stage.
      - `RenderPumpSink`'s optional `processOutputStage` pass, which nothing
        currently turns on, so it can simply go.

      The desktop suites already load libflowerbridge through
      `ClientTests.props`, and the iOS Mono suite carries the framework, so
      these run on a phone as well. That is more coverage than today, when
      the property tests only ever run the C#.
   3. **Delete** the managed `DataCallback`, `OutputStage`,
      `NativeOutputStageChecks` and `NativeOutputStageTests` (with one
      implementation there is nothing to compare), and `NativeBufferMs: 0`
      as a meaning. `FlowerBridge.props`'s missing-library warning becomes
      an error, and `MiniaudioSink` fails loudly on a missing bridge rather
      than falling back. CLAUDE.md's "change one, change both" goes too.
   4. **Decide on the callback timing diagnostics** (`AudioCallbackTiming`:
      gaps, late callbacks, render overruns). Only the managed callback
      gathers them, so desktop's watchdog already lost them by taking the
      bridge. Keeping them means porting them into the bridge's snapshot, in
      C. Either choice is defensible; losing them by accident is not.

   **What this costs: the cross-check.** Two independent implementations
   agreeing catches an error that still satisfies every bound, such as
   halves rounded the wrong way or a coefficient slightly off. Property
   tests can miss such an error by construction. That price is paid
   knowingly, and it is smaller now that the port has been verified than
   when it was new. Stepping through the stage in a C# debugger goes too.

   **Fused multiply-adds stay off**, even with one implementation. Fusing
   would buy accuracy around −138 dBFS at S24, below the dither, and speed
   nothing needs, since the whole stage took about 3 ms of a 42.7 ms period
   on an iPhone. It would cost identical output across architectures:
   clang fuses by default wherever the CPU has FMA, which is every arm64
   and only an x64 build targeted at newer CPUs. So `-ffp-contract=off` and
   the build scripts' disassembly check both stay.

### Sequencing, and how urgent this is

Not urgent. Desktop's controls are responsive today and it has no defect waiting
on any of this. Mobile's lag is real but reported as not a problem in practice,
for the good reason that the phone's own volume buttons bypass it entirely.

Steps 1–7 are built (see above). Steps 1–2 were a fidelity and
simplification win with no native work and no build infrastructure; step 3 is
the one that fixes a defect; step 4 proved it, by ear and byte for byte; step 5
made the bridge buildable on its own; steps 6–7 put desktop on it, which
turned out to need Docker rather than the CI matrix it was expected to wait
for. Desktop has been listened to, so step 8 is next: move the
property tests onto the C, delete the managed path, and decide about the
timing diagnostics.

### What not to adopt

Two things that appear in every generic version of this pipeline and are wrong
here:

- **A resampler between the decode ring and the render buffer.** There is
  deliberately none. `GaplessFormat` takes the output device's native rate and
  makes the decoder produce it, so nothing resamples on the way out —
  `MiniaudioSink` replaces the session rate with the opened device's before any
  decoder is constructed. That is finding I, and re-inserting a resample stage
  walks it back.
- **A 5–20ms device period.** The conservative profile's larger period is
  protective twice over: more slack for a stall to hide in, and the headroom
  that makes output-stage work in the callback comfortable. Shrinking it
  squeezes both at once.

`Flower.Web` is out of scope permanently — `WebAudioManager` drives an `<audio>`
element and has no render callback of either kind.

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
| `docs/AUDIOPHILE-PLAN.md` (since merged in as "Audiophile features") | item #1's "accepted click" note is superseded; hand it the double-resample item |
| `native/miniaudio/flower_audio_bridge.c` (new) | Phase 6 step 5: the bridge, out of `impl.c` and independent of miniaudio |
| `Tests/Flower.Tests.Decoding.Scenarios/NativeOutputStageChecks.cs`, `Flower.Tests/Audio/NativeOutputStageTests.cs` (new) | Phase 6 step 4: the C output stage held to `OutputStage` byte for byte |

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
The native callback it feeds had no automated coverage until Phase 6 step 4's
`NativeOutputStageChecks`, which checks its arithmetic on the phone runners; see
that phase's last paragraph for what is still not covered.

By ear, and done — which is the part that could not be automated:

1. **Desktop: no more clicks.** Seeking mid-track, hard Next presses, album
   auto-advance, a queue played to its end, the volume slider dragged during
   playback, EQ bands toggled while playing. None of it clicks, loops a
   fragment, or cuts the end of a song. That is A, B, C, D and E confirmed
   where they were reported, rather than only where they were tested.
2. **The volume taper is fine as it stands.** It was a real change — the slider
   used to be raw linear amplitude, so 50% was -6dB; it is now cubic, so 50% is
   about -18dB — and the question was whether the new travel feels right rather
   than whether it works. It does. `OutputStage.GainForVolumePercent` is the one
   line to revisit if that ever changes.
3. **Mobile: sustained everyday listening on a real iPhone, no blips.** This is
   the one that matters most, because it is the only evidence Phase 5's native
   render callback has against the thing it exists for. Its arithmetic is
   now checked on the phone runners (Phase 6 step 4), but no test can produce
   the stall it was built to survive, and the defect it
   fixes — a Mono GC stop-the-world suspending the audio thread — is by nature
   intermittent and mobile-only. Hours of ordinary listening without a blip is
   the shape of evidence that defect actually admits. It is what Phase 6's
   sequencing was waiting for.

Two checks from the original list were dropped rather than done, both because
what they would have told us arrived by a better road:

- Re-running with `AudioTiming.PrebufferMs` at 50 and `TransportFadeMs` at 5 was
  there to prove the knobs are real, not to change the defaults. A unit test
  over `AudioTimingSettings`' clamping and its use in `MiniaudioSink`/
  `AudioFeeder` would pin that better than ears can, and is worth writing if the
  question ever comes up again.
- Watching the Debug log for `Render watchdog`, `Handover was not gapless` and
  `no armed successor` was the pipeline reporting its own failures. "No clicks"
  is the same information arriving through the ear the warnings exist to
  protect, so it corroborates rather than adds.

## Audiophile features

Five requested features: gapless playback, multi-channel/hi-res sample rate support (24-bit/192kHz+), DSD/APE format support, a low-latency playback engine, and EQ with true bypass. Output-device selection itself is `AIRPLAY-BLUETOOTH-PLAN.md` territory, referenced here only where a feature depends on it.

The order they are worth doing in is "What is left, in order" at the top; the
original "Suggested order" was folded into it.

### A note on what is stale here

**Read the sections below as history with live conclusions attached, not as
current instructions.** This part was written when LibVLC did everything,
and revised once when LibVLC became decode-only. It is now neither: LibVLC is
gone entirely, `FfmpegTrackDecoder` is the only decoder, and every platform
renders through `MiniaudioSink`, which pulls float32 out of `GaplessRingBuffer`
at the negotiated device rate and has `OutputStage` requantise it to the
device's S24 or S16. On every platform that stage runs in the C render
callback of `libflowerbridge`.

That matters most for **#2**, which is stale outright — it proposes tuning
`new LibVLC(options)` and `file-caching`, and neither exists. Its underlying
question (what latency does the engine impose, and can it be lowered) is still
worth asking; it just has to be asked of `FfmpegTrackDecoder`, `AudioFeeder` and
`MiniaudioSink`'s period sizing instead, which is a different investigation
rather than a re-worded one.

**#3** is half done: Monkey's Audio shipped in October 2026, alongside Ogg
Vorbis, Opus and AIFF, and DSD has not. The playback half's two options
collapsed into one, because "build VLC demux plugins per platform" has no VLC
to build them for, and FFAudio.NET's slim codec set carries the APE demuxer and
decoder. DSD is what is left; see §3 below.

**#5** is the one that aged well, because most of it was built: its own
narrative below is the FFmpeg migration, step by step.

#1 hit this same problem and was re-scoped when it was built; #6 hit it and was
built somewhere else entirely.

### Key findings

- Historical baseline: one plain `VlcAudioManager` — no equalizer, no preloading, no output-format config. Auto-advance only started the next track after `EndReached`, so the gap included full demux/codec-open latency.
- Confirmed in installed `LibVLCSharp.dll` (3.10.0): `SetEqualizer(null)`/`UnsetEqualizer()` is a **true bypass**, not a flat 0dB filter. `SetAudioCallbacks`/`SetAudioFormat` exist (same seam `AIRPLAY-BLUETOOTH-PLAN.md` uses). No independent "pass multichannel through untouched" toggle exists in the API. **No longer the implementation path for #1 below** — this API applied to `LibVlcRawStreamSink`'s render `MediaPlayer`, which every platform's render path has since moved off (see `MiniaudioSink`'s own class comment); the finding itself still stands as a fact about LibVLC, just not one anything in this codebase calls anymore.
- Confirmed in `TagLibSharp.dll` (2.3.0): `.ape` and `.dsf` tag reading works today via the existing `TagLib.File.Create` call; `.dff` (DSDIFF) is not supported by this version.
- **Confirmed by inspecting the installed macOS VLC's native plugin directory: no Monkey's Audio or DSD demux/decode plugins exist.** Mainline VLC does not ship native `.ape`/`.dsf` playback support — a real gap, not an assumption. Android/iOS's LibVLC NuGets haven't been checked yet and could be in the same position.

### 1. EQ with true bypass — Done

Implemented as a 10-band graphic EQ, not against LibVLC (see above) — LibVLC is decode-only now, and every platform renders through `MiniaudioSink`, a plain `ma_device` pulling raw PCM from `GaplessRingBuffer`. The EQ is a pure-C# RBJ-cookbook peaking/bell biquad cascade (`Equalizer`/`EqualizerSettings`, `Flower/Audio/`), spliced directly into `MiniaudioSink.DataCallback` after the ring read. `IAudioSink.ApplyEqualizer(Equalizer? equalizer)` carries a rebuilt-and-atomically-swapped processor down from `IAudioManager`/`GaplessAudioManager`; passing `null` is **true bypass** — `DataCallback` skips the processing call entirely rather than running an all-zero-dB filter, preserving this section's original bypass requirement under the new render pipeline.

Fixed at 10 bands (31Hz–16kHz, ISO-ish spacing, `Q≈1.41`), ±12dB per band, plus a preamp stage applied before the cascade — no presets, no parametric (frequency/Q) adjustment; out of scope for what was asked. `EqualizerSettings` (enabled flag, preamp, 10 band gains) persists via `AppSettings.EqualizerSettings`/`AppSettingsStore`, and is eagerly re-applied at startup in `App.axaml.cs` — not only when the Equalizer window happens to be opened. UI: `EqualizerWindow`/`EqualizerViewModel`, reachable via **View → Equalizer…**, live-apply with no "Apply" button. A settings change still rebuilds the whole processor (fresh coefficients and fresh filter delay-line state together), but that is no longer heard: `OutputStage` crossfades from the outgoing filter's output to the incoming one's across a single render callback. The "accepted minor transient click" this section used to record is gone — see Phase 2 above, which also moved the EQ off S16 (it used to round and hard-clamp to 16-bit mid-chain, so any positive band gain clipped outright) onto the float path that requantises once, at the end, with dither.

### 2. Low-latency playback engine — Small effort, Low risk

**Superseded in its specifics; the question survives.** As written this was: pass a lean option set into `new LibVLC(options)`, skipping the video/subpicture/OSD/stats subsystems an audio-only app never uses, and lower `file-caching` from LibVLC's ~300ms default. None of those knobs exist any more.

Re-scoped, the same question is about three places: `FfmpegTrackDecoder`'s open path (it already avoids blocking work on the play thread, and `StartDecoding` opens on its own decode thread), `AudioFeeder`'s fill policy, and `MiniaudioSink`'s period sizing — the last being the one that actually sets the floor on how quickly a press of play makes a sound. `AudioTimingSettings` is where the tunables already live. Worth measuring before assuming there is anything to win.

### 3. DSD (`.dsf`) + Monkey's Audio (`.ape`) — Done

**DSD `.dsf` is done** (November 2026). `.dsf` is in
`Importer.ValidExtensions`, and `Tests/Flower.Tests.Decoding.Scenarios/Fixtures/sine.dsf`
is the same two seconds of 440Hz at DSD64, run through all six per-format
checks on every platform (from disk, streamed, without ranges, without HEAD,
mislabelled, and started the way Play starts it). Nothing on a Mac encodes
DSD, so `regenerate.sh` writes it itself, with a second-order sigma-delta
modulator and a DSF writer. It cannot be held to `sine.wav` byte for byte,
since its PCM is a low-pass filter's reading of one-bit samples, so it takes
the tone oracle the lossy formats take. The pinned FFAudio (1.0.2) carries all
four `dsd_*` decoders on every head; FFmpeg reports the file as
`dsd_lsbf_planar` and decodes it at an eighth of the DSD rate (352.8kHz),
which the pipeline then resamples like any other rate. TagLib# reads and
writes a DSF's ID3v2 tag and reports its own rate and depth (2822.4kHz,
1-bit), so Track Info's codec reads "DSF Audio, decoded to PCM" for anything
1-bit (`TrackInfoViewModel.CodecValue`): those figures beside a bare "DSF
Audio" read as though the DAC were sent DSD. `.dff` stays out, as below. The
browser UI cannot play it, as it cannot play Monkey's Audio: its `<audio>`
element decodes only what the browser does.

**Monkey's Audio is done** (October 2026), in the same change that added Ogg
Vorbis, Opus and AIFF: `.ape` is in `Importer.ValidExtensions`, and
`Tests/Flower.Tests.Decoding.Scenarios/Fixtures` carries a `sine.ape` fixture that has to decode back
to `sine.wav` byte for byte, from disk and over every streaming shape. FFmpeg
cannot encode APE, so `regenerate.sh` uses Monkey's Audio's own `mac` encoder.
What follows is the original finding, kept for its reasoning.

`.ape` is in `Importer.ValidExtensions`; add `.dsf` — tagging works today regardless of playback, so library browsing/sorting works immediately. Skip `.dff` until TagLib# supports it or a real library needs it. **Playback requires real engineering, but less than it did.** The finding was that mainline VLC ships no Monkey's Audio or DSD demux/decode plugins, leaving two options: build third-party VLC plugins per platform, or decode outside LibVLC. Option (a) is void — there is no VLC. Option (b) is what the whole pipeline now is, which turned the question into a build-configuration one. **That question is now answered.** `ape` was always in both lists; `dsf` was in the demuxer list with no `dsd_*` decoder behind it, which is worse than not claiming the format - the file demuxed and then found no decoder - and the four `dsd_*` decoders are in the list now (`native/ffmpeg/<ios|android>/build-ffmpeg.sh`, and FFAudio.NET's `native/codec-set.sh`, which is where that list is heading). It stays inside the LGPL constraint: neither is a `--enable-gpl` component. Desktop asks nothing of the list at all, since macOS and Linux link the system FFmpeg and Windows a pinned download. What is left is not configuration but proof - no phone has yet been built from a list with DSD in it. Decoded to PCM at an eighth of the DSD rate, never passed through. Until either lands, wrap `Play()` for these formats in a try/catch with a user-facing "unsupported format" message. Once playback exists, be clear in UI copy that DSD is decoded to PCM, not passed through natively.

### 4. Near-gapless playback (pragmatic step) — Superseded, never built

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

### 5. Multi-channel / hi-res passthrough — Medium effort, Medium risk

**The bounded double-resample slice is done.** At sink initialization, `MiniaudioSink.OpenDevice` asks miniaudio for the endpoint's native rate, then configures `GaplessFormat` before either decoder is constructed. A 44.1kHz source on a 44.1kHz endpoint is consequently converted once by the decoder's own resampler (`swresample` now, LibVLC then), rather than first to fixed 48kHz and then again by miniaudio's linear resampler. The fallback remains 48kHz for headless/test sinks.

The session rate is deliberately held across later output-device changes: changing it while a current or armed decoder exists would corrupt timing and playback speed. If the newly selected endpoint differs, miniaudio can resample for that switch. Full per-device native-rate renegotiation belongs with the wider format/passthrough redesign below.

`AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's picker gives desktop direct mode an
explicit endpoint; iOS uses the system route picker instead. The current
name/id-only device list does not expose format capabilities, so the direct
path must add an exact-format probe and retain the normal shared-mode fallback.

#### Decoder/backend spike — complete

**Finding:** `TrackDecoder` cannot be the hi-res decoder. Its one LibVLC `amem`
path calls `SetAudioFormat("S16N", ...)`; LibVLC decodes a 24-bit source but
hands Flower S16 samples. No downstream device capability can recover the lost
precision. The existing 96kHz/24-bit PCM probe confirms the alternative is
real: FFmpeg reports the file as `pcm_s24le`, `sample_fmt=s32`,
`sample_rate=96000`, and `bits_per_raw_sample=24`. FFmpeg represents 24-bit
PCM in a 32-bit integer container, with the eight low bits empty; packing that
to miniaudio's tightly-packed `ma_format_s24` preserves every source value.

**Decision:** add a small, owned `ffaudio` native façade, linked only to
FFmpeg's `avformat`, `avcodec`, `avutil`, and `swresample` libraries. It will
open one file/stream, expose its decoded PCM format, read interleaved frames,
seek, and close. Keep its ABI deliberately small and consume it with ordinary
P/Invoke; do **not** adopt `FFmpeg.AutoGen`. AutoGen is generated C# bindings,
not a packaged decoder runtime: Flower would still have to build, ship, load,
and keep ABI-compatible FFmpeg libraries for every target. A façade confines
that ABI, C-structure, callback, and ownership surface to one pinned native
component, which is materially safer for iOS/Android AOT builds. AutoGen's
static-binding option could be made to work, but offers no corresponding
packaging or ABI advantage here.

This is a cross-platform design, not yet a proven cross-platform feature. The
same exported façade ABI must be built and exercised on Windows (`.dll`),
macOS (`.dylib`), Linux (`.so`), Android (one `.so` per supported ABI), and iOS
(a pinned framework/XCFramework). Desktop may dynamically deploy LGPL-only
FFmpeg libraries; Android and iOS need Flower-built, pinned native libraries
and the corresponding FFmpeg source/configuration offer. No GPL or non-free
FFmpeg component may be enabled. Do not describe the decoder as supporting all
Flower platforms until CI builds, packages, and hardware-tests every one of
those artifacts.

The future `FfmpegTrackDecoder` implements `ITrackDecoder`, so
`GaplessCoordinator` keeps its current decode-ahead/retargeting logic. The
current `TrackDecoder` remains the normal S16 decoder until direct mode elects
the new backend. FFmpeg's decoded samples can be either packed S32 or float;
the façade must normalize planar output before it crosses into managed code.

#### The 16-bit ceiling, measured

The spike's finding above was re-verified by direct measurement on 2026-09-03,
because it is the single fact the whole decision rests on. A one-second
synthetic WAV of constant sample value 16384 was decoded through the same
`amem` seam `TrackDecoder` uses, asking in turn for each of the three formats
LibVLC's API documents:

```
requested=S16N  firstBytes=00-40-00-40-...
requested=FL32  firstBytes=00-40-00-40-...
requested=S32N  firstBytes=00-40-00-40-...
```

All three identical, and all three are int16 16384. Honoured FL32 would have
delivered 0.5 as `00 00 00 3F`. The request is not merely ignored in some
cases - it is never read. VLC's own source says so: 3.0.x's
`modules/audio_output/amem.c` opens its `Start` with `char format[5] = "S16N";`
and carries a literal `/* TODO: amem-format */`.

So every track Flower plays, on every platform, is truncated to 16 bits before
Flower sees a byte of it. LibVLC decodes a 24-bit source at full precision and
then discards the precision at the handoff.

#### Would LibVLC 4 solve this instead? - checked, no

VLC master's `amem` *does* read the caller's format
(`var_InheritString(aout, "amem-format")`, matching against S16N/S32N/FL32), so
4.0 genuinely fixes the bit-depth ceiling. It is still not the route here:

- **It is not shippable.** As of September 2026 VLC 4.0 remains nightly/beta;
  the iOS 4.0 public beta (June 2026) runs on a pre-release libVLC 4.
  LibVLCSharp 4 previews are not published to NuGet.org at all - only to the
  `videolan-preview` feed on feedz.io. Stable everywhere is 3.10.0, which is
  what this repo pins. Shipping a music player on nightly native libraries
  across five platform heads is a larger risk than the thing it fixes.
- **It fixes one of three problems.** Bit depth only. It does not give Flower
  ownership of the stream I/O (see below), does not close the audio-over-TLS
  authentication gap `VlcCertificateDialogs` documents, and does not remove
  demuxer probing.
- **The migration is not free.** 3.0 -> 4.0 is a breaking API change across
  every platform head. That cost is comparable to putting a second
  implementation behind `ITrackDecoder` - which is exactly what the ffmpeg
  façade does, while fixing all three.

Waiting for 4.0 is therefore not a reason to defer this work, and adopting a
preview feed is not a shortcut to it.

#### The case is wider than hi-res

Scoped here as a hi-res feature, the façade turned out to also be the fix for a
streaming-reliability bug and an authentication gap. On 2026-09-03 no track
from one AAC album would stream to the phone at all - each produced no audio,
was reported as finished, and the queue raced through the album in twenty
seconds. Serving the same file over HTTP without range support reproduces it
exactly:

```
mp4 demux warning: MP4 plugin discarded (not seekable)
main demux debug: using demux module "avcodec"
```

VLC's mp4 demuxer refuses any stream it cannot seek, whatever the file's
layout, and desktop survives only by falling back to libavformat's demuxer -
which is to say, to FFmpeg. Flower already depends on FFmpeg for this; it just
depends on it through VLC, without being able to choose it, configure it, or
find out from it what went wrong.

Owning the I/O is what removes that whole class of problem. FFmpeg takes an
`AVIOContext` with read and seek callbacks, so the stream is seekable by
construction rather than by whatever the platform's HTTP access module decided,
and the fetching happens on Flower's own pinned `HttpClient` - which closes the
audio-TLS gap in the same change.

#### Step one, built: Flower fetches its own audio

Done. A remote track is no longer a URL handed to LibVLC; it is a
`SeekableHttpStream` (`Flower.Core/Services/`) that LibVLC reads through a
`HttpMediaInput` (`Flower/Audio/`). Range requests, a known length, a real
`Seek`, and every byte fetched by `PeerHttpClient` - which closes the
audio-over-TLS gap `VlcCertificateDialogs` describes in the same change.

This ships on the pinned stable LibVLC 3.10.0 and is not work thrown away when
the façade lands: FFmpeg's `AVIOContext` read/seek callbacks are
`HttpMediaInput`'s two methods with different signatures, reading from the same
stream class. Only the consumer changes.

It does **not** fix the bit-depth ceiling. Nothing short of leaving `amem`
does.

Three things were found by building it, each of which cost a wrong first
attempt:

- **`StreamMediaInput` cannot be used.** LibVLCSharp ships it and it wraps any
  `Stream`, but its constructor reads `Stream.CanSeek` - which for this stream
  means asking the server. Constructing one therefore puts a blocking HTTP
  round trip on whichever thread built the decoder, which for a
  double-clicked track is the UI thread. `HttpMediaInput` exists so that every
  network call happens inside a callback LibVLC invokes on its own reading
  thread.
- **LibVLC will not parse a callbacks-media at all.** `Media.Parse` answers
  `Skipped` whether asked with `ParseLocal`, `ParseNetwork` or both, because
  the media has no URI for it to judge - measured, not assumed. So a remote
  `PrepareAsync` asks the server directly instead (`TrackDecoder.ProbeRemoteAsync`).
  That is a weaker answer in one way (a reachable file in an undecodable
  container now passes prepare and fails later) and a stronger one in another
  (it separates "not answering" from "said no" cleanly, which is the
  distinction `DecodePrepareResult` exists for).
- **A read callback returning -1 is ignored.** The callback contract documents
  -1 as an error; LibVLC 3.0.x responds by calling the callback again
  immediately, and again - 61,760 times in a two-second test - while its WAV
  demuxer went on emitting the entire declared length of a track only 30% of
  which had arrived. So the error costs a hot loop and buys a fabricated tail.
  `HttpMediaInput.Read` returns a clean 0 and raises `Failed` instead, which
  `TrackDecoder` turns into `Faulted`: LibVLC is told the stream is over, the
  decoder is told why, and a cut-off track is not counted as one the listener
  heard. Verified: the same fixture now produces 172,768 bytes of PCM rather
  than the full 576,000.

- **Synchronous HttpClient does not exist on iOS.** The first version of
  `SeekableHttpStream` fetched bodies with `HttpClient.Send` and
  `HttpContent.ReadAsStream`. Both are green on desktop and both throw
  `PlatformNotSupportedException` on iOS: .NET's mobile `HttpClientHandler`
  has no synchronous path at all, whichever underlying handler is configured,
  so Flower.iOS's own `UseNativeHttpHandler=false` does not buy it back. The
  result was
  worse than the bug it was fixing: *every* streamed track on the phone failed
  instantly with "Operation is not supported on this platform", and the queue
  skipped whole albums in seconds. Every call is now the async one blocked on,
  which is safe because these run on a decoder's reading thread with no
  synchronization context. `SeekableHttpStreamTests`' fake handler now throws
  from `Send` exactly as a phone does, so a synchronous call cannot come back
  unnoticed.

  This is also what produced `Flower.DeviceChecks`, since split into
  `Flower.Tests.Decoding.Scenarios` (the portable checks) and the
  `Flower.Tests.Decoding.iOS`/`.Android` runners. The older name is kept
  below, where it is history. Two bugs in a row were
  invisible to a green desktop suite and were found by a person listening to a
  phone, which is not a test strategy. The checks decode a fixture from disk
  and over a loopback HTTP server and compare the result to the fixture's own
  samples, and they run on the iOS Simulator
  (`scripts/ios-device-checks.sh`) as the same code that runs here. Confirmed
  against this bug by putting `HttpClient.Send` back: the local check passes
  and every streamed one fails with "no audio came out at all" - the phone's
  reported symptom exactly. Android has no head yet.

A fourth is unresolved and needs the device: `TrackDecoder.DemuxHintFor` still
pins the MP4 family to `avformat`, which was the workaround for exactly the
non-seekability this change removes. It is kept because it is proven on the
phone and forcing a working demuxer costs nothing; it should be dropped once a
streamed m4a is confirmed playing and scrubbing on iOS through the stream path.

#### Step two, built: the decoder that is not LibVLC

Done, on macOS. `native/ffmpeg/` holds `ffaudio`, an eight-function C
façade over `avformat`/`avcodec`/`avutil`/`swresample`, and
`Flower/Audio/Ffmpeg/` holds the two managed layers over it: `FfmpegDecoder`
(one decode of one source, over a path or a `Stream`) and `FfmpegTrackDecoder`
(`ITrackDecoder`, so `GaplessCoordinator` drives it exactly as it drives the
LibVLC one).

**The ceiling is gone, and it is pinned by a test rather than by an argument.**
`FfmpegDecoderTests` decodes one 24-bit/96kHz fixture twice: asked for packed
S24 every sample comes back bit-identical to what was written, and asked for
S16 the low byte is gone from all of them. The fixture's ramp puts meaningful
data below bit 8 on purpose, because that loss is invisible against any
fixture that does not.

Three things about the shape, each of which removes a class of bug rather than
handling it:

- **FFmpeg pulls where LibVLC pushes.** `FfmpegTrackDecoder` owns its decode
  thread and asks for samples, so a seek is a function call that returns where
  it landed. `TrackDecoder` needs `_seekRequested`, `_seekAwaitingFirstSample`
  and an `OnFlush` that has to tell a seek's flush from an output-route
  change's, all to answer the same question after the fact. That correlation
  problem does not exist here.
- **The prepare means something stronger.** FFmpeg reads the container during
  the open, so `Ready` means decodable rather than merely reachable - which is
  the answer `DecodePrepareResult` always wanted and the callbacks-media path
  cannot give (see step one's second finding).
- **A packed 24-bit output is the façade's own work.** swresample has no
  packed S24, so it converts to S32 and the façade drops the empty low byte -
  which is lossless, because FFmpeg carries 24-bit PCM left-aligned in the
  32-bit container. That is `pack_s24`, and it is the one place where a
  mistake corrupts the heap rather than failing a test, hence the ASan build
  option.

One real bug was found by writing the tests rather than by reading the code:
retiring a decoder parked on backpressure counted a whole read buffer of
audio that had only partly reached the ring, pushing the reported position
past audio nobody would hear. `RetargetableRingWriter.Write` returns early on
both a retire and a seek, so the count now happens only when neither
interrupted it.

**What is not done, and must not be claimed:** only macOS is built. Linux
should follow from the same `pkg-config` path; Windows, Android and iOS each
need an FFmpeg cross-build first, statically linked into the façade on mobile
so one binary per ABI ships rather than five. And every shipping build must be
against an **LGPL-only** FFmpeg - MacPorts' and Homebrew's are GPL-enabled and
are development-only. `native/ffmpeg/README.md` carries the per-platform
status table and the licensing constraint in full.

#### Step three, built: a pipeline that can carry what the decoder delivers

> **Superseded (November 2026).** The pipeline now carries float from the
> decoder to `OutputStage`, and nothing below about negotiating or freezing its
> *sample format* holds any more: the device's format is `OutputStage`'s alone
> (`DeviceSampleFormat`), chosen again at every open. See
> Phase 6, step 2, above. Kept for the reasoning, which is
> still why 24 bits is the ceiling.

Moving the ceiling out of LibVLC was not the same as removing it.
`GaplessFormat.BytesPerSample` was `const int = 2`, and the ring buffer, the
feeder, the render callback, the position arithmetic and every fade computed
off it - so a 24-bit decode would have been narrowed one stage later by the
pipeline itself, and the only thing that changed would have been which
component did the truncating. `FfmpegDecoderTests` proved the decoder could
carry 24 bits; nothing proved the pipeline could, because it could not.

The canonical format is now negotiated once at startup and frozen for the
session, the same way the sample rate already was:

- **The decoder chooses it.** `DecoderElection.CanonicalFormatFor` maps LibVLC
  to S16 and ffaudio to S24. That direction is forced rather than
  preferred: amem hardcodes S16N and never reads back the fourcc it was asked
  for, so a pipeline carrying 24 bits over the LibVLC decoder would be carrying
  eight zeroes and calling it hi-res.
- **The device gets a veto, not a vote.** `MiniaudioSink.OpenDevice` asks for
  `ma_format_s24`, and a refusal narrows the pipeline back to S16 and re-opens.
  That is a real answer rather than a failure: every decoder produces S16 and
  every device takes it.
- **Frozen afterwards.** A decoder already open cannot change format, so a
  device change mid-session keeps the negotiated one - the same rule, and the
  same `_hasNegotiatedFormat` guard, the sample rate has always had.

`PcmSampleFormat` stops at S24 on purpose. `OutputStage` does its arithmetic in
float, and a float mantissa holds 24 bits exactly and no more, so S24 is the
widest integer format that survives the round trip bit-identically; a true S32
source would have its bottom eight bits eaten by the EQ and gain stage - a
widening that quietly narrows again. F32 would avoid that and buy nothing, as
every PCM source a music library contains is 16- or 24-bit integer.

`OutputStage`'s dither and clamp move with the format rather than staying at
S16 constants, which is most of what the widening is worth: the requantisation
noise floor drops by 48dB, and a clamp left at ±32767 would have hard-limited
every 24-bit sample above -48dBFS. `CanonicalFormatTests` holds both, plus the
packed-S24 sign extension - three bytes carry no sign bit in the 32-bit sense,
so a negative sample read without it comes back as full-scale positive noise.

**One thing this cost, deliberately - since paid back.**
`flower_audio_bridge`'s transport fade walked its buffer as `int16_t*`, so it
could not render packed 24-bit PCM: it would rewrite every sample as though
three-byte frames were two-byte ones. `MiniaudioSink` therefore refused the
bridge at S24 and fell back to the managed render callback. Unreachable when
written, because the bridge exists only on Android and iOS and neither had a
ffaudio artifact; gated anyway, because that coincidence would end the
moment the mobile cross-builds landed and the failure would be a native one on
the platforms hardest to debug on. It did end, and the envelope has since
learned the width - see "The bridge learns the sample format" below.

#### The decoder is now electable

`AppSettings.AudioDecoder` picks it, `FLOWER_DECODER` overrides that per run,
and `DecoderElection.Resolve` turns either into the decoder that will actually
run - falling back to LibVLC with a warning when `ffaudio` is not
loadable, which is the ordinary state on four of the five heads.

Hand-edited rather than given a picker in Settings, and not only because no UI
has been built: a picker would offer every listener a choice that resolves one
way everywhere but macOS. (That reason expired once all five heads had an
artifact; the setting stayed hand-edited anyway, for the reason the last section
of §5 gives.)

*(Since gone, all of it. LibVLC was removed, and with it the election:
`DecoderElection`, `AppSettings.AudioDecoder` and `FLOWER_DECODER` no longer
exist, and `FfmpegTrackDecoder` is the only decoder on every head. A head whose
`ffaudio` will not load now plays nothing, which is why the packaging script
refuses an installer without it.)*

Verified end to end on macOS: a 24-bit/48kHz source decoded through
`FfmpegTrackDecoder` reaches the shared ring bit-identically, with 95,624 of
96,000 low bytes non-zero - the sub-16-bit content is genuinely present rather
than zero-padded.

`TrackDecoder` is still the default, and stays the default until FFmpeg has
listening hours behind it. *(It has them now - see "FFmpeg becomes the
default" at the end.)*

#### What electing it broke, and what that says about the checks

The first real library it met was a self-hosted server over the LAN, and a
track would not open: *"Failed to find two consecutive MPEG audio frames"*,
`Invalid data found when processing input`. Every test in the repo was green,
including `Flower.DeviceChecks`, whose entire reason for existing is to answer
"does this platform actually turn a track into the right audio?" - because
`DecodeChecks` named `TrackDecoder` in its constructor calls. Electing a
different decoder moved open, probe, range requests, seek and fault out from
under all 42 of them at once, and nothing said so.

So the suite now runs once per decoder the platform has (`DecoderUnderTest`),
which is the standing rule this initiative should be held to: a decoder nobody
checks is a decoder nobody has checked. Each subject is handed its own sample
format rather than the run moving `GaplessFormat`'s process-wide one - which
also closes the follow-up recorded here earlier, since the format is now
injected at the point that actually needed it (`FfmpegTrackDecoder`'s
constructor) rather than read from a static mid-decode.

Running them found four things:

1. **The demuxer hint was being forced, not preferred.** `DemuxerHintFor`
   listed mp3, flac and wav alongside mp4, on the reasoning that skipping the
   probe saves a round trip on a remote track. But the extension comes from
   the *catalog* (`Child.Suffix`), which describes a file on a server's disk -
   not the bytes on the wire, which a server is free to transcode, and not
   necessarily right in the first place. Forcing a demuxer discards FFmpeg's
   probe entirely, so a wrong suffix is not a slow open, it is a track that
   never plays. `TrackDecoder.DemuxHintFor` already carried this conclusion in
   its own comment - "forcing the wrong demuxer is worse than probing" - and
   this is that lesson learned twice. The hint is now MP4-only, and even that
   one falls back to probing (`FfmpegDecoder.OpenStream` rewinds and retries),
   with a warning logged so a mislabelled track is still *reported* rather than
   silently rescued.
2. **`SyntheticWav` followed the canonical format.** It is a *source file*
   generator, but it sized its frames from `GaplessFormat.BytesPerFrame` - so
   the moment that could be something other than S16 it wrote a 24-bit header
   over 16-bit samples, and every byte-exact check read corrupt audio. Pinned
   to 16 bits, which is what its `Func<int, short>` said all along.
3. **The cut-stream check passed vacuously for any decoder.** It called
   `StartDecoding()` without preparing first, and starting an unprepared
   decoder also faults - so the check was satisfied without a byte of the
   stream having been read.
4. **Two checks raced under load.** The seek check let the decoder finish the
   whole track before seeking it (a 10s WAV off a loopback socket decodes in
   a fraction of a second), so the seek arrived at a thread that had already
   exited; it now holds the decoder on backpressure with a ring smaller than
   the track, which is also the state it is in during real playback. The cut
   check snapshotted the drain the instant `Faulted` fired, racing the last
   write. Both only failed in the full suite, on a busy machine.

None of those four is the field failure's root cause with certainty - that one
is (1) if the server sent bytes that were not what the catalog said, which is
what the error message describes. The check that would have caught it now
exists per format and per decoder: *"decodes when the catalog has the wrong
extension for it"*, which fails 10 ways against the old hint table.

**Direct-mode format policy:** choose the track's native sample rate, bit depth
and channel layout when the selected output device accepts that exact format in
an exclusive/native path. Do not upsample merely because the device advertises
a higher rate: that is a conversion, not passthrough. If the device rejects the
native format, choose a supported output format and make one high-quality
conversion at the output boundary. Device capabilities are discovered by
attempting to open the exact miniaudio configuration, not from the current
name/id-only picker. A successful *shared-mode* open is not proof of
bit-perfect delivery—WASAPI shared mode mixes in float, and mobile/managed
routes may be converted by the OS. Windows can request miniaudio's exclusive
WASAPI mode; macOS, Linux, Android and iOS each require platform-specific
hardware-path verification before Flower can claim bit-perfect output.

**Switching policy:** normal mode retains its session format across a device
change and continues uninterrupted, as it does today. Direct mode is allowed a
short intentional interruption: stop the callback, flush/retire current and
armed decoders, open the new device in its selected format, recreate the
decoder/ring, seek to the measured playback position, prebuffer, and resume.
The same transition is required at a track boundary whose native formats differ;
sample-accurate gapless remains available only for adjacent tracks sharing the
selected output format.

**Bit-perfect boundary:** direct mode bypasses Flower's EQ, software gain,
dither and any crossfade. Enabling one of those features opts into the managed
float/DSP path and therefore is not bit-perfect. DSD is still out of scope for
this decoder path: it must be explicitly converted to PCM, not represented as
24-bit passthrough.

A second round, after the first fix still played nothing on a Mac. The log said
`StartDecoding() ... without a successful prepare` on every track, and the one
track that did open was `Armed=`, decoding 17MB into a staging ring while
`Current=null` and the sink rendered silence.

`GaplessCoordinator` calls `PrepareAsync` on one path only - decode-ahead.
`Play()`, which is what pressing play reaches, constructs a decoder and calls
`StartDecoding()` on it. `TrackDecoder` supports that because its open *is*
`StartDecoding` (`EnsureMedia` + `MediaPlayer.Play`); prepare is a parse it can
skip. `FfmpegTrackDecoder` put the whole open in `PrepareAsync`, so it faulted
instantly on every press of play, and only a track lucky enough to be armed
ahead of time ever opened. The golden path was the one path that never worked.

Two things follow. `StartDecoding` opens on its own decode thread when no
prepare has happened - it runs under the coordinator's lock, so it cannot open a
remote track inline. And the checks now include `plays the way pressing play
plays it`: the same streamed decode with no prepare, per fixture per decoder.
Against the faulting version it fails ten times on FFmpeg and passes on LibVLC,
which is the whole asymmetry in one line.

The lesson generalises past this decoder. `ITrackDecoder` documents what each
method returns and says nothing about which are mandatory, so "prepare is
optional" lived only in `TrackDecoder`'s implementation - and every check here
called both in the same order, which is exactly the order that hides the
question.

#### Linux, and taking CI out of the same blind spot

Both of those bugs happened on the one platform where the façade is built, and
were caught by a person listening. That is not a coincidence: `ffaudio`
was built on macOS only, so `DecoderElection` fell back to LibVLC everywhere
else, `FfmpegDecoder.IsAvailable` was false on every CI runner, and the FFmpeg
half of the checks did not exist on CI at all. The decoder with two outright
playback bugs in it was the one nothing automated had ever run.

Linux is now built the same way macOS is - `native/ffmpeg/linux/build.sh`, the
same `CMakeLists.txt`, FFmpeg found through `pkg-config`. Written but not yet
run on a Linux machine: there is no Linux here and no container runtime, so
CI's first run is the thing that proves it rather than a local build. The one thing in the
way was a version floor of FFmpeg 7 that nothing needed: the newest APIs
`ffaudio.c` uses are `AVChannelLayout` and `swr_alloc_set_opts2`, both
5.1, and 7 was simply the version of the machine it was first built on. Ubuntu
24.04 ships 6, so that accident was the difference between a distro FFmpeg
being found and not.

Both CI jobs now build the façade on Linux and macOS, so the `RequiresFfmpeg`
tests and the FFmpeg decode checks run on two platforms per push instead of
zero. Windows still has no façade and filters those tests out by name - which
stopped being true later, see "Windows, by not building FFmpeg at all".

The guard matters as much as the build. The checks loop over the decoders that
loaded, so a façade that stopped building would not fail - it would shorten the
suite, and the run would stay green having checked half of what it did the day
before. `FLOWER_REQUIRE_DECODERS` names what a caller expects the platform to
have and turns a missing one into a failing check. It is unset on a phone,
where a decoder's absence is a true fact about the platform, and set on CI,
where it never is.

#### The intermittent one, and why its message was useless

`LibVLC: FLAC at 44.1kHz decodes when streamed from a server that refuses
ranges` failed once in three full-suite runs on this Mac - 283,660 bytes of an
expected 384,000, so 523ms of a two-second track never arrived - and passed on
the other two. It is the LibVLC path, which nothing in this section touches.

It has not been reproduced. Thirty runs of that check alone, twenty-five more
with twenty spinning cores against it, and four further full-suite runs are all
green, so what follows is what was ruled out rather than a fix.

What the investigation did find is that the check could not have told anyone
what happened. `DecodeFully` wired `Drained` and `Faulted` to the same latch and
recorded nothing about which fired, so three unrelated events arrived at the
length oracle as one number:

- the decode faulted mid-stream and stopped,
- the decode ended cleanly but short,
- the decode was complete and the *reader* lost the tail.

"523ms off" is true of all three and distinguishes none of them, and two of them
are not about audio at all. Each now reports itself: a fault says it faulted and
how many bytes it had produced, and a shortfall between what the decoder counted
and what the drain collected says exactly that.

The third of those is also now impossible rather than merely reported. `Settle`
waited for the collection to stop growing, which is a guess from the outside: two
samples 20ms apart look identical whether the ring is empty or the drain thread
simply was not scheduled between them. It now waits for the decoder's own
`BytesProduced`, which is an exact finish line - the decoder increments it after
each write - with quiescence kept only as the fallback for a decoder that
faulted part-way through a write and left a target nothing will ever reach.

That was the leading theory, and measuring it is what killed it: the ring holds
a steady 22,880 bytes - 119ms - at the moment `Drained` fires on this fixture,
so a starved drain cannot lose 523ms of audio, because 523ms of audio is never
in there. Two other theories died the same way. Cutting the body mid-track does
not truncate anything (`SeekableHttpStream` reopens from zero and skips forward,
and the decode comes back whole), and LibVLC issues no seek at all on this path,
so `HttpMediaInput.Seek` returning false against a server that refuses ranges -
which would make LibVLC abandon the demuxer - never happens here.

So the cause is still open, and the value of the change is that the next
occurrence will name it instead of being a number three things could produce.
That matters more on CI than here: the run that sees this next is the one that
cannot be attached to a debugger, and a disagreement between two platforms is
the entire signal this suite exists to carry.

#### iOS, where the decoder has to bring its own FFmpeg

The difference between this and the desktop builds is that there is nothing to
find. macOS and Linux ask `pkg-config` for an FFmpeg somebody else installed; a
phone has no package manager, so `native/ffmpeg/ios/build-ffmpeg.sh`
cross-compiles FFmpeg itself for device arm64 and Apple Silicon simulator
arm64, and `ios/build.sh` links it into `ffaudio.framework` - one
framework per slice, checked in beside the platform head exactly as
`native/miniaudio/` does. About 1.9MB a slice, which is what
`--disable-everything` plus the decoders and demuxers a music library is
actually made of costs.

That static link is also where the LGPL stops being somebody else's problem. On
desktop the FFmpeg is a distro or MacPorts build, replaceable by whoever
installed it; here it is inside the app, so the configure line is the
compliance. No `--enable-gpl`, no `--enable-nonfree`, and `config.h` says
`CONFIG_GPL 0` - which is the thing to re-check after any change to that
script, because nothing else will.

The narrow ABI needed defending in a way it did not on desktop.
`CMAKE_C_VISIBILITY_PRESET hidden` cannot reach inside a static archive, so a
framework built the obvious way re-exports all of FFmpeg - the second route to
FFmpeg that this façade exists in order not to have. The build derives an
export list from the `FFAUDIO_API` lines in the header and ends by printing
anything else that escaped.

**The bug this found is the one worth writing down.** With the framework
embedded, signed, loadable and exporting `ffaudio_abi_version`, the first
simulator run reported 70 passed, 0 failed - LibVLC only, with no FFmpeg checks
in it at all and nothing saying so. `FfmpegNative` registered its
`DllImportResolver` in its own static constructor, which works on every desktop
runtime and cannot work here: a P/Invoke has no body for a type initializer to
run in front of, and Mono on iOS resolves the library for the stub *before*
running the declaring type's cctor. The resolver was being registered by the
very call that had already thrown `DllNotFoundException`. It is a
`[ModuleInitializer]` now, which has no such ordering to get wrong.

And the shape of that failure is the argument for the guard, restated: an
absent decoder shortened the run instead of failing it, and the tally was green.
`scripts/ios-device-checks.sh` now passes `FLOWER_REQUIRE_DECODERS=LibVLC,FFmpeg`
into the simulator, so on iOS - which has a built façade as of this change - a
missing FFmpeg is a failing check rather than half a suite. With that fixed the
run is 140 passed, 0 failed: every fixture, every streaming shape, both
decoders, on the iOS runtime.

The device slice is not simulator-only by inference: it was installed on a
phone through `Flower.iOS/deploy.sh` with `FLOWER_DECODER=ffmpeg`, and the app
played through the FFmpeg decoder there. That is the run that matters, since
the simulator shares this Mac's arm64 and its dynamic loader is not iOS's.

#### Android, the same shape with a different compiler

Android needed the identical two-step - `android/build-ffmpeg.sh` cross-compiles
FFmpeg for `arm64-v8a`, `armeabi-v7a` and `x86_64` with the NDK's clang,
`android/build.sh` links it into `libffaudio.so` per ABI - and the same
two things are load-bearing for the same reasons: an LGPL-only configure line,
because an APK links FFmpeg in and has nothing to point at, and an export
narrowing, spelled as an ELF version script here instead of an
`-exported_symbols_list`. Both are derived from the header's `FFAUDIO_API`
lines, so the eight-function ABI is described once and defended twice.

Two things were easier than iOS and one was harder. Easier: no
`DllImportResolver` branch, because Android's loader resolves
`DllImport("ffaudio")` to `libffaudio.so` in the APK unassisted -
the same reason `libminiaudio.so` needs no help - and the `[ModuleInitializer]`
fix iOS forced was already in place. Harder: the export check initially passed
by reading nothing. `llvm-nm` on a stripped `.so` reports "no symbols", because
the strip took the symtab and left only the dynamic table, so the check that
exists to catch all of FFmpeg leaking would have waved anything through.
`--dynamic` is what makes it a check.

What is not proven is the part iOS could prove. There is no Android
device-checks head, so nothing here decodes a fixture and compares samples on
Android the way `scripts/ios-device-checks.sh` does - the evidence is that the
libraries build, export eight symbols, and package into the APK. An Android
head for `Flower.DeviceChecks` is the missing piece, and it is the same
argument this whole directory was created by: both streaming bugs so far were
found by a person listening to a phone.

*(Since built, in a different shape. `Flower.DeviceChecks` gave way to the
phone decoding runners, and `Tests/Flower.Tests.Decoding.Android`, run by
`scripts/android-decoding-tests.sh`, decodes every fixture on Android and
compares samples. It also holds the C output stage to `OutputStage` byte for
byte. It passes all 119 checks on the arm64 emulator, and runs in CI on an
x86_64 one. CI once ran it and failed on `XA4301`, because a Linux host's
desktop `libffaudio.so` won `lib/x86_64/`; both Android heads now exclude that
payload's native assets (Tests/README.md, "Android"). One gap remains: it has
never run on a real Android phone, nor has anyone listened to one.)*

That also made `MiniaudioSink`'s S24 bridge gate live rather than theoretical:
electing FFmpeg on either phone fell back to the managed render callback and
gave up the GC-pause resilience the native bridge was built for. Correct, and
audibly fine, but a real cost that arrived with these builds rather than a
hypothetical one. It is fixed two sections below.

What this does not do is make FFmpeg cross-platform. Windows still needs an
FFmpeg build and an import-lib route, and neither mobile head has a decode
check running on real hardware. Until then the decoder is macOS, Linux, iOS and
Android, honestly labelled as such in `native/ffmpeg/README.md`'s table, and
`TrackDecoder` remains the default. *(Windows has its build now, and the
default has since moved - see the last section of §5, which is also honest about
which of those checks still do not exist.)*

#### Windows, by not building FFmpeg at all

The fifth head took the opposite decision to the other four: it downloads its
FFmpeg. Windows is the only platform with nothing to find - no distro package,
no MacPorts, and no `pkg-config` to ask either - but it is also the only one
where cross-compiling buys nothing, because FFmpeg publishes Windows builds and
the **LGPL** variant of them is configured exactly as Flower needs (no
`--enable-gpl`, no `--enable-nonfree`, verified in the configuration string
baked into the DLLs) and ships the libraries as separate, replaceable DLLs -
the same shape the obligation already takes on macOS and Linux. Spending tens
of minutes of every CI run reproducing that would be ceremony, not rigour.
`windows/build.ps1` therefore fetches a pinned, checksummed autobuild, and
`CMakeLists.txt` grows an MSVC branch that takes a prefix through
`FFAUDIO_PREFIX` instead of asking pkg-config. The header already said
`__declspec(dllexport)`, and the façade's C is portable enough that MSVC needed
nothing else.

Five DLLs come out rather than one - the façade plus the four it imports - and
that is the interesting risk here. On every other desktop FFmpeg lives somewhere
the loader already knows; on Windows it is four loose files next to the façade,
resolved because `FfmpegNative.Resolve` loads by full path and Windows then
searches that file's own directory. "Built, and would not load" is a real
outcome rather than a formality, which is why `decode-checks` now requires
`LibVLC,FFmpeg` on all three desktops: it makes that a red run instead of half
a suite. The `test` job stops filtering `RequiresFfmpeg` out on Windows for the
same reason.

None of this has run on a Windows machine. There is no Windows machine here,
so unlike every other platform in this section the first CI run is not the
confirmation of a local build - it is the only evidence there has ever been.
And a packaged Windows app still has nowhere to get those DLLs from: the build
puts them in `native/ffmpeg/artifacts/windows/`, and copying them into a
publish output is work nobody has done.

#### The bridge learns the sample format

The last thing electing FFmpeg cost. `MiniaudioSink` refused the native audio
bridge whenever the pipeline was S24, so on Android and iOS - the only two
platforms that have a bridge at all - the choice was 24 bits *or* a render
callback Mono's GC cannot suspend, and those are the two platforms where the
second one is not a nicety. The gate was right while it stood: the envelope
walked its buffer as `int16_t*`, and packed 24-bit PCM through a 16-bit walk is
not a skipped fade, it is every sample reinterpreted and then scaled.

`flower_audio_bridge_create` now takes a `bytesPerSample` alongside
`bytesPerFrame`, and `flower_audio_bridge_apply_envelope` branches on it -
`int16_t` as before, or a sign-extending read/scale/write of packed
little-endian three-byte samples. The width is passed rather than derived,
because a ring of interleaved frames with no header cannot tell stereo S24 from
three channels of S16, and `create` refuses any other width outright: a format
the bridge cannot fade is a format it must not render, and a NULL at startup is
a log line where a silent fallback would be noise on a phone.

The arithmetic was checked by compiling `impl.c` on the host and driving the
envelope directly - full-scale positive and negative S24 at a held gain, a
fade-in ramping monotonically without wrapping, a fade-out reaching zero and
setting `fadeOutCompleted` - because the shipped paths are cross-compiled for
hardware no test here runs on. That is a harness, not a suite; it is not
checked in, and the standing gap is still an Android device-checks head.
*(Both gaps since closed: the envelope and the rest of the output stage are
checked byte for byte by `NativeOutputStageChecks` on both phone runners and
by `NativeOutputStageTests` on each desktop, and Android has its runner - see
the note under "Android, the same shape with a different compiler".)*

Both vendored miniaudio binaries were rebuilt for the new signature:
`Flower.Android/libs/<abi>/libminiaudio.so` and
`Flower.iOS/Frameworks/ios-{device,simulator}/miniaudio.framework`. They are
the ABI the managed `DllImport` talks to, so a rebuild is not optional
housekeeping - a stale one would take the third argument as whatever happened
to be in the register.

#### FFmpeg becomes the default

`AppSettings.AudioDecoder` now starts at `Ffmpeg` rather than `LibVlc`, which is
the point everything above was building toward. The argument for flipping it is
not that FFmpeg is better in general - it is that the alternative truncates
every track to 16 bits before Flower sees a byte, so a default of LibVLC is a
default of a ceiling nobody chose and no setting most listeners will ever open
removes. A decoder that is only reached by hand-editing a JSON file is a
decoder nobody uses.

Three things make that a change rather than a gamble.

**The fallback is real.** `DecoderElection.Resolve` returns `LibVlc` with a
logged warning when `ffaudio` will not load, so a head whose artifact is
missing or broken plays anyway, one bit-depth poorer. Defaulting to FFmpeg
cannot produce silence on a platform that has no façade; it produces the
previous behaviour and a log line.

**The escape hatch does not need an edit.** `FLOWER_DECODER=libvlc` overrides
the setting for one run, which is how a bad session is recovered without
touching `settings.json` - the reason that variable exists, and the reason a
default flip is cheap to undo per-run.

*(Neither survives: the fallback and the escape hatch both went when LibVLC
was removed, so there is nothing left to fall back to or elect. What stands
behind each head now is its own evidence, below and in the note after the
table.)*

**The bridge no longer charges for it.** Until the previous section, electing
FFmpeg on a phone traded the native render callback for 24 bits. Flipping the
default while that gate stood would have made every phone pay a GC-pause cost
for a bit depth it never asked for.

What the flip does *not* do is invent evidence. Honestly, per head:

| Head | What backs the default |
|---|---|
| macOS | Elected in real listening for weeks; all decode checks, both decoders |
| iOS | 142 decode checks on the simulator, both decoders; elected on a physical device |
| Linux | CI only - `decode-checks` runs it under `FLOWER_REQUIRE_DECODERS`. Never built or listened to on a Linux machine here |
| Windows | CI only, same job. Never built or listened to on a Windows machine here |
| Android | **Nothing.** The libraries build, export their eight symbols and package into the APK. No fixture has ever been decoded and compared |

Android is the one that matters, and it is the one place the fallback does not
help: `libffaudio.so` loads, so the election succeeds, so a decoder nobody
has heard is the one every Android launch now uses. That is a deliberate
acceptance rather than an oversight - there are no released users, and
`FLOWER_DECODER=libvlc` is one environment variable away - but it makes the
Android `Flower.DeviceChecks` head go from "the missing piece" to the thing
standing between this default and a phone playing nothing. It is the next piece
of work in this initiative.

One mechanical consequence worth knowing: `settings.json` is written whole on
every save, so an existing file still says whatever it said. A machine that has
run the app before keeps LibVLC until its `"AudioDecoder"` key is changed by
hand. The default only reaches a fresh install.

**Where the table stands now (November 2026).** The default is no longer a
default, because there is no other decoder. Android's row is no longer
"Nothing": its decoding runner passes all 119 checks on the emulator, locally
and in CI (see the note under "Android, the same shape with a different
compiler"). One gap remains: nobody has played anything on a real Android
phone, which is now the next piece of work in this initiative. Linux and
Windows are still CI-only. A phone deploy
is now `scripts/deploy-ios-device.sh`, which replaced `Flower.iOS/deploy.sh`.

### 6. True sample-accurate gapless — Done

Built, and not the way this section sketched. The plan was a custom PCM pipeline
hung off LibVLC's `SetAudioCallbacks`/`SetAudioFormat`, folded into
`AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's `AVAudioEngine` bridge so it wouldn't be
built twice. What shipped needs neither: LibVLC decodes, and **every** platform
renders through `MiniaudioSink`, so there is exactly one render path to be
gapless on and no Apple-specific bridge to wait for. The "don't build this
twice" warning was answered by deleting the second path, not by sharing it.

The pipeline (`Flower/Audio/`):

- `GaplessFormat` — one canonical PCM format (S16N/native-session-rate/stereo)
  every track is decoded to, so a track boundary is never a format change and
  the render sink never reconfigures mid-stream. S16N specifically, because LibVLC 3.0.x's
  `amem` module hardcodes it (`char format[5] = "S16N";`, with a literal
  `/* TODO: amem-format */` beside it) and silently ignores the requested
  fourcc - measured, not inferred; see #5's "The 16-bit ceiling, measured".
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

### Instrumentation

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
