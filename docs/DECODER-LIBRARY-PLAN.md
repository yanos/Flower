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

## Why not an existing package

The first question anyone will ask, and the answer is not "none exist" — it is
that the ones that exist are the wrong half. There are three kinds on NuGet and
none of them is this:

- **Process wrappers.** `FFMpegCore` (7.4M downloads) and `Xabe.FFmpeg` (3.2M)
  shell out to an `ffmpeg` executable. Both are excellent at what they do and
  neither can be on a phone: iOS forbids spawning arbitrary executables at all,
  and `Xabe.FFmpeg.Downloader` describes itself as fetching binaries for
  "Windows, Mac or Linux" — mobile is not a gap in the feature list, it is a
  wall. They are also the wrong shape for gapless playback, which needs a
  pull-based PCM API with sample-accurate seek rather than a command line that
  produces a file.
- **Whole-ABI auto-generated bindings.** `FFmpeg.AutoGen` (4.7M) and
  `Sdcb.FFmpeg` (59K) are genuine in-process bindings, and they **ship no
  natives at all** — no dependency groups, nothing in `runtimes/`. They hand you
  the entire FFmpeg ABI in `unsafe` C# and leave finding a libavcodec to you,
  which is the exact opposite of the property this header has.
- **Xamarin-era mobile wrappers.** `Laerdal.Xamarin.FFmpeg.Audio` (10K) wraps
  `mobile-ffmpeg`, exposes the ffmpeg *command line* rather than a decode-to-PCM
  API, and its upstream was superseded by `ffmpeg-kit`, which has since been
  retired.

So the value here is inverted from how it is easy to write it. **The bindings
are not scarce; the mobile static natives are.** Anyone can P/Invoke
`avcodec_send_packet` in an afternoon. Almost nobody has a working, LGPL-clean,
statically linked, audio-only FFmpeg for `net10.0-ios` and `net10.0-android`
with the export list narrowed to the functions actually called — and that is
`native/ffmpeg/ios/` and `native/ffmpeg/android/`, which took two scripts apiece
and several rounds of being wrong.

Which also settles what this is *for*. It saves Flower no work: Flower's
decoder already works on all five heads and the extraction can only cost effort,
never recover it. The reason to publish is to hand the next person the mobile
build scripts. That is a fine reason and it should be stated as the reason,
because it is what decides every trade-off below — when the general case and
Flower's case disagree, the general case wins, and Flower absorbs the cost.

## What is already done

Worth stating first, because it is most of the ask and it changes where the
effort goes. **The four formats are already in the ABI and already in the
managed binding**:

- `ffaudio.h`'s `ffaudio_sample_format` declares all four.
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

## Phase 0 — Prove the two unexercised formats — **Done**

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

Done in `FfmpegDecoderTests`: `An_S32_delivery_of_a_24_bit_source_leaves_the_low_byte_empty`,
`S24_is_S32_with_the_padding_dropped`,
`F32_carries_a_24_bit_source_with_no_rounding_at_all` (exact float equality, not
a tolerance), and the four-row theory `Every_format_delivers_the_width_it_advertises`.
Nothing was wrong: the S32 and F32 paths were correct as written, and are now
correct as asserted.

## Phase 1 — The name — **Done**

The one genuinely open decision, and it was not cosmetic: the name fixes the
symbol prefix, the header guard, the package id, the repo, the `DllImport`
string and the export list that `ios/build.sh` and `android/build.sh` derive
from the `FFAUDIO_API` lines. Doing it once is cheap; doing it twice means
touching every one of those.

`flower_` could not survive — a library called Flower that decodes audio for
everyone is a joke at the expense of whoever has to explain it. The name is
**`FFAudio.NET`**, and inside the C it is `ffaudio`:

| Thing | Now |
|---|---|
| Package | `FFAudio.NET`, with `FFAudio.NET.iOS` / `.Android` / `.Windows` / `.Linux` / `.macOS` for the natives |
| Sources | `native/ffmpeg/ffaudio.c`, `ffaudio.h`, guard `FFAUDIO_H` |
| Symbols | `ffaudio_decoder_open_path`, `ffaudio_decoder_read`, … |
| Export macro | `FFAUDIO_API` — still the whole export list, still derived from the header |
| Constants | `FFAUDIO_OK`, `FFAUDIO_EOF`, `FFAUDIO_ERR_*`, `FFAUDIO_SAMPLE_*`, `FFAUDIO_SEEK_SIZE` |
| Binaries | `libffaudio.dylib` / `.so`, `ffaudio.dll`, `ffaudio.framework` |
| `DllImport` | `"ffaudio"` |
| Environment | `FFAUDIO_LIBRARY` |
| CMake options | `FFAUDIO_PREFIX`, `FFAUDIO_STATIC`, `FFAUDIO_ASAN` |

