# Vendored miniaudio natives (Android/iOS)

Desktop already gets `libminiaudio` via the community `Miniaudio-CS` NuGet package (`~/.nuget/packages/miniaudio-cs/`). That package ships no `android-*`/`ios-*` binaries at all, so mobile is built here instead - no NuGet, no hosting, just compiled binaries checked directly into `Flower.Android/libs/` and `Flower.iOS/Frameworks/`.

## Flower's own C in here

Three files alongside the vendored header are ours:

- `flower_coreaudio_diagnostics.h` - iOS-only counters around miniaudio's
  CoreAudio render callback (callback gap, host-time gap, callback duration,
  abrupt-sample and repeated-buffer detection), implemented in `impl.c`.
  `vendor/miniaudio.h` carries a small patch calling into them from
  `ma_on_output__coreaudio`; that patch is the only edit to the vendored
  source and has to be reapplied if it is re-pinned.
- `flower_audio_bridge.h` and `flower_audio_bridge.c` - the PCM hand-off that
  keeps managed code off the real-time thread on Mono platforms. Read the
  header's comment for why it exists; the short version is that Mono's GC
  suspends the render thread, and iPhone logs caught it doing so for 668ms
  mid-track. It carries the pipeline's float and runs the output stage (EQ,
  volume ramp, declick, dither) in the callback, as a port of
  `Flower/Audio/OutputStage.cs` held to it sample for sample. That is why the
  bridge turns floating-point contraction off and Android links `libm`:
  either side fusing a multiply-add the other does not, or rounding
  differently from `MathF.Round`, would break that match.

The bridge is the library's second translation unit rather than part of
`impl.c`, and it includes no miniaudio header: it uses `struct ma_device*` only
as an opaque key and never dereferences it. So it builds on its own
(`clang -dynamiclib flower_audio_bridge.c` gives a library that links only
libc), and that is what will let desktop ship it beside the NuGet's miniaudio
(`docs/AUDIO-QUALITY-PLAN.md`, Phase 6 step 6). Keep `miniaudio.h` out of it.

Both are absent from the desktop NuGet binary, which is why `MiniaudioSink`
probes for them rather than assuming a platform.

## Why this exact miniaudio commit

`vendor/miniaudio.h` is pinned to commit `350784a9467a79d0fa65802132668e5afbcf3777` (tag `0.11.22`) - **not** the latest miniaudio release. This is the exact commit `Miniaudio-CS` 1.0.4's own C# bindings were generated against (confirmed via `Miniaudio-CS`'s own pinned git submodule at its published commit `9c64a8fb404c965a538584bf76a691f0d4ffccd6`, and cross-checked against the version string embedded in the desktop `libminiaudio.dylib`). Building a different miniaudio version here would risk a silent ABI mismatch (`ma_context`/`ma_device` struct layout, enum values) against the existing C# struct definitions - do not bump this without also verifying `Miniaudio-CS` itself was rebuilt against the same newer commit.

## Android

```
export ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/<version>
native/miniaudio/android/build.sh
```

Builds `arm64-v8a` (real device), `x86_64` (emulator), `armeabi-v7a` (stretch/older devices) via the NDK's CMake toolchain, strips debug info, and copies each into `Flower.Android/libs/<abi>/libminiaudio.so`. Those are wired into `Flower.Android.csproj` via explicit `<AndroidNativeLibrary>` items (not implicit globbing).

**Do not add `-fvisibility=hidden`** to the CMake build - it hides every `ma_*` symbol from `Miniaudio-CS`'s P/Invoke resolution (this was hit and fixed during initial bring-up: 0 exported symbols with it, 1169 without).

The output must be named `libminiaudio.so` exactly - `Miniaudio-CS`'s generated bindings use the literal `DllImport("miniaudio")` string, and .NET's Android P/Invoke resolution maps that to `libminiaudio.so` by convention. Get the name right and no custom `DllImportResolver` is needed.

For a debug build that catches native heap corruption early (relevant given `MiniaudioSink.cs`'s `ma_device` struct-sizing padding - AAudio/OpenSL|ES are untested backends), configure with `-DFLOWER_MINIAUDIO_ASAN=ON`.

## iOS

`ios/build.sh` builds a dynamic `miniaudio.xcframework` (device `arm64-apple-ios` + Apple Silicon simulator `arm64-apple-ios-simulator`), wired into `Flower.iOS.csproj` via `<NativeReference Kind="Framework">`. See that script and `Flower.iOS.csproj` for details.

## Verification

Real gapless playback test on device/emulator/simulator is required before considering either platform done - see the plan doc / task list for the exact checklist (including an API 23-25 Android image specifically - the app's minSdk floor since the Avalonia 12 upgrade, see Flower.Android.csproj - to confirm the OpenSL|ES fallback engages when AAudio isn't available).
