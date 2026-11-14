# Vendored natives: miniaudio (Android/iOS) and libflowerbridge (everywhere)

Desktop already gets `libminiaudio` via the community `Miniaudio-CS` NuGet package (`~/.nuget/packages/miniaudio-cs/`). That package ships no `android-*`/`ios-*` binaries at all, so mobile is built here instead - no NuGet, no hosting, just compiled binaries checked directly into `Flower.Android/libs/` and `Flower.iOS/Frameworks/`.

Flower's render-thread bridge is a library of its own, `libflowerbridge`, on
every platform: built beside miniaudio by the two phone scripts, and for the six
desktop RIDs by `desktop/build.sh` into `desktop/runtimes/<rid>/native/`. All of
it is checked in, so every head runs from a clean clone with no native
toolchain.

## Flower's own C in here

Three files alongside the vendored header are ours:

- `flower_coreaudio_diagnostics.h` - iOS-only counters around miniaudio's
  CoreAudio render callback (callback gap, host-time gap, callback duration,
  abrupt-sample and repeated-buffer detection), implemented in `impl.c`.
  `vendor/miniaudio.h` carries a small patch calling into them from
  `ma_on_output__coreaudio`; that patch is the only edit to the vendored
  source and has to be reapplied if it is re-pinned.
- `flower_audio_bridge.h` and `flower_audio_bridge.c` - libflowerbridge, the
  PCM hand-off that keeps managed code off the real-time thread. Read the
  header's comment for why it exists; the short version is that Mono's GC
  suspends the render thread, and iPhone logs caught it doing so for 668ms
  mid-track. It carries the pipeline's float and runs the output stage (EQ,
  volume ramp, declick, dither) in the callback, which is why desktop runs it
  too: it is Flower's only output stage and its only render path, with the
  controls applied after the queue. The callback also times itself (gaps,
  late callbacks, render overruns) for the render watchdog. Every build turns
  floating-point contraction off (`-ffp-contract=off`, which GCC needs on top
  of the source's pragma) and Linux and Android link `libm`, so every
  platform renders the same bytes from the same input. The tests drive the
  callback directly through `OutputStageHarness` (in
  `Tests/Flower.Tests.Decoding.Scenarios`): the property tests in
  `Tests/Flower.Tests/Audio` on each desktop and in the Mono iOS suite, and
  `OutputStageChecks` on the phone runners
  (`scripts/ios-decoding-tests.sh`, `scripts/android-decoding-tests.sh`).
  Run them after changing it.

The bridge includes no miniaudio header: it uses `struct ma_device*` only as an
opaque key and never dereferences it, and miniaudio calls its render callback
through a plain function pointer, which may live in another library. That is
what lets desktop ship it beside the NuGet's miniaudio. Keep `miniaudio.h` out
of it. Its exports are exactly `FLOWER_AUDIO_BRIDGE_API` (every build is
`-fvisibility=hidden`), 21 symbols, which the desktop build script checks.

**After changing it, rerun all three build scripts.** Each writes
`flowerbridge-source.sha256` beside its binaries, and CI's Linux job fails a
tree whose bridge source no longer matches any of them.

The CoreAudio diagnostics are absent from the desktop NuGet binary, which is
why `MiniaudioSink` probes for them rather than assuming a platform. The
bridge is probed the same way, and a platform with no build of it cannot play:
`MiniaudioSink` refuses to open a device without one.

## Why this exact miniaudio commit

`vendor/miniaudio.h` is pinned to commit `350784a9467a79d0fa65802132668e5afbcf3777` (tag `0.11.22`) - **not** the latest miniaudio release. This is the exact commit `Miniaudio-CS` 1.0.4's own C# bindings were generated against (confirmed via `Miniaudio-CS`'s own pinned git submodule at its published commit `9c64a8fb404c965a538584bf76a691f0d4ffccd6`, and cross-checked against the version string embedded in the desktop `libminiaudio.dylib`). Building a different miniaudio version here would risk a silent ABI mismatch (`ma_context`/`ma_device` struct layout, enum values) against the existing C# struct definitions - do not bump this without also verifying `Miniaudio-CS` itself was rebuilt against the same newer commit.

## Desktop (libflowerbridge only)

```
colima start   # or any running Docker
native/miniaudio/desktop/build.sh
```

Builds `osx-arm64`/`osx-x64` natively, `linux-arm64`/`linux-x64` with GCC in a
Debian container (x64 by its cross compiler, so nothing runs emulated), and
`win-x64`/`win-arm64` with llvm-mingw's clang - not MSVC, whose C11
`<stdatomic.h>` is still behind `/experimental:c11atomics`. Both images are
pinned by digest in the script. It then checks each binary: 21 exports, no
dependency past libSystem / glibc and libm / the UCRT and kernel32, glibc no
newer than 2.17, and no fused multiply-add in the disassembly.
Linux builds also enable stack protection, fortified libc calls and full
RELRO; the script checks for a RELRO segment and eager symbol binding.
On arm64, stack protection also references glibc's loader for its guard value;
that dependency is held to the same glibc version floor.

`FlowerBridge.props` copies the one for the RID being built (the SDK's own
platform when there is no RID) flat beside the app, where `DllImport` finds it
with no resolver; the macOS head bundles it into `Contents/MonoBundle`, which
rewrites its install name - hence `-headerpad_max_install_names`. It is
imported by `Flower.Desktop`, `Flower.MacOS` and the desktop test suites
(through `Tests/ClientTests.props`), never by `Flower.csproj`, whose
references reach the phone heads too.

## Android

```
export ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/<version>
native/miniaudio/android/build.sh
```

Builds `arm64-v8a` (real device), `x86_64` (emulator), `armeabi-v7a` (stretch/older devices) via the NDK's CMake toolchain, strips debug info, and copies each into `Flower.Android/libs/<abi>/` as `libminiaudio.so` and `libflowerbridge.so`. `Flower.Android.csproj` lists neither: the Android SDK globs `libs/<abi>/*.so` on its own, and naming them again packages them twice (XA4301).
Both libraries enable stack protection, fortified libc calls and full RELRO.
The script checks each copied library for a RELRO segment and eager symbol
binding.

**Do not add `-fvisibility=hidden`** to the `miniaudio` target - it hides every `ma_*` symbol from `Miniaudio-CS`'s P/Invoke resolution (this was hit and fixed during initial bring-up: 0 exported symbols with it, 1169 without).

The output must be named `libminiaudio.so` exactly - `Miniaudio-CS`'s generated bindings use the literal `DllImport("miniaudio")` string, and .NET's Android P/Invoke resolution maps that to `libminiaudio.so` by convention. Get the name right and no custom `DllImportResolver` is needed.

For a debug build that catches native heap corruption early (relevant given `MiniaudioSink.cs`'s `ma_device` struct-sizing padding - AAudio/OpenSL|ES are untested backends), configure with `-DFLOWER_MINIAUDIO_ASAN=ON`.

## iOS

`ios/build.sh` builds dynamic `miniaudio.framework` and `flowerbridge.framework` folders (device `arm64-apple-ios` + Apple Silicon simulator `arm64-apple-ios-simulator`), wired into `Flower.iOS.csproj` via `<NativeReference Kind="Framework">` and, for NativeAOT, `<DirectPInvoke>`. See that script and `Flower.iOS.csproj` for details.

## Verification

Real gapless playback test on device/emulator/simulator is required before considering either platform done - see the plan doc / task list for the exact checklist (including an API 23-25 Android image specifically - the app's minSdk floor since the Avalonia 12 upgrade, see Flower.Android.csproj - to confirm the OpenSL|ES fallback engages when AAudio isn't available).
