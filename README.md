# Flower

A music player for Windows, macOS, Linux, iOS and Android, plus a small server
for your library. Plex, but only for music: one owner runs the server, a
handful of friends or family listen, at home or away.

Pre-release (v0.x). Desktop builds are on GitHub; the phone apps aren't in the
stores yet.

## Get it

| | |
|---|---|
| **Desktop** | [Latest release](https://github.com/yanos/Flower/releases/latest): macOS (Apple silicon) `.dmg`, Windows x64 `Setup.exe`, Linux x64 `.AppImage`. Updates itself. |
| **Server** | `ghcr.io/yanos/flower-server:latest` (amd64 + arm64), or `dotnet run --project Flower.Server` |
| **Browser** | Served by the server; nothing to install |
| **iPhone / Android** | Build from source (below) |

Without a server, the app plays the folders you give it. With a server, every
device sees the same library and can keep copies for offline listening.

**Just listening?** Someone sent you a link, or you want to play your own
music: [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) is the plain-language
guide. The rest of this page is for running a server and building Flower.

## Run a server

```bash
cd docker
echo "FLOWER_MUSIC=/srv/music" > .env   # your music, mounted read-only
docker compose up -d
docker compose logs flower              # prints a pairing code and link
```

- **macOS/Windows host:** add `-f docker-compose.yml -f docker-compose.non-linux.yml`.
- **Pair:** send the `flower://pair?...` link, not the bare code. The link
  carries the server's fingerprint. A code works once, on one device, for ten
  minutes, and makes that device an **admin**.
- **More devices:** Settings → **Generate Pairing Code**, from any admin device.
- **Back up the `/data` volume.** If you lose it, every device is unpaired.
- **Update:** `docker compose pull && docker compose up -d`

Ports: **4533** plain HTTP, **4534** TLS (its certificate is pinned by paired
apps).

## Reach it from outside

| Option | What to do |
|---|---|
| **Tailscale** (easiest) | Install it on the server. Nothing else. |
| **Port forward** | Forward **4534**, never 4533. Set `AllowPublicAccess: true` **and** `AdvertisedHost: "https://<public-ip>:4534"`. Write the scheme and port, and never use a `.local` name. |
| **Behind a proxy/tunnel** | Set `TrustedProxies`, or every listener counts as one caller. |

A browser on another machine needs HTTPS (port 4534, which shows a
self-signed warning) or a real certificate. The apps need neither.
Full guide: [docs/SELF-HOSTING.md](docs/SELF-HOSTING.md).

**Locked out (no admin device left):**
`dotnet run --project Flower.Server -- --pairing-code`. For Docker, see
SELF-HOSTING.md.

## What it does

- Gapless, float32 pipeline, EQ, hi-res output; decodes anything FFmpeg can
- Output picker, AirPlay and Bluetooth routes (macOS/iOS)
- Listener accounts with per-device pairing; only admins change the library
- Admin devices push adds, tag edits, moves and removals to the server
- Vibe Playlists and Endless Play (CLAP embeddings, run by the server)
- Artist pictures; on desktop, imports an existing Music.app library

## Build from source

.NET 10. Every head runs from a clean clone with no setup.

```bash
dotnet run --project Flower.Desktop                    # Windows/Linux
dotnet run --project Flower.MacOS/Flower.MacOS.csproj  # macOS (macos workload)
dotnet run --project Flower.Server                     # server + browser UI (wasm-tools workload)
scripts/deploy-ios-device.sh                           # plugged-in iPhone
dotnet test Tests/Flower.Tests/Flower.Tests.csproj     # fast tests
```

Architecture and conventions: [CLAUDE.md](CLAUDE.md). Tests and CI:
[Tests/README.md](Tests/README.md).

## Docs

| Question | Read |
|---|---|
| Installing, pairing with a server, adding music | [GETTING-STARTED](docs/GETTING-STARTED.md) |
| Running and exposing a server | [SELF-HOSTING](docs/SELF-HOSTING.md) |
| Adding, removing, editing songs | [MANAGING-SONGS](docs/MANAGING-SONGS.md) |
| What leaves my device | [PRIVACY](docs/PRIVACY.md) |
| Server trust boundary | [SECURITY](docs/SECURITY.md) |
| HTTP API | [API](docs/API.md) |
| Anything else | `docs/*-PLAN.md`, one per initiative, each with its status |

## License

Apache 2.0 ([LICENSE](LICENSE)). FFmpeg is LGPL-2.1; third-party notices are in
[NOTICE](NOTICE).
