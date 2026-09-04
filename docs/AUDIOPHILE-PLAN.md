# Audiophile Playback Features Plan

Five requested features: gapless playback, multi-channel/hi-res sample rate support (24-bit/192kHz+), DSD/APE format support, a low-latency playback engine, and EQ with true bypass. Output-device selection itself is `AIRPLAY-BLUETOOTH-PLAN.md` territory, referenced here only where a feature depends on it. **#1 (EQ) and #6 (true sample-accurate gapless) are done. #4 was superseded by #6 and will never be built. #2, #3 and #5 are open, and all three are scoped against a render path that no longer exists** — see the note below.

## A note on what is stale here

Items #2 and #5 below are written against LibVLC as the *output* stage — its
`aout` modules, its `file-caching`, its device selection. That stopped being
true. LibVLC is decode-only now: every platform renders through `MiniaudioSink`
pulling canonical S16/native-rate/stereo PCM out of `GaplessRingBuffer`. Neither item is
wrong about *what the user wants*; both are wrong about where the knob lives, so
each needs re-scoping against miniaudio before it is actionable. #1 already hit
this and was re-scoped when it was built; #6 hit it and was built somewhere else
entirely.

## Key findings

- Historical baseline: one plain `VlcAudioManager` — no equalizer, no preloading, no output-format config. Auto-advance only started the next track after `EndReached`, so the gap included full demux/codec-open latency.
- Confirmed in installed `LibVLCSharp.dll` (3.10.0): `SetEqualizer(null)`/`UnsetEqualizer()` is a **true bypass**, not a flat 0dB filter. `SetAudioCallbacks`/`SetAudioFormat` exist (same seam `AIRPLAY-BLUETOOTH-PLAN.md` uses). No independent "pass multichannel through untouched" toggle exists in the API. **No longer the implementation path for #1 below** — this API applied to `LibVlcRawStreamSink`'s render `MediaPlayer`, which every platform's render path has since moved off (see `MiniaudioSink`'s own class comment); the finding itself still stands as a fact about LibVLC, just not one anything in this codebase calls anymore.
- Confirmed in `TagLibSharp.dll` (2.3.0): `.ape` and `.dsf` tag reading works today via the existing `TagLib.File.Create` call; `.dff` (DSDIFF) is not supported by this version.
- **Confirmed by inspecting the installed macOS VLC's native plugin directory: no Monkey's Audio or DSD demux/decode plugins exist.** Mainline VLC does not ship native `.ape`/`.dsf` playback support — a real gap, not an assumption. Android/iOS's LibVLC NuGets haven't been checked yet and could be in the same position.

## 1. EQ with true bypass — Done

Implemented as a 10-band graphic EQ, not against LibVLC (see above) — LibVLC is decode-only now, and every platform renders through `MiniaudioSink`, a plain `ma_device` pulling raw PCM from `GaplessRingBuffer`. The EQ is a pure-C# RBJ-cookbook peaking/bell biquad cascade (`Equalizer`/`EqualizerSettings`, `Flower/Audio/`), spliced directly into `MiniaudioSink.DataCallback` after the ring read. `IAudioSink.ApplyEqualizer(Equalizer? equalizer)` carries a rebuilt-and-atomically-swapped processor down from `IAudioManager`/`GaplessAudioManager`; passing `null` is **true bypass** — `DataCallback` skips the processing call entirely rather than running an all-zero-dB filter, preserving this section's original bypass requirement under the new render pipeline.

