# Build Flower from source

Instructions for building Flower from source. For an installer, use the [latest release](https://github.com/yanos/Flower/releases/latest).

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Git (for cloning the repository and reading version tags)
- Platform-specific tools for macOS, iOS, and Android, as described below

Native audio libraries are included; FFmpeg doesn't need a separate installation.

```bash
git clone https://github.com/yanos/Flower.git
cd Flower
```

## Desktop

**Windows and Linux:**

```bash
dotnet run --project Flower.Desktop
```

**macOS:** install Xcode and the .NET macOS workload, then run:

```bash
dotnet workload install macos
dotnet run --project Flower.MacOS/Flower.MacOS.csproj
```

Use an Xcode version supported by your .NET workload. `Flower.Desktop` also runs on macOS, but the dedicated macOS app includes native AirPlay selection and Now Playing integration.

## Server and browser player

Build and run both together:

```bash
dotnet workload install wasm-tools
dotnet run --project Flower.Server
```

To build only the server, without the browser player:

```bash
dotnet run --project Flower.Server -p:IncludeWebUi=false
```

See [Self-hosting](SELF-HOSTING.md) for library setup, configuration, and device pairing.

**Docker image** (from the repository root, with Docker BuildKit):

```bash
docker build -f docker/Dockerfile -t flower-server .
```

For a shallow clone or source archive without Git tags, add `--build-arg VERSION=1.2.3`, replacing the example with your build's version. Use `--build-arg INCLUDE_WEB_UI=false` to exclude the browser UI.

## Phone apps

The phone apps aren't available in app stores yet. Building them requires the platform SDK, a matching .NET workload, and signing credentials for iOS.

### iPhone / iPad

On a Mac with Xcode and the .NET `ios` workload, connect your device and run:

```bash
scripts/deploy-ios-device.sh
```

The script registers the connected device, then publishes and deploys the app. See its header for signing requirements and runtime options.

### Android

Install the Android SDK and .NET `android` workload, then run:

```bash
dotnet build Flower.Android/Flower.Android.csproj
```

Run the app from your IDE on an emulator or connected device. Debug builds produce an `.apk`; release publishing produces an `.aab` for Google Play.

## Tests and contribution guidelines

Run the main test suite:

```bash
dotnet test Tests/Flower.Tests/Flower.Tests.csproj
```

For additional suites, see [Tests](../Tests/README.md). For project structure and code conventions, see [CLAUDE.md](../CLAUDE.md). If you change native audio code, follow the [native build instructions](../native/miniaudio/README.md).