Three things decided the shape rather than taste. **`ff_` is FFmpeg's own
internal-symbol prefix**, and Phase 5 links FFmpeg statically, so an `ff_`-prefixed
façade would collide inside the archive for real; `ffaudio_` is clear of it,
since `ff_` requires the underscore. **`Sharp` versus `Dotnet`**: every
high-download `*Sharp` package is precisely this category — a binding over a
native library (SkiaSharp, HarfBuzzSharp, PuppeteerSharp) — while `Dotnet` run
together is overwhelmingly Microsoft's own `Microsoft.DotNet.*` infrastructure
or a `dotnet-` CLI global tool, which is the wrong neighbourhood to be read
into. **`.NET` with a dot** is the modern third option, and `Silk.NET` is the
proof it reads well and nests cleanly. **The prefix is unreserved**: NuGet's
`verified` flag is false on the `FFmpeg.*` prefix and `FFAudio.NET` is free.

`FFAUDIO_ABI_VERSION` resets to 1. Flower's old ABI 1 and this ABI 1 are
different contracts under different names, which is fine precisely because
nothing has shipped.

The rename is applied inside Flower, ahead of the extraction, so that the
extraction is a file move rather than a file move plus a rename. All five heads
were rebuilt against it and are green: the full suite (1824, including the
`RequiresFfmpeg` set against a freshly built `libffaudio.dylib`), the iOS
simulator device checks (71/71) and the Android emulator device checks (71/71),
with the iOS framework and all three Android ABIs rebuilt and re-committed
under the new names. The folder stays `native/ffmpeg/` until Phase 2 moves it
out entirely — it sits beside `native/miniaudio/` and still describes what is
built there.

## Phase 2 — Extract the repo — **Half done**

The repository exists: `../FFAudio.NET`, first commit `cd68ad4`. It holds the
whole left column below and its own 21-test suite passes against a natively
built `libffaudio.dylib` it finds in its own `native/artifacts/`. What has not
happened is the other half of this phase — Flower still builds against
`native/ffmpeg/` and `Flower/Audio/Ffmpeg/`, and cannot stop until there is a
package to consume. See "What the removal is waiting on" below.

The new repository contains what is genuinely general:

| Moves | Stays in Flower |
|---|---|
| `ffaudio.c` / `.h` (already renamed) | `FfmpegTrackDecoder` — it implements `ITrackDecoder`, Flower's own interface, and owns the demuxer-hint policy, the decode thread and the ring |
| `CMakeLists.txt`, all five build scripts, `build-all.sh` | `GaplessFormat`, `PcmSampleFormat`, `OutputStage` |
| `FfmpegNative.cs` → the package's own P/Invoke layer | The `FormatFor` mapping, which is where "Flower asks for S24" is written down |
| `FfmpegDecoder.cs` → `FFAudio.Decoder`, the public managed API | |
| `FfmpegDecoderTests` → the library's own suite | `FfmpegTrackDecoderTests`, `CanonicalFormatTests` |

One thing in `FfmpegNative.Resolve` was Flower-shaped and did not move as-is.
The six-level walk up to `native/ffmpeg/artifacts/<platform>/` was a
development convenience for a repo layout the package will not have — a NuGet
resolves natives from `runtimes/<rid>/native/` and needs none of it. It was
kept and repointed at the new repo's own `native/artifacts/<platform>/`, where
it does the same job for the library's own suite; the plain `TryLoad` by name at
the end of the chain is what will pick up a NuGet's payload. The iOS
branch does move, because .NET-for-iOS genuinely cannot resolve a `DllImport`
string to a binary nested inside an embedded framework; that is a real problem
every consumer will have, and `MiniaudioSink`'s static constructor documents
the same failure for the same reason.

`FFAUDIO_LIBRARY` keeps its job across the move: pointing at a
differently-built library for a bisect without a rebuild.

**Flower consumes the package at the end of this phase, not the beginning.**
The check that the extraction lost nothing is that the full suite plus the
device checks stay green on all five heads, which means the phase is not done
until Flower is building against the package rather than the folder.

### What the removal is waiting on

Deleting `native/ffmpeg/` from Flower today would break three things at once
with nothing to replace them: the `RequiresFfmpeg` tests, which need a dylib
only those scripts can build; `tests.yml`'s three desktop native-build steps;
and the fresh-clone property `CLAUDE.md` calls load-bearing, since a clone
would have no route to a decoder at all. The checked-in mobile binaries under
`Flower.iOS/Frameworks/` and `Flower.Android/libs/` are the exception — those
are shipped artifacts rather than sources and keep working untouched.

So the order is: **Phase 5's licence read, then Phase 6's packaging, then the
deletion.** Two bridges could pull it earlier if that wait proves too long, and
neither is free:

- **A cross-repo `ProjectReference`.** What a developer working on both repos
  at once actually wants, and it costs the fresh-clone property outright — a
  clone of Flower alone would not build. Fine on a branch, wrong on `master`.
