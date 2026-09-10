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

## Phase 4 — The decoder set — **Done**

The mobile builds are `--disable-everything` plus an explicit list, which is
what keeps an iOS slice at 1.9MB and an Android ABI at 1.3MB. That list was
Flower's, written out twice — once in `ios/build-ffmpeg.sh`, once in
`android/build-ffmpeg.sh` — with a comment in each saying it had to match the
other. It now lives once, in FFAudio.NET's `native/codec-set.sh`, which both
scripts source, and `FFAUDIO_VARIANT` picks between two sets:

- **`slim`** (the default) — the music-library list, unchanged in shape, plus
  the DSD fix below. 22 decoders, 12 demuxers. Flower takes this and its size
  does not move.
- **`full`** — every audio decoder FFmpeg has and every demuxer it has: 201
  and 350. Not "drop `--disable-everything`", which would enable the whole
  video decoder set for a façade that hands back PCM and cannot express a
  frame. It is the audio half of FFmpeg's own list, read out of the source
  tree about to be configured: `libavcodec/allcodecs.c` groups its
  declarations under `/* audio codecs */`, `/* PCM codecs */`, `/* DPCM
  codecs */` and `/* ADPCM codecs */`, and everything before `/* subtitles */`
  is what produces samples. Markers that disappear in a future FFmpeg fail the
  build with an empty list rather than configuring no decoders at all.

Two variants, one artifact path: they build to the same name in the same
place, and the tree carries a `VARIANT` file saying which one it holds. Their
FFmpeg prefixes are separate (`ffmpeg/prefix/<variant>/`), so switching is a
relink rather than another forty minutes.

**The gap this phase was really about is closed**: `dsf` was in the demuxer
list with no `dsd_*` decoder behind it, so a `.dsf` demuxed and then failed to
find a decoder — a worse failure than not claiming the format at all. The four
`dsd_*` decoders are in `slim` now, in FFAudio.NET and in Flower's own copy of
the scripts, which is what phone builds still come from until Phase 7.
`AUDIOPHILE-PLAN.md` §3 is still written as though the build-configuration
question were open; it is not, and that document needs the correction.

What is verified and what is not: the configure lines were run against FFmpeg
7.1.1 on macOS, both variants, which is what says 22/12 and 201/350 and that
`CONFIG_GPL` and `CONFIG_NONFREE` both come back 0. No slice or ABI has been
linked from either — `full` has never been built for a phone, and neither has
a `slim` with DSD in it. Phase 5's licence assertion arrived early because it
belongs to the file that generates the configure line: `ffaudio_assert_lgpl`
reads the generated `config.h` after configure and stops the build rather than
leaving the check to be remembered.

## Phase 5 — Static natives and the LGPL route — **Mostly done**, **High risk**

The riskiest phase and the one that gates publishing anything at all. The
constraint is already stated in `native/ffmpeg/README.md`: FFmpeg may be linked
only under the LGPL, which means no `--enable-gpl`, no `--enable-nonfree`, and
the libraries must stay replaceable.

Dynamic linking satisfies "replaceable" directly, which is why desktop *looked*
fine — and it was not, for a reason that has nothing to do with licensing: a
dynamically linked façade records where it found FFmpeg, and a package is a
binary that gets restored somewhere else. See "Where dynamic actually broke"
below. **A static NuGet does not satisfy "replaceable" either**, and the package is the point at which Flower
stops being able to point at a distro build as someone else's problem. Meeting
§6 for a statically linked artifact means shipping the corresponding source and
a genuine relink route — the exact FFmpeg version, the exact configure line,
the build scripts, and object files or a documented reproducible rebuild that
actually produces the shipped binary.

`FFAUDIO_STATIC` already exists in `CMakeLists.txt`, and macOS and Linux now
use it. **Windows turns out not to need it at all**: its payload package
already carries the four LGPL FFmpeg DLLs beside `ffaudio.dll`, in
`runtimes/win-x64/native/`, and Windows resolves a DLL's imports from the
directory it was loaded out of. So that payload is already self-contained
*and* already keeps the libraries separate and replaceable, which is the
easier half of the licence obligation rather than the harder one. Building a
static MSVC FFmpeg — mingw `.a` files MSVC will not consume, or FFmpeg under
MSVC, neither of which anyone here has done — would be a worse answer to a
question Windows does not have. "Unscoped" was the wrong word for it.

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

### What is done

The provable half. A build can now be asked what it is, from both sides:

