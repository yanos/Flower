# Building Flower

For ready-to-install desktop apps, use the
[latest release](https://github.com/yanos/Flower/releases/latest).
These instructions are for running Flower from source.

## Requirements

- .NET 10 SDK.
- Git, to clone the repository and read version tags.
- The platform tools listed below for macOS, iOS or Android.

Native audio libraries are included in the repository. You don't need to
build or install FFmpeg separately.

```bash
git clone https://github.com/yanos/Flower.git
cd Flower
```

## Desktop

On Windows or Linux:

```bash
dotnet run --project Flower.Desktop
```

On macOS, install Xcode and the .NET macOS workload, then run the macOS app:

```bash
dotnet workload install macos
dotnet run --project Flower.MacOS/Flower.MacOS.csproj
```

Use an Xcode version supported by the installed .NET workload. The generic
`Flower.Desktop` project also runs on macOS, but lacks the macOS app's native
AirPlay picker and Now Playing integration.

## Server and browser player

```bash
dotnet workload install wasm-tools
dotnet run --project Flower.Server
```

The server build includes the browser UI. To build just the server:

```bash
dotnet run --project Flower.Server -p:IncludeWebUi=false
```

For library setup, pairing and configuration, see [Self-hosting](SELF-HOSTING.md).

To build a Docker image from the repository root, use Docker with BuildKit:

```bash
docker build -f docker/Dockerfile -t flower-server .
```

For a shallow clone or source archive without tags, add
`--build-arg VERSION=1.2.3` with the version you're building.
`--build-arg INCLUDE_WEB_UI=false` leaves out the browser UI.

## Phone apps

The phone apps aren't in the stores yet. Building them requires the platform's
SDK, the matching .NET workload and, for an iPhone, signing credentials.

### iPhone / iPad

On a Mac with Xcode and the `ios` workload installed, connect your iPhone and run:

```bash
scripts/deploy-ios-device.sh
```

The script registers the connected device before publishing and deploying.
Its header explains the signing requirements and runtime options.

### Android

Install the Android SDK and the .NET `android` workload, then build:

```bash
dotnet build Flower.Android/Flower.Android.csproj
```

Use your IDE to run the app on an emulator or a connected phone. Release
publishes produce an `.aab` for Google Play; debug builds produce an `.apk`.

## Tests and contribution guidelines

For a quick check:

```bash
dotnet test Tests/Flower.Tests/Flower.Tests.csproj
```

See [Tests](../Tests/README.md) for desktop, server and phone suites, and
[CLAUDE.md](../CLAUDE.md) for project layout, code style and test conventions.
If you change native audio code, follow the
[native build instructions](../native/miniaudio/README.md).
