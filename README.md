# Flower

A music player for your own collection. Listen on your computer, or run a
small server to share your library with friends and family, at home or away.

**[Download Flower](https://github.com/yanos/Flower/releases/latest)** ·
[Getting started](docs/GETTING-STARTED.md) ·
[Run a server](docs/SELF-HOSTING.md)

Flower is in early development (v0.x). Desktop downloads are available;
the iPhone and Android apps aren't in the stores yet.

## What you can do

- **Play your own files.** Add a music folder and browse by album, artist or
  song. No server or Flower account needed.
- **Listen anywhere.** Stream from your Flower server, or download music in
  the app for offline listening.
- **Share your collection.** Invite friends and family with a pairing link.
  Listeners get their own playlists; administrators manage the library.
- **Make playlists your way.** Pick songs yourself, use smart playlist rules,
  or describe a mood with Vibe Playlists. Endless Play can keep the music going
  with similar songs. The AI features are optional and run on your server.
- **Choose how you listen.** Gapless playback, an equalizer, hi-res audio and
  a desktop output picker in the installed apps, with AirPlay on macOS and iOS.
- **Bring your library with you.** Edit song details and covers, and import an
  existing Music.app library on desktop, including play counts.

## Get Flower

| Platform | Download |
|---|---|
| macOS (Apple silicon) | `.dmg` from the [latest release](https://github.com/yanos/Flower/releases/latest) |
| Windows (x64) | `Setup.exe` from the [latest release](https://github.com/yanos/Flower/releases/latest) |
| Linux (x64) | `.AppImage` from the [latest release](https://github.com/yanos/Flower/releases/latest) |
| iPhone / Android | [Build from source](docs/BUILDING.md#phone-apps) |
| Web browser | Open your Flower server's address; nothing to install |

The browser player uses your browser's supported audio formats and doesn't
provide the installed apps' gapless playback, equalizer or offline downloads.

Desktop installations check for updates automatically. Beta releases are
optional in **Settings → General → Get beta releases**.

## Start listening

**Have music on your computer?** Open **Settings → Library → Add folder…**
and choose your music folder. Flower will notice new songs there automatically.

**Have an invite?** Open the `flower://pair` link on the device where you
installed Flower, then choose **Pair**. Each invite works once and expires
after ten minutes.

The [getting started guide](docs/GETTING-STARTED.md) covers phones, browser
listening, offline downloads and pairing help.

## Run your own server

Keep your collection on one computer or NAS and listen from your other devices.
The server includes a browser player and runs on Linux, macOS or Windows.
A Docker image is available for amd64 and arm64:
`ghcr.io/yanos/flower-server:latest`.

Follow the [self-hosting guide](docs/SELF-HOSTING.md) to install it, add your
music and invite listeners. It also covers backups and listening away from home.

## Help and documentation

- [Getting started](docs/GETTING-STARTED.md)
- [Adding, editing and removing songs](docs/MANAGING-SONGS.md)
- [Self-hosting](docs/SELF-HOSTING.md)
- [Privacy](docs/PRIVACY.md) and [security](docs/SECURITY.md)
- [All guides](docs/README.md)

Found a problem? Use **Report an Issue** in Flower, or
[open a GitHub issue](https://github.com/yanos/Flower/issues).
Release builds send crash reports by default; you can turn this off in
Settings. See [Privacy](docs/PRIVACY.md) for what is included.

## Build and contribute

Flower uses .NET 10 and Avalonia. See [Building Flower](docs/BUILDING.md) for
setup and run commands, [Tests](Tests/README.md) for checks, and
[CLAUDE.md](CLAUDE.md) for architecture and contribution conventions.

## License

[Apache 2.0](LICENSE). Third-party licenses and FFmpeg details are in
[NOTICE](NOTICE).