Fixed at 10 bands (31Hz–16kHz, ISO-ish spacing, `Q≈1.41`), ±12dB per band, plus a preamp stage applied before the cascade — no presets, no parametric (frequency/Q) adjustment; out of scope for what was asked. `EqualizerSettings` (enabled flag, preamp, 10 band gains) persists via `AppSettings.EqualizerSettings`/`AppSettingsStore`, and is eagerly re-applied at startup in `App.axaml.cs` — not only when the Equalizer window happens to be opened. UI: `EqualizerWindow`/`EqualizerViewModel`, reachable via **View → Equalizer…**, live-apply with no "Apply" button. A settings change still rebuilds the whole processor (fresh coefficients and fresh filter delay-line state together), but that is no longer heard: `OutputStage` crossfades from the outgoing filter's output to the incoming one's across a single render callback. The "accepted minor transient click" this section used to record is gone — see `AUDIO-QUALITY-PLAN.md`, which also moved the EQ off S16 (it used to round and hard-clamp to 16-bit mid-chain, so any positive band gain clipped outright) onto the float path that requantises once, at the end, with dither.

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

**The bounded double-resample slice is done.** At sink initialization, `MiniaudioSink.OpenDevice` asks miniaudio for the endpoint's native rate, then configures `GaplessFormat` before either decoder is constructed. A 44.1kHz source on a 44.1kHz endpoint is consequently converted once by LibVLC, rather than first to fixed 48kHz and then again by miniaudio's linear resampler. The fallback remains 48kHz for headless/test sinks.

The session rate is deliberately held across later output-device changes: changing it while a current or armed decoder exists would corrupt timing and playback speed. If the newly selected endpoint differs, miniaudio can resample for that switch. Full per-device native-rate renegotiation belongs with the wider format/passthrough redesign below.

`AIRPLAY-BLUETOOTH-PLAN.md` Phase 1's picker gives desktop direct mode an
explicit endpoint; iOS uses the system route picker instead. The current
name/id-only device list does not expose format capabilities, so the direct
path must add an exact-format probe and retain the normal shared-mode fallback.

### Decoder/backend spike — complete

**Finding:** `TrackDecoder` cannot be the hi-res decoder. Its one LibVLC `amem`
path calls `SetAudioFormat("S16N", ...)`; LibVLC decodes a 24-bit source but
hands Flower S16 samples. No downstream device capability can recover the lost
precision. The existing 96kHz/24-bit PCM probe confirms the alternative is
real: FFmpeg reports the file as `pcm_s24le`, `sample_fmt=s32`,
`sample_rate=96000`, and `bits_per_raw_sample=24`. FFmpeg represents 24-bit
PCM in a 32-bit integer container, with the eight low bits empty; packing that
to miniaudio's tightly-packed `ma_format_s24` preserves every source value.

**Decision:** add a small, owned `flower-ffmpeg` native façade, linked only to
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

### The 16-bit ceiling, measured

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

### Would LibVLC 4 solve this instead? - checked, no

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

### The case is wider than hi-res

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

### Step one, built: Flower fetches its own audio

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

  This is also what produced `Flower.DeviceChecks`. Two bugs in a row were
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

### Step two, built: the decoder that is not LibVLC

Done, on macOS. `native/ffmpeg/` holds `flower-ffmpeg`, an eight-function C
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

Nothing routes through this decoder yet. `TrackDecoder` remains the decoder
the app uses; `FfmpegTrackDecoder` is selectable behind `ITrackDecoder` and is
what direct mode will elect once the format policy below is built and the
remaining platforms are proven.

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

## 6. True sample-accurate gapless — Done

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
   the same re-scope as #2. The S16/native-session-rate format is a deliberate
   gapless requirement; the completed decoder spike defines the separate
   format-aware direct path needed for hi-res output and the controlled
   transitions it requires.

   Its first two steps are **done**: Flower fetches its own audio (see #5's
   "Step one, built"), and the `flower-ffmpeg` façade plus `FfmpegTrackDecoder`
   exist and are proven on macOS to deliver a 24-bit source intact (see "Step
   two, built"). What remains is the part that is not a decoder: the four
   unbuilt platform artifacts, an LGPL-only FFmpeg to build them against, and
   the direct-mode format policy below - device capability probing, the
   controlled transitions, and the switch that actually elects this decoder.
