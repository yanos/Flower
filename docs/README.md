# Flower

Flower is a free music player for files you already own. Play music on your device, or run Flower Server to stream your collection from a computer or NAS. Neither requires an account, subscription, or hosted music service.

[Download Flower](https://github.com/yanos/Flower/releases/latest) · [Installation instructions](GETTING-STARTED.md#get-the-app)

## Features

- Play local music files, including MP3, FLAC, ALAC, Ogg Vorbis, Opus, and more.
- Browse albums, edit song information and artwork, and create playlists.
- Use gapless playback and an equalizer in the installed apps.
- Stream music from your own server to Flower apps or a browser, including for people you invite.
- Download songs from your server for offline listening in the installed apps.
- Optionally enable Vibe Playlists and Endless Play. Song analysis runs on your server.

## Guides

| Task | Guide |
|---|---|
| Install Flower and play local files | [Getting started](GETTING-STARTED.md) |
| Connect to someone else's server | [Connect to a server](GETTING-STARTED.md#listening-to-a-server) |
| Add, edit, move, or remove songs | [Managing songs](MANAGING-SONGS.md) |
| Run a server and invite listeners | [Self-hosting](SELF-HOSTING.md) |
| Stream away from home | [Remote access](REMOTE-SETUP.md) |
| Check external connections and data reporting | [Privacy](PRIVACY.md) |

Local playback and self-hosted streaming don't need an outside music provider. Flower does contact external services for some features, including artist images and update checks. Crash reporting is enabled by default in release builds and can be turned off. See [Privacy](PRIVACY.md).

## Technical reference

[Server settings](SERVER-REFERENCE.md) · [Security](SECURITY.md) · [Build from source](BUILDING.md) · [HTTP API](API.md) · [Tests](../Tests/README.md) · [Architecture](../CLAUDE.md)

<details>
<summary>Design notes and implementation history</summary>

These files contain design notes, plans and completed reviews. Use the guides above for current setup instructions.

- [Audio quality](AUDIO-QUALITY-PLAN.md)
- [AirPlay and Bluetooth](AIRPLAY-BLUETOOTH-PLAN.md)
- [Media keys](MEDIA-KEYS-PLAN.md)
- [Library sync](SYNC-PLAN.md)
- [Playlist sync](PLAYLIST-SYNC-PLAN.md)
- [Vibe Playlists and Endless Play](VIBE-PLAYLIST-PLAN.md)
- [Online metadata](ONLINE-METADATA-PLAN.md)
- [Remote-access implementation](REMOTE-ACCESS.md)
- [Remote transport options](REMOTE-TRANSPORT-PLAN.md)
- [Native security review](NATIVE-SECURITY-PLAN.md)
- [Server security review](SECURITY-REVIEW.md)
- [Store deployment](STORE-DEPLOYMENT-PLAN.md)
- [Performance tracking](PERFORMANCE-TRACKING-PLAN.md)
- [Streaming-service research](STREAMING-SERVICES-PLAN.md)
- [Archived review references](CITED-DECISIONS.md)

</details>
