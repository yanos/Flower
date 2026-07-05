# Audiophile Playback Features Plan

Source: investigation into five specific audiophile-oriented features requested
for Flower: **gapless playback**, **multi-channel/hi-res sample rate support
(24-bit/192kHz+)**, **additional high-res formats (DSD, APE — FLAC/ALAC/WAV are
already imported today and out of scope)**, **a low-latency/minimal-overhead
playback engine**, and **an EQ with a true bypass mode**. Scope excludes
anything already covered by other plans — bit-perfect/exclusive-mode *output
device* selection is the existing `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1/2 territory
and is only referenced here where a feature below directly depends on it.

## Research summary (why this is shaped the way it is)

- Today's playback stack is a single `VlcAudioManager`
  (`Flower/Manager/VlcAudioManager.cs`) wrapping one plain LibVLCSharp
  `MediaPlayer` with **no** equalizer, no preloading, and no output-format
  configuration — `new LibVLC()` and `new MediaPlayer(_libVLC)` with zero
  options (`VlcAudioManager.cs:50-51`). Every `Play()` call
  (`VlcAudioManager.cs:61-69`) constructs a brand-new `Media` and hands it
  straight to `_mediaPlayer.Play(media)`.
- Auto-advance (`PlaylistControlViewModel.cs:102-117`) only starts the next
  track *after* `EndReached` fires — i.e. after the current track has fully
  stopped — via `Dispatcher.UIThread.Post(() => Play(nextTrack))`. There is no
  preloading of the next track and no attempt to overlap the two, so today's
  gap is at least: event dispatch latency + `Play()`'s own demux/codec-open
  latency for a freshly constructed `Media`.
- Confirmed by inspecting the installed `LibVLCSharp.dll` (3.10.0,
  `~/.nuget/packages/libvlcsharp/3.10.0/lib/net8.0/`) directly rather than
  assuming API shape:
  - `Equalizer` is a full managed type (`libvlc_audio_equalizer_new`,
    `_new_from_preset`, `SetAmp`, `SetPreamp`, `get_BandCount`,
    `get_PresetCount`, `GetPresetName`, `GetBandFrequency`), and
    `MediaPlayer.SetEqualizer`/`UnsetEqualizer` both exist
    (`libvlc_media_player_set_equalizer(mp, NULL)` under `UnsetEqualizer`/
    `SetEqualizer(null)`) — **passing null is a true bypass**, not a
    flat/0 dB filter still running in the chain. This is good news: the "fully
    disabled" requirement has an exact, already-available mechanism.
  - `MediaPlayer.SetAudioCallbacks`/`SetAudioFormat` also already exist (same
    finding `AIRPLAY-BLUETOOTH-PLAN.md` relies on) — this is the seam any
    sample-accurate gapless or hard bit-perfect work below would use.
  - `libvlc_audio_set_channel`/`AudioOutputChannel` only expose
    Stereo/RStereo/Left/Right/Dolby-style folding — **there is no "pass
    multichannel through untouched" toggle in the managed API**. Multichannel
    behavior is whatever the aout module + device negotiate, not something
    Flower can independently force via LibVLCSharp today.
  - No `DSD`/`Dsf`/`Dff` strings anywhere in `LibVLCSharp.dll` — expected,
    since DSD handling lives entirely in native libvlc demux/codec plugins,
    invisible to the C# layer.
- Confirmed by inspecting the installed `TagLibSharp.dll` (2.3.0,
  `~/.nuget/packages/taglibsharp/2.3.0/lib/netstandard2.0/`): it has
  `TagLib.Ape` (Monkey's Audio) and `TagLib.Dsf` (DSD Stream File) namespaces,
  but **no** `TagLib.Dsdiff`/`Dff` — i.e. tag reading for `.ape` and `.dsf` is
  already available for free via the same `TagLib.File.Create(file)` call
  `Importer.cs:54` already uses; the *other* common DSD container, `.dff`
  (Philips/Sony DSDIFF), is not supported by this TagLib# version at all.
- Whether LibVLC can actually *play* `.ape`/`.dsf` depends entirely on which
  native demux/codec plugins are bundled in each platform's libvlc binaries —
  this can't be confirmed by inspecting managed DLLs, so it was checked
  directly against the actual installed native plugin set: `ls
  /Applications/VLC.app/Contents/MacOS/plugins/` (the exact binaries
  `VlcNativeSetup.cs` loads on macOS). **Correction to an earlier assumption
  in this doc: there is no Monkey's Audio and no DSD plugin in that list at
  all** — no `libape`/`libmonkey`-anything, no `libdsf`/`libdsd`-anything.
  Mainline VLC does **not**, in fact, ship native `.ape`/`.dsf` support today,
  at least not in this installed build. This is a real gap, not just an
  unverified assumption — the `VideoLAN.LibVLC.{Windows,Android,iOS}` NuGet
  packages (3.0.23.1 / 3.6.5 / 3.3.18) need the same direct check before
  assuming anything there either.

---

## 1. EQ with a true bypass mode

**Problem:** no equalizer exists in Flower at all today.

**Plan:**
1. Extend `IAudioManager` with `SetEqualizer(EqualizerSettings? settings)`
   (and read-only preset/band metadata accessors: preset names, band count,
   band center frequencies) implemented in `VlcAudioManager` directly on top
   of the confirmed `Equalizer`/`SetEqualizer`/`UnsetEqualizer` API above.
   **`settings == null` must call the bypass path** (`UnsetEqualizer()`), not
   push a flat all-zero-dB `Equalizer` — that still runs the DSP filter chain
   and is not a true bypass.
2. Add an `EqualizerSettings` model (preamp float, band-gain float array,
   enabled flag, optional preset index) and persist it via `AppSettings`
   (`Flower/Persistence/AppSettingsStore.cs`), mirroring the existing
   `IsRepeatEnabled`/`IsShuffleEnabled` load/save pattern — default
   `Enabled = false` so this ships purely additive with zero behavior change
   out of the box.
3. New `EqualizerWindow`/panel: band sliders (count driven by
   `Equalizer.BandCount`, not hardcoded to 10), preamp slider, preset
   dropdown (`PresetCount`/`GetPresetName`), and a prominent Enabled toggle —
   that toggle *is* the bypass control from the user's perspective.
4. Wire changes straight through to `IAudioManager.SetEqualizer` in real
   time (no "apply" button) the same way volume changes are live today.

**Effort:** Small–Medium (mostly UI + persistence plumbing). **Risk:** Low —
the correct underlying mechanism already exists and is confirmed present.

---

## 2. Low-latency, minimal-overhead playback engine

**Problem:** `VlcAudioManager` initializes `LibVLC` with zero options
(`VlcAudioManager.cs:50`), so it pays for whatever LibVLC's defaults assume
(general-purpose player, not an audio-only one).

**Plan:**
1. Pass an explicit lean option set into `new LibVLC(options)`: skip video
   output/subpicture/OSD subsystems and stats collection Flower never uses
   (audio-only app). Exact flag names need validating against LibVLC 3.x's
   option table at implementation time rather than assumed — some may already
   be no-ops when driving libvlc directly rather than the `vlc` binary — but
   the direction (don't initialize subsystems this app has no UI for) is
   clear and low-risk to try.
2. Lower `file-caching` from LibVLC's ~300ms local-file default — Flower only
   ever plays local/on-device files, never network streams, so a smaller
   buffer trims `Play()`-to-audible-output latency with negligible underrun
   risk. Start as a fixed constructor option; only surface as a user setting
   if a real regression shows up on slow storage.
3. Already fine, verified, no change needed: `VlcAudioManager.Play`
   (`VlcAudioManager.cs:61-69`) never calls `Media.Parse()` or otherwise
   blocks on metadata extraction before playback — tag reading already
   happens once up front in `Importer`, not per-`Play()` — and `LibVLC`/
   `MediaPlayer` are constructed once, not per track. No redundant overhead
   found here beyond the two config items above.

**Effort:** Small (constructor options + one buffering tweak). **Risk:**
Low — additive/config-only, trivially revertible if an option destabilizes
playback on some platform.

---

## 3. Additional high-res formats: DSD (`.dsf`) + Monkey's Audio (`.ape`)

**Problem:** `Importer._validExtensions` (`Importer.cs:15`) only lists
`.mp3, .m4a, .wav, .flac, .alac` — DSD and APE aren't imported at all today.
**Confirmed the installed macOS VLC build (the one `VlcNativeSetup.cs`
depends on) has no native demux/decode plugin for either format** — this is
a bigger gap than "add two extensions," since import and playback are no
longer expected to both just work.

**Plan:**
1. Add `.ape` and `.dsf` to `Importer._validExtensions` regardless — the
   confirmed `TagLib.Ape`/`TagLib.Dsf` support means *tagging* works today via
   the same `TagLib.File.Create(file)` call `Importer.cs:54` already uses,
   independent of whether LibVLC can play the file back. Library metadata,
   sorting, and browsing all work even before playback does.
2. Deliberately scope out `.dff` (DSDIFF) — not supported by TagLib# 2.3.0,
   would need either a TagLib# fork/PR or a hand-rolled chunk parser for its
   ID3-in-DSDIFF metadata block. Revisit only if a real library shows up with
   `.dff` files instead of `.dsf`.
3. **Playback needs real native-plugin work, not just a smoke test.** Since
   mainline VLC's own plugin set doesn't include `ape`/`dsd` modules,
   getting `Play()` to work means either (a) building/sourcing third-party
   VLC demux/decode plugins for Monkey's Audio and DSD and shipping them
   alongside LibVLC on every platform (real native-build effort, not a
   config change), or (b) decoding these formats outside LibVLC entirely —
   e.g. a managed/native Monkey's Audio decoder and a DSD-to-PCM decoder
   feeding raw PCM into `MediaPlayer` via the same `SetAudioCallbacks` path
   already noted for gapless/AirPlay work — and treating LibVLC as
   metadata/UI-only for these two formats. Either path is real engineering,
   not a one-line fix.
4. Until one of those paths is built, `Play()` on an `.ape`/`.dsf` track
   will fail — wrap it in an explicit try/catch with a user-facing
   "unsupported format" message rather than a silent failure, so imported
   DSD/APE tracks are at least visible and explained rather than mysteriously
   broken.
5. Set expectations correctly in any UI copy once playback does exist: DSD
   would be decoded to PCM (there's no bit-perfect DSD/DoP passthrough
   path being planned here) — "DSD support" would mean "Flower can play
   your DSD files," not "Flower outputs native DSD to your DAC."

**Effort:** Small for import/tagging (one-line extension list change,
ships independently and immediately) — but **Medium–Large for playback**,
now that native plugin support is confirmed absent rather than assumed
present. **Risk:** Low for tagging/import. Medium–High for playback — this
is closer to "build or source a new decode path" than "verify an existing
one," and Android/iOS's `VideoLAN.LibVLC.*` packages haven't been checked
yet either and could easily be in the same position.

---

## 4. Near-gapless playback (pragmatic first step)

**Problem:** auto-advance today only starts the next track after `EndReached`
(`PlaylistControlViewModel.cs:102-117`), so the full cost of constructing and
opening a fresh `Media` happens *after* the gap has already started.

**Plan:**
1. Extend `IAudioManager` with a `Preload(Track track)` that constructs and
   `Parse()`s the next `Media` ahead of time (format/demux probing done
   early, off the critical path) without starting playback, plus a
   `PlayPreloaded()` that swaps to it instantly when the time comes.
2. In `PlaylistControlViewModel`, use the already-forwarded `PositionChanged`
   event: once remaining time drops under a small threshold (e.g. ~2–3s) and
   this track hasn't been preloaded yet, compute the next track **once**
   (respecting shuffle/repeat via the existing `GetNextTrack`) and call
   `Preload`. Freeze that choice — `EndReached` must play the exact preloaded
   track rather than recomputing `GetNextTrack` again, which matters for
   shuffle (a second random roll would preload one track and then play a
   different one).
3. This removes demux/codec-open latency from the audible gap but is **not**
   sample-accurate gapless — there's still a hand-off between the old and new
   `Media` on the same `MediaPlayer`. Treat it as "much shorter gap," not
   "no gap," and set expectations accordingly wherever this is surfaced.

**Effort:** Medium (new preload API + a `PositionChanged`-driven trigger with
frozen next-track selection). **Risk:** Low — purely additive; falls back to
today's exact behavior if `Preload` is never called.

---

## 5. Multi-channel / hi-res sample rate passthrough (24-bit/192kHz+)

**Problem:** confirmed above — LibVLCSharp's channel API can't independently
force multichannel passthrough, and whether a track's native sample rate
reaches the DAC un-resampled depends on the audio output module/device, not
anything Flower configures today.

**Plan:**
1. **Depends on `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1** (in-app output-device
   picker): VLC's per-platform aout modules generally only attempt
   sample-rate matching against an explicitly selected physical device, not
   the OS's abstracted "system default" — so this feature is close to
   meaningless without device selection landing first. Sequence it after
   that phase rather than duplicating device-enum work here.
