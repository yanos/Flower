# Tests

Tests are implemented once and grouped by subject. Platform projects contain
native integrations or provide the runner needed to execute shared tests on a
phone runtime.

| Project | Purpose |
| --- | --- |
| `Flower.Tests` | Shared unit and integration behavior: library, import, playlists, sync, persistence, networking, and audio/playback using fakes |
| `Flower.Tests.UI` | Headless views, controls, app-shell view models, input/binding behavior, and platform/dispatcher wiring |
| `Flower.Tests.Decoding` | Decoder, HTTP streaming, PCM validation, and playback integration with real decoding |
| `Flower.Server.Tests` | In-process server/API, security, and configuration tests |
| `Flower.Tests.macOS` | Native macOS Trash and Music/iTunes export discovery |
| `Flower.Tests.Windows` | Native Windows Recycle Bin, and junctions in a library folder |
| `Flower.Tests.Decoding.Scenarios` | Portable decoding scenarios, fixtures, and sample comparisons, without a test framework; shared by desktop and phone runners. Also `OutputStageHarness`, which drives the C output stage in `libflowerbridge` with no device, and `OutputStageChecks`, the phone runners' checks of it |
| `Flower.Tests.Decoding.iOS` | Focused decoding and native-output-stage runner; NativeAOT by default through the script, matching the shipped iOS runtime |
| `Flower.Tests.Decoding.Android` | Focused decoding and native-output-stage runner for an emulator or device |
| `Flower.Tests.iOS` | Existing Mono/xUnit iOS runner; runtime and build settings retained pending upstream NativeAOT support |
| `Flower.Tests.iOS.Suite` | Execution bundle for the Mono runner; compiles shared/UI/decoding sources into one assembly, with no copied test implementations |

`TestSupport/` contains linked sources compiled into each client suite through
`ClientTests.props`. Assembly attributes, xUnit collection definitions, data/trash
redirects, and timer guards belong to each executing test assembly. In particular,
the directory-isolation checks run in every suite. The iOS execution bundle keeps
one headless session and one dispatcher, as the original combined suite did.
Test namespaces generally retain their existing names to keep method filters
stable; select a subject by its project.

Use `Flower.Tests` for ordinary unit tests that can call production logic
without a window, dispatcher pumping, or `MainViewModelHarness`. This includes
mobile album metadata, search history and suggestions, tab editing, filter
models, browser filtering and sorting, artist icon metadata, navigation history,
selection/gesture state machines, menu construction, sync-result wording,
Home shelf rules, settings models and current-track metadata. Keep input, focus, bindings,
virtualization, and command/navigation integration in `Flower.Tests.UI`.
Both projects currently import the common headless infrastructure; placing a
unit test in the shared project does not require removing those shared packages.
Playback and queue rules belong in the shared project even when their types
are named `ViewModel`. Its playback pipeline integration tests use fake decoders
and sinks; some retain a headless dispatcher for real event delivery. That is
integration coverage, distinct from ordinary unit tests and UI input/binding
checks. The UI project retains focused tests of backend events reaching the
dispatcher and real queue views accepting input.
All ordinary Fact/Theory methods now live in the shared project. Mixed fixtures
are split so UI-specific tests keep their window, control or dispatcher setup.
Shared integration tests may still use scratch storage, timers or headless
platform services; project placement alone does not make a test a unit test.
The browser model tests await rebuilds directly without a dispatcher; their
debounce regression also waits for the scheduled grid publication. Recently
Played's row reuse and layout tests construct Home directly, while playback
and settings forwarding remain in the UI suite.
The cleanup and its counts are recorded in
[`docs/MOBILE-TEST-AUDIT.md`](../docs/MOBILE-TEST-AUDIT.md).

Tests specific to one desktop OS live in its platform project. Those projects
exclude native test sources on other hosts, so the desktop solution filter can
be built anywhere without executing unsupported native calls. POSIX permission
checks remain shared because they apply to several platforms; they report skips
when unsupported or when the process can bypass read-only permissions. Desktop
folder watching and empty-folder import behavior also report explicit iOS skips.

## Running tests

Run all desktop suites, including the server and the current OS's native tests:

```sh
dotnet build Tests/Flower.Desktop.Tests.slnf -c Release -p:IncludeWebUi=false
FLOWER_REQUIRE_DECODERS=FFmpeg dotnet test Tests/Flower.Desktop.Tests.slnf -c Release --no-build -m:1
```

`FLOWER_REQUIRE_DECODERS=FFmpeg` makes missing native decoding support a failure
instead of allowing the portable scenarios to exercise no decoder. CI and the
phone scripts set it. In PowerShell, set `$env:FLOWER_REQUIRE_DECODERS = 'FFmpeg'`
before running `dotnet test`.

