# Decoder Library Plan

Turning `native/ffmpeg/` into a general-purpose, audio-only decode library that
someone who has never heard of Flower can consume, shipped from its own repo as
a NuGet package with static natives — while Flower itself keeps decoding
exactly as it does today.

Two decisions are already taken and everything below serves them:

- **Flower stays on S24.** `PcmSampleFormat` keeps its two members and its
  comment. The reasoning is in `AUDIOPHILE-PLAN.md` and has not changed:
  `OutputStage` does its arithmetic in float, a float mantissa holds 24 bits
  exactly, so S32 is a widening that quietly narrows again. There is no music
  format that needs more — everything above 24 bits in the wild is *float*
  (32-bit-float WAV, DAW masters, DSD after conversion), never 32-bit integer,
  and even for those the clipping that matters is clamped identically at S24
  and S32 because swresample clamps float→int at full scale either way.
- **The library exposes all four.** S16, S24, S32, F32. A library does not get
  to decide its caller's sink. Someone feeding an analysis tool, a DAW-adjacent
  pipeline, or a device with a native S32 path should not have to fork it.

## What is already done

Worth stating first, because it is most of the ask and it changes where the
effort goes. **The four formats are already in the ABI and already in the
managed binding**:

- `flower_ffmpeg.h`'s `flower_sample_format` declares all four.
- `swr_format_for` maps all four (S24 and S32 both to `AV_SAMPLE_FMT_S32`,
  F32 to `AV_SAMPLE_FMT_FLT`).
- `delivered_bytes_per_sample` returns 2/3/4/4.
- `stage_frame` and `stage_swr_tail` special-case only S24's packing and
  `memcpy` everything else, so S32 and F32 fall out of the general path.
- `FfmpegSampleFormat` and `FfmpegAudioFormat.BytesPerFrame` mirror all four.

The buffer sizing was checked rather than assumed: `ensure_buffers` sizes
scratch by `swr_bytes_per_frame` and pending by `out_bytes_per_frame`, and
those differ only for S24 (4 vs 3). For S16, S32 and F32 they are equal and the
`memcpy` of `converted * out_bytes_per_frame` cannot overrun.

**What is missing is proof, not code.** `grep` across the solution finds
`FfmpegSampleFormat.S32` and `.F32` referenced by nothing — no caller, no test,
no device check. They are plausible and unexercised, which is the same state
every bug in `AUDIO-QUALITY-PLAN.md` was in. So the first phase is coverage.

## Phase 0 — Prove the two unexercised formats — Small effort, Low risk

Before anything is extracted, make the claim true. In `FfmpegDecoderTests`,
against the existing 24-bit fixture:

- **S32 of a 24-bit source has a zero low byte in every sample.** That is the
  left-alignment the whole S24 packing depends on, asserted directly rather
  than inferred from `pack_s24` working.
- **S24 and S32 of the same source carry the same three bytes.** Decode twice,
  drop each S32 sample's low byte, compare buffers. This is the lossless claim
  in `pack_s24`'s comment, currently believed and never tested.
- **F32 round-trips a 16-bit source exactly.** Every 16-bit integer is exact in
  a float, so the comparison is equality, not tolerance.
- **`BytesPerFrame` matches what `flower_decoder_read` actually writes** for
  each of the four, which is the one assertion that would catch a sizing
  mistake introduced later.

This phase is worth doing on its own merits even if the rest of the plan never
happens: two ABI values that nothing exercises are two ABI values that will be
wrong the first time someone needs them.

## Phase 1 — The name — Blocks Phase 2

The one genuinely open decision, and it is not cosmetic: the name fixes the
symbol prefix, the header guard, the package id, the repo, the `DllImport`
string and the export list that `ios/build.sh` and `android/build.sh` derive
from the `FLOWER_API` lines. Doing it once is cheap; doing it twice means
touching every one of those.

`flower_` cannot survive — a library called Flower that decodes audio for
everyone is a joke at the expense of whoever has to explain it. This document
uses **`audioread`** provisionally (`audioread_decoder_open_path`,
`AudioRead.Decoder`, `libaudioread.so`), on the grounds that it says what it
does and matches the name the same operation carries in librosa and MATLAB.
Substitute freely before Phase 2 starts; after it, do not.