- `ffaudio_assert_lgpl` (Phase 4's, in `codec-set.sh`) reads the generated
  `config.h` after configure and stops a mobile build whose `CONFIG_GPL` or
  `CONFIG_NONFREE` came back set.
- `FFmpegBuild.License` / `.Configuration` / `.Version` / `.IsRedistributable`
  ask the *binary*, through three new C functions over `avutil_license()`,
  `avutil_configuration()` and `av_version_info()`. A configure line lives in
  a script and an environment variable; neither travels with a dylib that has
  been copied into a NuGet and embedded in an app bundle. avutil does.

They fail at different times, which is why both: one when the FFmpeg is built,
one when a binary that already exists is asked. And the second is what the
LGPL's relink route actually needs — the exact arguments that produced what is
inside this artifact, from the artifact.

`FFAUDIO_REQUIRE_LGPL` turns the second into a gate. It is off by default
because a developer's machine is expected to fail it, and this one does: the
MacPorts FFmpeg here reports `GPL version 2 or later`, configured with
`--enable-gpl --enable-libx264 --enable-libx265 --enable-libvidstab
--enable-libxvid`. CI sets it on Windows alone, where the FFmpeg is a pinned
LGPL asset this repo chose, so the gate doubles as a check that the pin is
still what its name says.

Additive again — three functions, nothing moved — so the ABI stays 1. Unlike
Phase 3's metadata calls, none of these is on a decode path, so an older
façade paired with this binding fails only if something asks.

### Where dynamic actually broke

Predicted as a licensing problem, arrived as a loading one, and the two are
independent.

The first real consumer of `FFAudio.NET.macOS` was Flower itself, switched on
by `scripts/use-ffaudio-package.sh` against the CI-built 0.1.0-alpha.0.10.
`RequiresFfmpeg` came back **39 failed / 53**. Not a binding mismatch and not
a missing payload — the dylib was restored to exactly the right place. `otool
-L` on it:

```
/opt/homebrew/opt/ffmpeg/lib/libavformat.63.dylib
/opt/homebrew/opt/ffmpeg/lib/libavcodec.63.dylib
/opt/homebrew/opt/ffmpeg/lib/libavutil.61.dylib
/opt/homebrew/opt/ffmpeg/lib/libswresample.6.dylib
```

CI does `brew install ffmpeg`, so the façade recorded Homebrew's absolute
paths. This machine's FFmpeg is MacPorts', and `/opt/homebrew` does not exist
on it. The same package would fail on any machine without that exact Homebrew
install, which is most of them; Linux was in the same shape one soname away.
There is no link error and no warning, which is what made this worth writing
down — the failure is invisible until it is somebody else's.

### The static desktop build

Both desktops now build the way the phones do, under `FFAUDIO_STATIC=1`:

- `native/host-ffmpeg.sh` — new, the desktop twin of the two
  `build-ffmpeg.sh` scripts. A `--disable-everything` LGPL FFmpeg for the host,
  from `codec-set.sh`'s shared list, so what a track decodes into no longer
  depends on which platform is asking. `--disable-autodetect`, so configure
  cannot link whatever dev packages the build machine happens to have — each
  one is another absolute path, and turning them off also makes the build
  reproducible, which is what the relink route needs.
- **Export narrowing, in `CMakeLists.txt`** — the thing the phase said must
  not be skipped. Derived from the header's own `FFAUDIO_API` lines, exactly as
  `ios/build.sh` and `android/build.sh` derive theirs, and it fails the build
  if the derivation ever comes back empty.
- Both build scripts end by asserting what the binary asks the OS for, and
  fail on anything beyond libSystem or libc — a stray dependency is precisely
  the failure above, and it should not be able to reach a package twice.
- CI builds **both** ways: the system-FFmpeg build still runs and is still
  tested (it is the path every developer uses), and then the static one is
  built, run through the decode checks again, and uploaded as the artifact
  that gets packed. A binary that ships should be one that decoded something.
- `FFAUDIO_REQUIRE_LGPL` is now on for macOS and Linux too. It could not be
  before — apt's and brew's FFmpeg are GPL-enabled, which is why only Windows
  was ever held to it. A build the repo configured itself has no such excuse.

Proven rather than asserted: the static macOS payload is 1.9MB, exports 16
symbols and no more, links only `libSystem`, `libz` and three system
frameworks, and reports `LGPL version 2.1 or later` through
`FFmpegBuild.License` on a machine whose own FFmpeg reports GPL. Packed
locally and switched on, Flower's `RequiresFfmpeg` suite goes **53 passed / 0
failed** — the same suite that was 39/53 against the Homebrew-linked package.

### What is not

Shipping the corresponding source alongside a static artifact — the scripts
are the relink route and `FFmpegBuild.Configuration` reads the configure line
back out of the artifact, but nothing assembles or attaches a source archive.
And the licence read itself.

**Do not publish before a licence read that is not this document.** Everything
above is the constraint as the repo already understands it, not advice. What
changed is that a binary can now be asked whether it satisfies it, which is a
different thing from having decided that it does.

## Phase 6 — Packaging and CI — **Done for the desktops, packed but unproven on the phones**

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

### What was built

Five payload projects under `packaging/`, plus the binding, all six packed by
CI's `pack` job out of the natives the test and checks jobs built — not a
rebuild, which is the same argument the publish job already makes for pushing
pack's exact bytes.

`osx-arm64`, `linux-x64` and `win-x64` are plain `runtimes/<rid>/native/`
payloads. The mobile two are `buildTransitive/` targets instead, for the
reason above, and their TFM folders name a platform version (`net10.0-ios12.2`,
`net10.0-android21.0`) because NuGet refuses a bare `net10.0-ios`. Each RID is
the one the runner that built it actually is; osx-x64 and arm64 Linux are
absent rather than claimed, because no machine here can make them.

Nothing depends on anything else: the binding does not drag a native in, since
which native an app wants is the app's decision.

The failure this phase invites is a payload package that packs nothing and
publishes anyway, so each project names one file that must exist and
`EnsurePayloadExists` stops the pack if it does not. Verified by watching it
fire.

Proven end to end on macOS only: a scratch console app referencing
`FFAudio.NET` and `FFAudio.NET.macOS` from a folder feed, and nothing else,
decodes a FLAC — 44100Hz mono, 264600 bytes, the dylib resolved out of
`runtimes/` with no `FFAUDIO_LIBRARY` and no copy step. The mobile packages
have been packed and their layout inspected; no phone project has consumed
one yet, and that is the next thing to do.

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

**Phases 0, 1, 3, 4 and 6 are done**; **2 and 5 are half done**: the library has its
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
