# flower-ffmpeg

A small native façade over FFmpeg's decode libraries, and the second
implementation behind `ITrackDecoder`. `flower_ffmpeg.h` is the whole
interface: eight functions over ints and byte buffers. Read its header comment
first - it says why this exists rather than `FFmpeg.AutoGen`, and why the ABI
is deliberately this narrow.

The reason it exists at all is measured, not assumed: LibVLC 3.0.x's `amem`
seam truncates every track to 16 bits before Flower sees a byte of it, on
every platform, whatever format is requested. See `docs/AUDIOPHILE-PLAN.md`,
"The 16-bit ceiling, measured". `FfmpegDecoderTests` pins the fix in place -
its first two tests decode the same 24-bit fixture twice and show the low byte
surviving one way and gone the other.

## Electing it

`AppSettings.AudioDecoder` in `settings.json` chooses which decoder runs:

```json
{ "AudioDecoder": "Ffmpeg" }
```

`FLOWER_DECODER=ffmpeg` (or `libvlc`) overrides that for one run, so an A/B
needs neither an edit nor a rebuild. Asking for FFmpeg where the façade is not
loadable is not an error - `DecoderElection` falls back to LibVLC and says so
in the log, which is the ordinary outcome on every platform in the table below
except macOS.

Electing it is also what widens the pipeline to 24 bits: the canonical PCM
format follows the decoder, since LibVLC cannot fill anything wider. See
`GaplessFormat` and `docs/AUDIOPHILE-PLAN.md`'s step three.

## Building for development (macOS)

```
native/ffmpeg/macos/build.sh
```

Finds FFmpeg through `pkg-config`, builds `libflower_ffmpeg.dylib`, and drops
it in `native/ffmpeg/artifacts/macos/`. Nothing copies it anywhere: the
managed side finds it by walking up from the test/app output directory, or
from `FLOWER_FFMPEG` if that names a file. Both are in
`FfmpegNative.Resolve`.

Without it, the `RequiresFfmpeg` tests fail rather than skip, the same way the
`RequiresLibVLC` ones do - filter them out on a machine that has not built it:

```
dotnet test Flower.Tests/Flower.Tests.csproj --filter "Category!=RequiresLibVLC&Category!=RequiresFfmpeg"
```

## Licensing - the constraint on every shipping build

Flower may link FFmpeg only under the **LGPL**. That means the FFmpeg it is
built against must be configured without `--enable-gpl` and without
`--enable-nonfree`, and Flower must ship the corresponding source offer and
keep the FFmpeg libraries replaceable (dynamically linked on desktop; on
mobile, where they are linked in, the object files or an equivalent relink
route must be offered).

**A MacPorts or Homebrew FFmpeg is not a shipping build.** Both enable GPL
components by default. They are fine for the development build above and for
running the tests; they cannot be packaged. Point `PKG_CONFIG_LIBDIR` at an
LGPL-only prefix for anything that ships.

No decoder Flower needs is GPL-only. The GPL components in a distro build are
filters and encoders this façade never touches.

## Building for development (Linux)

```
sudo apt-get install -y libavformat-dev libavcodec-dev libavutil-dev libswresample-dev
native/ffmpeg/linux/build.sh
```

The same `CMakeLists.txt` as macOS, found the same way through `pkg-config`,
landing in `native/ffmpeg/artifacts/linux/`. A distro FFmpeg is GPL-enabled and
so is a development build only, exactly as MacPorts' is.

The version floor is FFmpeg 5.1 - where `AVChannelLayout` and
`swr_alloc_set_opts2` arrived, which are the newest APIs in
`flower_ffmpeg.c`. It was briefly 7.x, which was not a requirement but the
version of the one machine this was first built on, and it kept the Linux
build from finding Ubuntu 24.04's FFmpeg 6 at all.

## Platform status

Two of the five heads are built. The same source and the same `CMakeLists.txt`
are meant to serve all of them, but "meant to" is not "does", and the plan doc
is explicit that this decoder must not be described as cross-platform until
each artifact is built, packaged and tested on real hardware:

| Platform | Artifact | Status |
|---|---|---|
| macOS | `libflower_ffmpeg.dylib` | Built and tested against MacPorts FFmpeg, and elected in real listening; built and checked on CI |
| Linux | `libflower_ffmpeg.so` | `linux/build.sh` written and wired into CI; the first CI run is what proves it - it has never been built on a Linux machine here |
| Windows | `flower_ffmpeg.dll` | Unbuilt; needs an FFmpeg build and an import-lib route |
| Android | `libflower_ffmpeg.so` per ABI | Unbuilt; needs a static NDK FFmpeg (`FLOWER_FFMPEG_STATIC`) |
| iOS | `flower_ffmpeg.xcframework` | Unbuilt; needs a static FFmpeg and `<NativeReference>` wiring |

## On CI

Both the `test` and `decode-checks` jobs build the façade on Linux and macOS,
so the FFmpeg decoder is exercised there rather than only on the one developer
machine that happens to have built it. `decode-checks` additionally sets
`FLOWER_REQUIRE_DECODERS=LibVLC,FFmpeg`, which turns a decoder the platform
turns out not to have into a failing check rather than a shorter run - the
checks loop over the decoders that loaded, so a façade that quietly stopped
building would otherwise present as a green run that checked half as much.
Windows requires only LibVLC, having no façade yet.

Android and iOS follow `native/miniaudio/`'s precedent - build scripts here,
binaries checked in beside the platform head - with the difference that FFmpeg
itself has to be cross-compiled first. Both should link FFmpeg statically into
the façade (`-DFLOWER_FFMPEG_STATIC=ON`) so one binary per ABI ships instead of
five.

## Debugging

`-DFLOWER_FFMPEG_ASAN=ON` builds with AddressSanitizer, which is worth doing
after any change to the buffer management in `flower_decoder_read` - the
scratch/pending pair and the S24 packing are where a mistake would corrupt the
heap rather than fail a test.