`AUDIOREAD_ABI_VERSION` resets to 1. Flower's current ABI 1 and the library's
ABI 1 are different contracts under different names, which is fine precisely
because nothing has shipped.

## Phase 2 — Extract the repo — Medium effort, Low risk

A new repository containing what is genuinely general:

| Moves | Stays in Flower |
|---|---|
| `flower_ffmpeg.c` / `.h` → `audioread.c` / `.h` | `FfmpegTrackDecoder` — it implements `ITrackDecoder`, Flower's own interface, and owns the demuxer-hint policy, the decode thread and the ring |
| `CMakeLists.txt`, all five build scripts, `build-all.sh` | `GaplessFormat`, `PcmSampleFormat`, `OutputStage` |
| `FfmpegNative.cs` → the package's own P/Invoke layer | The `FormatFor` mapping, which is where "Flower asks for S24" is written down |
| `FfmpegDecoder.cs` → `AudioRead.Decoder`, the public managed API | |
| `FfmpegDecoderTests` → the library's own suite | `FfmpegTrackDecoderTests`, `CanonicalFormatTests` |

Two things in `FfmpegNative.Resolve` are Flower-shaped and do not move as-is.
The six-level walk up to `native/ffmpeg/artifacts/<platform>/` is a
development convenience for a repo layout the package will not have — a NuGet
resolves natives from `runtimes/<rid>/native/` and needs none of it. The iOS
branch does move, because .NET-for-iOS genuinely cannot resolve a `DllImport`
string to a binary nested inside an embedded framework; that is a real problem
every consumer will have, and `MiniaudioSink`'s static constructor documents
the same failure for the same reason.

`FLOWER_FFMPEG` becomes `AUDIOREAD_LIBRARY` and keeps its job: pointing at a
differently-built library for a bisect without a rebuild.

**Flower consumes the package at the end of this phase, not the beginning.**
The check that the extraction lost nothing is that the full suite plus the
device checks stay green on all five heads, which means the phase is not done
until Flower is building against the package rather than the folder.

## Phase 3 — What a general audio library needs that this does not have — Medium effort, Low risk

The current eight functions are already general — `open_io` is FFmpeg's own
AVIO signature, so it is generic by construction, and `get_format` / `read` /
`seek` are the whole of decoding. The additions are the things every consumer
would otherwise reach past the façade for, which is exactly what the façade
exists to prevent:

- **Tags.** `avformat`'s metadata dictionary, flattened to a key/value
  enumeration over caller-owned storage. Flower reads tags with TagLib# and
  will keep doing so; a general library that decodes a file and cannot say its
  title is one nobody adopts.
- **Embedded cover art.** The `AV_DISPOSITION_ATTACHED_PIC` stream, handed back
  as bytes plus a mime type. Same argument.
- **Channel layout, not a channel count.** `flower_decoder_format.channels` is
  a number; a caller doing anything multi-channel needs to know which channel
  is which. `AVChannelLayout` has a canonical string form — return that, keep
  the struct on the C side, and the ABI stays ints and byte buffers.
- **Codec and container names.** One call, two short strings. Cheap, and it is
  the first thing anyone printing a file's properties wants.

Bumps the ABI to 2. Resist everything else: encoding, filtering, video,
resampling as a standalone service. The value of this header is that you can
read all of it in one sitting, and that is a property that only gets spent.

## Phase 4 — The decoder set — Small effort, Medium risk

The mobile builds are `--disable-everything` plus an explicit list, which is
what keeps an iOS slice at 1.9MB and an Android ABI at 1.3MB. That list is
Flower's:

```
decoders="mp3,mp3float,aac,aac_latm,alac,flac,vorbis,opus,wavpack,ape,pcm_*"
demuxers="mov,mp3,flac,wav,w64,ogg,matroska,aac,ape,wv,aiff,dsf"
```

A general library cannot ship only that, and cannot ship all of FFmpeg either —
the Windows download is already 70MB of avcodec for a façade that calls four
functions. So: **two build variants from one configure line**, a `slim` whose
list is the one above and a `full` that drops `--disable-everything` for the
audio family, published as two packages or one package with a runtime
selection. Flower takes `slim` and its size does not move.