Run a subject while iterating:

```sh
dotnet test Tests/Flower.Tests/Flower.Tests.csproj
dotnet test Tests/Flower.Tests.UI/Flower.Tests.UI.csproj
dotnet test Tests/Flower.Tests.Decoding/Flower.Tests.Decoding.csproj
dotnet test Tests/Flower.Tests.macOS/Flower.Tests.macOS.csproj
dotnet test Tests/Flower.Server.Tests/Flower.Server.Tests.csproj
```

`Flower.Tests` and `Flower.Tests.UI` never touch FFmpeg, so they are the fast
loop. The output stage's tests in `Flower.Tests/Audio` (`OutputStageTests`,
`CanonicalFormatTests`, `EqualizerTests`, `AudioCallbackTimingTests`) run the C
in `libflowerbridge`, through `OutputStageHarness`, against the desktop build
`native/miniaudio/desktop/FlowerBridge.props` copies beside the tests and in
the Mono iOS suite; `OutputStageChecks` runs a few of the same
properties in both phone runners. All of them fail rather than skip if the
bridge is missing. Real decoding is confined to `Flower.Tests.Decoding`, whose classes carry
`[Trait("Category", "RequiresFfmpeg")]` (and `RequiresClapModel` for the CLAP
reference tests); no other project uses either trait.

Switches, all environment variables:

| Variable | Effect |
| --- | --- |
| `FLOWER_REQUIRE_DECODERS=FFmpeg` | A missing decoder fails rather than skips (CI and the phone scripts set it) |
| `FLOWER_CLAP_MODEL_DIR` | Where `ClapEmbeddingReferenceTests` finds the model; default `~/.cache/flower-semantic-audio/export`, skipped when absent |
| `FLOWER_REQUIRE_CLAP_MODEL=1` | A missing model fails rather than skips (CI's Linux leg only) |
| `FLOWER_TEST_SEED=<n>` | Runs a client suite in a seeded random order (`ShuffleOrderer`), to check that shared state is not order-dependent |
| `FLOWER_TEST_ARGS` | xUnit `-class`/`-method` filters for the full iOS runner |

Run on phone runtimes using the existing local SDK/workload prerequisites:

```sh
scripts/ios-tests.sh                 # shared + UI + decoding, on Mono
scripts/ios-decoding-tests.sh        # focused decoding, on NativeAOT
scripts/android-decoding-tests.sh    # focused decoding, local emulator/device
```

The iOS scripts accept a simulator name and `IOS_SIMULATOR_RUNTIME`. The decoding
script retains its `--mono` option for diagnosis. CI runs the Android
decoding runner on an x86_64 emulator, beside the app's build (see "Android"
below).

## Shared test state

Every client suite shares one Avalonia application and dispatcher across the
whole assembly (`AvaloniaTestIsolationLevel.PerAssembly`, see
`TestAppBuilder`), and several process-wide statics. What keeps that safe, all
in `TestSupport/`:

- **A safe floor.** `AssemblySetup`'s module initialisers point
  `PlatformDataDirectory.Current`, `FileTrash.Override` and
  `FlowerFolder.Override` at temp directories before any test runs, so even an
  unpinned test or a late fire-and-forget save never reaches the developer's
  real Application Support folder, trash or `~/Music/Flower`. A default
  `AppSettings` once overwrote the real `settings.json` (and its one backup)
  this way. `TestDataDirectoryIsolationTests` asserts the floor holds.
- **Pinning.** A class whose subject writes app data derives from
  `PinnedDataDirectory` (or `IsolatedHome`) and is in
  `[Collection("PlatformDataDirectory")]`, because the override is one shared
  static; `TestDataDirectoryIsolationTests` fails any subclass missing the
  collection. Dispose restores the floor, not `null`.
- **Leaked timers.** `TimerLeakGuard` fails the test after one that leaves a
  Flower-owned `DispatcherTimer` running, naming the test that started it.
  Objects that start timers (a `MainViewModel`) are handed to
  `PinnedDataDirectory.Own` so a failed assertion still disposes them.
- **Thread pool.** `AssemblySetup` raises the pool minimum, because
  `SpinWait`-driven tests starved real async continuations under parallel runs.
- **Other process-wide state** (`GaplessFormat`, native text fields, album art
  loading) has its own xUnit collection: `GaplessFormatCollection`,
  `NativeTextFieldCollection`, `AlbumArtLoaderCollection`.

`Flower.Server.Tests` does not use `ClientTests.props`; its own helpers
(`FlowerServerFixture`, `GuardedRequest`) live in its `TestSupport/` folder.

