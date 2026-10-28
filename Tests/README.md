# Tests

Tests are implemented once and grouped by subject. Platform projects contain
native integrations or provide the runner needed to execute shared tests on a
phone runtime.

| Project | Purpose |
| --- | --- |
| `Flower.Tests` | Shared behavior: library, import, playlists, sync, persistence, networking, and audio/playback logic using fakes |
| `Flower.Tests.UI` | Headless views, controls, view models, and integration tests that require Avalonia's dispatcher; exercises behavior, without launching the full app |
| `Flower.Tests.Decoding` | Decoder, HTTP streaming, PCM validation, and playback integration with real decoding |
| `Flower.Server.Tests` | In-process server/API, security, and configuration tests |
| `Flower.Tests.macOS` | Native macOS Trash and Music/iTunes export discovery |
| `Flower.Tests.Windows` | Native Windows Recycle Bin |
| `Flower.Tests.Decoding.Scenarios` | Portable decoding scenarios, fixtures, and sample comparisons, without a test framework; shared by desktop and phone runners |
| `Flower.Tests.Decoding.iOS` | Focused decoding runner; NativeAOT by default through the script, matching the shipped iOS runtime |
| `Flower.Tests.Decoding.Android` | Focused decoding runner for an emulator or device |
| `Flower.Tests.iOS` | Existing Mono/xUnit iOS runner; runtime and build settings retained pending upstream NativeAOT support |
| `Flower.Tests.iOS.Suite` | Execution bundle for the Mono runner; compiles shared/UI/decoding sources into one assembly, with no copied test implementations |

`TestSupport/` contains linked sources compiled into each client suite through
`ClientTests.props`. Assembly attributes, xUnit collection definitions, data/trash
redirects, and timer guards belong to each executing test assembly. In particular,
the directory-isolation checks run in every suite. The iOS execution bundle keeps
one headless session and one dispatcher, as the original combined suite did.
Test namespaces generally retain their existing names to keep method filters
stable; select a subject by its project.

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

Run on phone runtimes using the existing local SDK/workload prerequisites:

```sh
scripts/ios-tests.sh                 # shared + UI + decoding, on Mono
scripts/ios-decoding-tests.sh        # focused decoding, on NativeAOT
scripts/android-decoding-tests.sh    # focused decoding, local emulator/device
```

The iOS scripts accept a simulator name and `IOS_SIMULATOR_RUNTIME`. The decoding
script retains its `--mono` option for diagnosis. The full iOS runner accepts
`FLOWER_TEST_ARGS` for xUnit method/class filters. CI compiles the Android decoding runner alongside the app. Emulator checks remain
local-only because of the existing hosted-emulator limitations documented in
`CLAUDE.md`; this reorganization does not remove their coverage or add a new CI
requirement.

## CI

The test build skips publishing the browser bundle (`IncludeWebUi=false`); the
browser has its own CI build and release-image bundle validation. Building before
running tests also avoids running timing-sensitive tests alongside compilation.

Each desktop OS builds `Flower.Desktop.Tests.slnf` once, then executes shared, UI,
decoding, native-platform, and server suites in named steps with `--no-build`.
Linux also compiles `Flower.Desktop`, reusing that checkout and its shared build.
Mobile and browser builds remain separate parallel jobs, including the full Mono
iOS suite and focused NativeAOT decoding run. Cache inputs cover every project and
the shared test configuration. Superseded branch runs are canceled; tag runs and
existing release publication gates are retained.