- **`dotnet pack` into a folder feed committed under Flower.** Keeps the clone
  working and puts a binary in git, which is the thing `native/ffmpeg/artifacts/`
  is gitignored to avoid. Defensible only as an explicitly temporary bridge.

The recommendation is to wait. Nothing about Flower is worse for the duplicate
existing for another phase or two, and both bridges trade a real property for
tidiness.

## Phase 3 — What a general audio library needs that this does not have — **Done**

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
- **Channel layout, not a channel count.** `ffaudio_decoder_format.channels` is
  a number; a caller doing anything multi-channel needs to know which channel
  is which. `AVChannelLayout` has a canonical string form — return that, keep
  the struct on the C side, and the ABI stays ints and byte buffers.
- **Codec and container names.** One call, two short strings. Cheap, and it is
  the first thing anyone printing a file's properties wants.

All four are in, as `ffaudio_decoder_tag_count`/`tag_at`, `cover_art`,
`channel_layout` and `names`, with `Decoder.Tags`, `Decoder.TryReadCoverArt()`
and three new fields on `AudioFormat` over them. Everything still goes into
caller-owned buffers and nothing on the native side allocates.

**The ABI did not need to bump after all.** This was written expecting version
2, on the assumption that adding to the header changes its shape. It does not:
every one of these is a new function, no struct moved, and no existing
signature changed. What a bump would have bought is a clean "wrong library"
error instead of a missing-symbol one when new managed code meets an old
native — worth having once something has shipped, and not worth the churn
while nothing has. It stays at 1.

That distinction had a consequence worth recording, because it is the first
time the two façades diverged. Flower vendors its own copy under
`native/ffmpeg/`, and the managed side now calls `channel_layout` and `names`
from the `Decoder` constructor — so pairing the new package with Flower's older
library fails *every* open with `EntryPointNotFoundException`.
`scripts/use-ffaudio-package.sh` therefore takes the façade from the
FFAudio.NET checkout rather than from Flower, which is the only arrangement
that stays honest as the library moves ahead of what Flower vendors.

The fixture question was the interesting part of the work. None of this can be
tested against synthetic PCM — a WAV of a ramp has no tags and no cover art —
and the obvious answer, shelling out to the `ffmpeg` binary to build one, fails
exactly where it matters: the Linux CI job installs `libavformat-dev` and no
binary at all, so those tests would have quietly skipped on the platform most
likely to differ. `SyntheticTaggedAiff` builds one by hand instead: AIFF is raw
big-endian PCM, and `aiffdec` reads an `ID3 ` chunk with the same parser it
uses on an MP3, so a hand-written ID3v2.3 tag reaches the same metadata
dictionary and the same `ATTACHED_PIC` stream a tagged MP3 would. Ten tests
over it, all green.

Resisted, and still worth resisting: encoding, filtering, video, resampling as
a standalone service. The value of this header is that you can read all of it
in one sitting, and that is a property that only gets spent.

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

`FFAUDIO_STATIC` already exists in `CMakeLists.txt` and already works for
every pkg-config platform, so iOS and Android are already most of the way there. **Windows is
the genuinely unscoped one**: the MSVC branch fatally requires
`FFAUDIO_PREFIX` and links import libraries with no static path at all,
and a static Windows FFmpeg means either mingw `.a` files that MSVC will not
consume or building FFmpeg under MSVC, neither of which anyone here has done.

Two things this phase must not skip. The GPL check is mechanical — the
generated `config.h` says `CONFIG_GPL 0` — and should be asserted by the build
script rather than remembered. And the export narrowing has to survive the
rename: `CMAKE_C_VISIBILITY_PRESET hidden` cannot reach inside a static
archive, so without `-exported_symbols_list` (Apple) or an ELF version script
(Android) the package re-exports FFmpeg's entire ABI — which is precisely the
second route to FFmpeg this façade exists in order not to have. Both are
derived from the `FFAUDIO_API` lines in the header, and that derivation is what
must keep working — it is why the export macro is on the functions and on
nothing else.

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

**Phases 0, 1 and 3 are done** and **Phase 2 is half done**: the library has its
own repository at `../FFAudio.NET` and passes its own suite there, while Flower
still builds against the folder. The removal is gated on Phases 5 and 6, for the
reasons Phase 2 records.

Phase 2's other half now has a bridge that neither of the two it lists costs
anything: `scripts/use-ffaudio-package.sh` puts Flower on a CI-built (or
locally packed) FFAudio.NET without editing a single csproj or source file, by
way of four MSBuild `Using` aliases and a gitignored props file
Directory.Build.props imports only when it exists. A clean clone still builds
the vendored façade, so the fresh-clone property is intact. It is a testing
switch and not the deletion — but it is what makes the deletion something that
can be proven rather than attempted.