## CI

The test build skips publishing the browser bundle (`IncludeWebUi=false`); the
browser has its own CI build and release-image bundle validation. Building before
running tests also avoids running timing-sensitive tests alongside compilation.

CI has seven explicitly named jobs: **Linux**, **Windows**, **macOS**,
**iOS tests (Mono)**, **iOS build and decoding (NativeAOT)**, **Android**, and
**Web**. The two iOS jobs remain separate temporarily so their long builds run
in parallel; the Mono runtime and runner settings are unchanged.

The Linux job first checks that the checked-in bridge binaries were built from
the bridge source in the tree: `native/miniaudio/{desktop,android,ios}/build.sh`
each write `flowerbridge-source.sha256` beside what they build, so a change to
`flower_audio_bridge.{c,h}` without all three rebuilds fails there. The desktop
jobs then run those binaries through the output stage's tests on Linux,
Windows and macOS.

Each desktop job builds `Flower.Desktop.Tests.slnf` once and its platform app,
then executes shared, UI, decoding, native-platform (where applicable), and
server suites in named steps with `--no-build`. macOS uses `xcode-27` for both
its native app build and tests, since that head needs Apple's SDK headers.
Only Linux runs the CLAP reference tests: it downloads the Vibe Playlists model
from the repo's release (cached on `SemanticAudioModelManifest.cs`) and sets
`FLOWER_REQUIRE_CLAP_MODEL`; the other desktops skip them.

The iOS jobs pin the workload (`--version 10.0.402`) and `Xcode_27.0`, because
a newer workload set can demand a newer Xcode (`NETSDK1140`); move workload,
TFMs and Xcode together. **iOS tests (Mono)** runs `scripts/ios-tests.sh`;
**iOS build and decoding (NativeAOT)** builds `Flower.iOS` as it ships
(`FlowerNativeAot`, `_IsPublishing`, ad hoc signing) and runs
`scripts/ios-decoding-tests.sh`. **Android** builds the head and the decoding
runner without running it; **Web** builds `Flower.Web` with `wasm-tools`.

Tags additionally package and upload the desktop installer in each desktop job.
Release and image publication wait for all seven platform jobs, so a failed
build, test, or installer prevents publication. There is no skipped installer
matrix with an unresolved name on ordinary branch pushes.

Cache inputs cover every project and the shared test configuration. Superseded
branch runs are canceled; tag runs finish. The Android job runs the decoding
runner on an x86_64 emulator, KVM-accelerated, through
`reactivecircus/android-emulator-runner`, which owns the emulator; the script
is handed its serial rather than starting one of its own.

Release publication lives in `tests.yml` rather than a workflow of its own
because that is the only way it can wait: a tag push fires every `on: push`
workflow at once and `needs` reaches only jobs in the same file, so a separate
`publish-server-image.yml` once pushed `latest` for v0.2.0 off a tree whose
macOS, Windows and Android jobs then failed. Keep every job in the publish
step's `needs`. The image is built on tags only, so check a Dockerfile change
with `docker compose build` from `docker/` before tagging; installers can be
tried with `scripts/package-desktop.sh dmg|appimage|setup` on their own OS.
A tag is not a private act: MinVer reads it, and every installed desktop is
offered it as an update (`docs/AUTO-UPDATE-PLAN.md`).

## Why the runners are built this way

### The Mono iOS suite (`Flower.Tests.iOS`)

- **Build time.** `MtouchLink=None` compiled the whole BCL (28 minutes locally,
  2h07m in CI, for three minutes of tests); `SdkOnly` trims only the framework
  and leaves the suite, xunit, Flower and Avalonia whole so reflection still
  finds every test. `MtouchUseLlvm=false` uses Mono's own code generator: LLVM
  was nine tenths of what remained. Everything is still compiled ahead of time
  with no JIT. `MtouchInterpreter=all` was tried and is off so the leg runs
  compiled code, as a phone does. The app ships on NativeAOT, not this.
- **Load-bearing exclusions.** xunit's `buildTransitive` targets (they demand an
  app host and generate their own `Main`); the VSTest packages
  (`Microsoft.VisualStudio.TestPlatform.Common` does not AOT-compile). The runner
  drives xunit's public runner pieces rather than `ConsoleRunner.EntryPoint`,
  which subscribes to `Console.CancelKeyPress` and throws
  `PlatformNotSupportedException` on iOS before running anything.
- **Scene lifecycle.** Both iOS runners adopt it (`Tests/iOSRunner`) because
  iOS 27 kills an app that does not, with an `EXC_BREAKPOINT` in
  `UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption` before any
  output, which reads exactly like a hung run.