One real gap found while reading this list, worth fixing regardless of which
variant it lands in: **`dsf` is in the demuxers and no `dsd_*` decoder is in
the decoders.** A `.dsf` file will demux and then fail to find a decoder, which
is a worse failure than not supporting it, and `AUDIOPHILE-PLAN.md` §3 is
written as though the build-configuration question is still open when it is
half-answered in the wrong direction.

## Phase 5 — Static natives and the LGPL route — Medium effort, **High risk**

The riskiest phase and the one that gates publishing anything at all. The
constraint is already stated in `native/ffmpeg/README.md`: FFmpeg may be linked
only under the LGPL, which means no `--enable-gpl`, no `--enable-nonfree`, and
the libraries must stay replaceable.

Dynamic linking satisfies "replaceable" directly, which is why desktop has been
fine. **A static NuGet does not**, and the package is the point at which Flower
stops being able to point at a distro build as someone else's problem. Meeting
§6 for a statically linked artifact means shipping the corresponding source and
a genuine relink route — the exact FFmpeg version, the exact configure line,
the build scripts, and object files or a documented reproducible rebuild that
actually produces the shipped binary.

`FLOWER_FFMPEG_STATIC` already exists in `CMakeLists.txt` and already works for
every pkg-config platform, so iOS and Android are mostly a rename. **Windows is
the genuinely unscoped one**: the MSVC branch fatally requires
`FLOWER_FFMPEG_PREFIX` and links import libraries with no static path at all,
and a static Windows FFmpeg means either mingw `.a` files that MSVC will not
consume or building FFmpeg under MSVC, neither of which anyone here has done.

Two things this phase must not skip. The GPL check is mechanical — the
generated `config.h` says `CONFIG_GPL 0` — and should be asserted by the build
script rather than remembered. And the export narrowing has to survive the
rename: `CMAKE_C_VISIBILITY_PRESET hidden` cannot reach inside a static
archive, so without `-exported_symbols_list` (Apple) or an ELF version script
(Android) the package re-exports FFmpeg's entire ABI — which is precisely the
second route to FFmpeg this façade exists in order not to have. Both are
currently derived from the `FLOWER_API` lines in the header; they become
`AUDIOREAD_API` lines and the derivation is unchanged.

**Do not publish before a licence read that is not this document.** Everything
above is the constraint as the repo already understands it, not advice.

## Phase 6 — Packaging and CI — Medium effort, Low risk

`runtimes/<rid>/native/` for the desktop three, plus `buildTransitive/<tfm>/`
targets injecting `NativeReference` for iOS and `AndroidNativeLibrary` for
Android, since neither mobile head resolves a native from `runtimes/` on its
own. The library's CI builds all five, which is more than Flower's does today —
`tests.yml` builds the three desktops and mobile is checked-in binaries. Moving
the mobile cross-compiles to the library's own release workflow is a strict
improvement: they are tens of minutes that a push-triggered job should not
spend, but a tagged release should.

Version the package independently. Flower pins a version like any other
dependency, which is the first time the façade has had a version at all.

## Phase 7 — Flower consumes it — Small effort, Low risk

`PackageReference` replaces `native/ffmpeg/`, `Flower/Audio/Ffmpeg/FfmpegNative.cs`
and `FfmpegDecoder.cs`. `FfmpegTrackDecoder` keeps its name and its interface
and changes only which namespace `Decoder` comes from. `FormatFor` still returns
S24 and always will.

Three documents need the correction afterwards, and they are load-bearing rather
than incidental: `CLAUDE.md`'s "FFmpeg façade" paragraph and its fresh-clone
note (the façade stops being built and starts being restored, which makes
`native/ffmpeg/build-all.sh` no longer a prerequisite for a clone that wants to
play music — a real improvement worth stating), `native/ffmpeg/README.md`
(which mostly moves rather than changes), and `AUDIOPHILE-PLAN.md` §3's open
DSD question.

## Suggested order

Phase 0 first and on its own — it is small, it is useful regardless, and if it
finds a defect in the S32 path then everything after it is built on a correction
rather than on an assumption. Then 1, then 2 and 7 as one movement, because a
library nothing consumes proves nothing. Then 3 and 4. Phase 5 last and
deliberately, since it is the only one where being wrong is a licence problem
rather than a bug.

**Nothing here is started.** Phase 0 is the only part that can be done inside
Flower's own repo, and it is the only part that should be done before the name
is settled.