2. Once a specific device is selected, expose (and default-enable) the
   relevant per-platform passthrough option: Windows' WASAPI output module
   has a documented exclusive-mode setting (exact confvar name needs
   confirming against the installed native module at implementation time);
   macOS's `auhal` module can switch the hardware's nominal sample rate to
   match the stream when talking to a real device rather than the default
   aggregate device. Linux is the least controllable of the three (PulseAudio
   resamples to a fixed rate; would need to target ALSA/PipeWire directly)
   — treat as best-effort there.
3. Multichannel: since there's no explicit passthrough toggle, treat this as
   "verify it already works when both the source and the selected device
   support more than 2 channels" rather than new API surface — the
   `Channels`/`BitsPerSample`/`SampleRate` fields already read by `Importer`
   give enough information to *detect and surface* a track's channel count
   in the UI, even if Flower can't independently force passthrough behavior.
4. This is a genuine spike, not a scoped implementation task yet — the
   plan above is "what to go verify," not confirmed working behavior.

**Effort:** Medium, mostly investigation. **Risk:** Medium — depends on
per-platform native module behavior not verifiable from this codebase alone,
and is blocked on Phase 1 of the AirPlay/Bluetooth plan.

---

## 6. True sample-accurate gapless (stretch goal)

**Problem:** #4 above shortens the gap but doesn't eliminate it; genuinely
sample-accurate gapless needs audio that never stops flowing to the output
device across a track boundary, which isn't possible while each track is a
separate `Media` handed to `MediaPlayer.Play()`.