- **Hangs.** The app reports verbosely, so the script prints the tests that
  started and never finished instead of per-test lines.
- **NativeAOT.** The full suite cannot run there yet: xunit discovers tests by
  reflection over the assembly file, which NativeAOT lacks, and
  Avalonia.Headless.XUnit is not built against xunit's NativeAOT edition.
  As of November 2026, xUnit 4 ships that edition (`xunit.v3.aot.*`, source
  generated discovery, its own `ISelfExecutingCodeGenTestCase` extension
  point), but Avalonia.Headless.XUnit 12.1.5 still targets xUnit 3.2.2, and
  the one xUnit 4 adapter (`ProTest.Avalonia.Headless.XUnit.v4`) is
  reflection-only. xUnit 4 also drops Mono, so this suite stays on xUnit 3
  until an Avalonia adapter for the AOT edition exists. That is the change to
  watch for.

### Decoding checks (`Flower.Tests.Decoding.Scenarios`)

`DecodeChecks.RunAll()` answers *does this platform turn a track into the right
audio?* on the platform itself. Both streaming bugs so far (VLC's mp4 demuxer
refusing an unseekable stream; .NET's mobile `HttpClientHandler` having no
synchronous path) were invisible to a green desktop suite and found by a person
listening to a phone.

- **Fixtures** are committed (`Fixtures/regenerate.sh`) because a phone has no
  encoder: the same 440Hz sine as WAV/FLAC/ALAC/MP3/AAC at 48kHz and 44.1kHz
  (44.1kHz is the one rate the pipeline resamples), plus AIFF, APE, Vorbis and
  Opus at 48kHz. Each is decoded from disk, over loopback, from a server
  refusing ranges, and from one refusing HEAD (`Flower.Server` maps streams with
  `MapGet`, so real streams find their length via the ranged-GET probe).
  `LoopbackMediaServer` honours the end of a range for the same reason.
- **Oracle** is a property of the fixture: lossless at the pipeline rate must
  match byte for byte; everything else is held to `PcmOracle.ToneMismatch`
  (`PcmOracleTests` shows it rejects silence, noise and a wrong tone).
- **Refusals.** `RequiresFreshNonce` answers a reused nonce as the server does
  (`401`, `nonce-reused`), because a once-signed stream URL fetched several times
  used to play ~130 bytes of JSON. A check that cannot refuse a request cannot
  find a bug about being refused. `ServesAPortalPage` holds
  `SeekableHttpStream.ProtocolErrorFor` to refusing a 200 that is not audio;
  `RefuseBodiesWith429` reproduces throttled bodies behind a successful probe
  (docs/CITED-DECISIONS.md #2b).
- **Per decoder.** The suite runs once per available decoder (`DecoderUnderTest`)
  so a decoder change cannot move the streaming path out from under it;
  `FLOWER_REQUIRE_DECODERS` turns an empty decoder list into a failure. Each
  decoder gets its own sample format rather than moving `GaplessFormat`'s
  process-wide one.
- **Pressing play.** `Play()` calls `StartDecoding()` with no prepare; a check
  covers that path because every other check prepared first and missed that it
  faulted.
- **Extensions are hints.** `OriginFileExtension` describes the server's file,
  not the bytes sent; forcing a demuxer discards the probe, so
  `FfmpegTrackDecoder.DemuxerHintFor` names only the MP4 family and
  `FfmpegDecoder.OpenStream` re-probes when the hint fails.
- **Raw `TcpListener`.** Kept because `HttpListener` is unverified on a physical
  iOS device and on Android (it works on the simulator).
- **Transcripts are files** on both phones: `Console.WriteLine` does not
  reliably reach `simctl launch --console-pty`, and logcat drops lines.

### Android

`Flower.csproj` picks a desktop ffaudio payload by host (`FFAudio.NET.Linux` on
a Linux runner), and it flows to the Android heads. `android-x64` falls back to
`linux-x64` in the RID graph, so without help the glibc `libffaudio.so` wins
`lib/x86_64/` (`XA4301`) and `dlopen` fails on the emulator. Both Android heads
therefore re-reference each host's payload with `ExcludeAssets="native"`, as
they already do for `Miniaudio-CS`. A Mac cannot see this go wrong, because no
Android RID falls back to `osx-*`: only the CI emulator run, on Linux, can.

The runner's ABI follows the host (arm64-v8a on a Mac, x86_64 in CI). Three
settings are load-bearing: `EmbedAssembliesIntoApk` (Fast Deployment keeps
assemblies out of the APK, so a script-installed APK aborts); the `INTERNET`
permission, even for loopback; and a network security config permitting
cleartext to `127.0.0.1`, since API 28+ blocks plain HTTP.