**Plan:** Build a custom PCM pipeline on `MediaPlayer.SetAudioCallbacks`/
`SetAudioFormat` (confirmed present, same API `AIRPLAY-BLUETOOTH-PLAN.md`
Phase 2 already plans to use for AirPlay/Bluetooth) that decodes the next
track ahead of the current one ending and feeds a continuous PCM stream
across the boundary, rather than stopping/starting the device. **Do not build
this twice** — if/when `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2's `AVAudioEngine`
bridge gets built, design its ring-buffer/callback plumbing to also serve
gapless track transitions on Apple platforms, and treat a Windows/Linux/
Android equivalent as the same category of work if it's ever prioritized
there. Until then, #4's pragmatic preload approach is the standing solution.

**Effort:** Large. **Risk:** Medium–High — real-time audio buffering is
inherently the hard part, same as flagged in `AIRPLAY-BLUETOOTH-PLAN.md`
Phase 2.

---

## Suggested execution order

1. **#1 EQ bypass** — fully self-contained, no dependencies, the one item
   here with zero open technical questions.
2. **#2 Low-latency engine tuning** — small, config-level, do alongside #1.
3. **#3 DSD/APE formats** — split in two: ship the cheap import/tagging change
   any time, but treat actual playback as its own, much larger, separately
   prioritized effort now that native plugin support is confirmed missing.
4. **#4 Near-gapless preload** — moderate, no external dependencies.
5. **#5 Multi-channel/hi-res passthrough** — do after
   `AIRPLAY-BLUETOOTH-PLAN.md` Phase 1 ships (hard dependency on device
   selection existing first).
6. **#6 True sample-accurate gapless** — biggest and riskiest; fold into
   `AIRPLAY-BLUETOOTH-PLAN.md` Phase 2 if/when that's built, rather than
   building a second custom audio pipeline independently.
