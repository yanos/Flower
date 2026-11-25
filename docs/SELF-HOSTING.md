# Self-hosting Flower

Flower Server runs on a computer or NAS and streams your music to paired apps or a web browser. You can also invite other listeners. It doesn't require a hosted music service or subscription.

[Install](#running-it-in-docker) · [Pair your first device](#pair-your-first-device) · [Add music](#adding-music-to-the-servers-folder) · [Invite listeners](#adding-devices-later) · [Back up](#backups) · [Troubleshoot](#troubleshooting)

## Before you start

You'll need a music folder and a computer or NAS that stays on while people stream from it. The instructions below use Docker Compose; running from source is also supported.

Flower streams your original audio files without converting them. Large lossless files use more upload bandwidth and mobile data.

## Running it in Docker

### Linux

Install Docker and Docker Compose. Clone the repository to get the Compose files; you don't need to install .NET or FFmpeg on the host.

```bash
git clone https://github.com/yanos/Flower.git
cd Flower/docker
```

Create a `.env` file in `Flower/docker` with the full path to your music folder:

```dotenv
FLOWER_MUSIC=/srv/music
```

Start Flower and display its setup links:

```bash
docker compose up -d
docker compose logs flower
```

Run the Docker commands below from `Flower/docker`.

### macOS or Windows

Use the same `.env` file, then start Flower with the non-Linux Compose override:

```bash
docker compose -f docker-compose.yml -f docker-compose.non-linux.yml up -d
docker compose logs flower
```

Use **both `-f` options** for later `up`, `stop`, `restart`, `create`, or `run` commands.

Devices won't discover this container automatically. Add your computer's local network address to `.env`:

```dotenv
FLOWER_ADVERTISED_HOST=192.168.1.40
```

Replace the example with your address and run `up` again. Prefer a reserved LAN address or a resolvable DNS name, not a `.local` hostname. See [Docker networking](SERVER-REFERENCE.md#docker-networking) for limitations on macOS and Windows.

## Running without Docker

Install the .NET 10 SDK, clone Flower, and run this from the repository root:

```bash
dotnet workload install wasm-tools
dotnet run --project Flower.Server
```

The workload builds the browser player; [Building Flower](BUILDING.md) covers other build options. After pairing, add music folders in **Server Settings → Library**, or configure `LibraryPaths` in the [settings file](SERVER-REFERENCE.md#settings).

## Pair your first device

At first launch, Flower prints a setup code and links. **The first paired device becomes an administrator**, with permission to manage the server. The invite works once and expires after **10 minutes**.

Choose a way to pair:

| Where | What to open |
|---|---|
| **Flower app** | Open the `flower://pair` link, or paste it into **Settings → Servers → Pairing code or link** on desktop. |
| **Browser on the server** | The printed `http://localhost:4533` setup link. |
| **Browser on another computer** | The printed HTTPS setup link, for example `https://192.168.1.40:4534/#pair=…&page=settings`. |

Flower's built-in HTTPS certificate may cause a browser warning. Confirm that the address belongs to *your* server before proceeding. For a browser address with a trusted certificate, see [Remote access](REMOTE-SETUP.md). Browser pairing over plain HTTP works only on `localhost`.

The paired app or browser remembers its access. Bookmark the server's regular address. Clearing browser site data means pairing that browser again.

## Adding music to the server's folder

Copy songs into the folder configured as `FLOWER_MUSIC` (Docker), or into a folder listed in **Server Settings → Library**.

| Music location | When new songs appear |
|---|---|
| Local disk | Usually within seconds |
| Network drive | Within five minutes by default; use **Rescan files** to check sooner |

The interval is configurable with `LibraryListingMinutes`. Administrators can also drag songs into their desktop app to upload them. Their edits to tags, artwork, and the shared library sync to other devices.

Deleting a song from the server removes it for every listener. Deleting files in an administrator's library folder retained from before pairing can also remove them from the server. Read [Managing songs](MANAGING-SONGS.md) before reorganizing your collection.

The Docker music mount allows writes by default. Uploading, changing tags, moving, or deleting files requires write permission for the server user (`1654`). To make the library read-only, add `:ro` to the `/music` mount in `docker-compose.yml`. Scanning, streaming, playlists, and play counts still work. See [Docker file permissions](SERVER-REFERENCE.md#docker-file-permissions).

## Adding devices later

From an administrator device:

1. Open the server in the desktop sidebar, phone **Settings**, or **Server Settings → Devices** in the browser.
2. Leave **Administrator** unchecked for listeners. Enable it only for devices allowed to manage the server.
3. Select **Generate Pairing Code** and privately send the app or browser invite.

Invites last 10 minutes and work on one device. **Prefer a pairing link to a code**: the link checks the server's identity.

| Role | Access |
|---|---|
| **Listener** | Stream or download music and manage personal playlists |
| **Administrator** | Also edit the shared library, change server settings, invite devices, and revoke access |

The owner's administrator devices share playlists; each listener device has its own. Pair another of your own devices as an administrator if it should share the owner's playlist set and management controls.

If a device is lost or should no longer have access, remove it in **Devices**. Revoking access prevents future connections but **doesn't erase music already downloaded** to that device.

## Optional AI features

In **Server Settings → AI Features**, enable **AI Features** to download the model and analyze your music. This is off by default and can use hours of CPU time for large libraries.

- **Vibe Playlists:** build a playlist from a description of a sound or mood.
- **Endless Play:** add similar songs when the queue finishes. Enable it on both the server and any device that should use it.

Analysis and prompts run on your server, not on an AI provider's service. Paired devices don't download the model. You can pause analysis; disabling all server AI features deletes the downloaded model and stored analysis. See [Privacy](PRIVACY.md).

## Listen away from home

[Remote access setup](REMOTE-SETUP.md) compares the options and walks through each one.

| Method | Use |
|---|---|
| **Direct port forwarding** | Flower apps, with no third-party relay or VPN needed |
| **Tailscale** | Private connections when everyone can install Tailscale |
| **Caddy + port forwarding** | A browser-friendly HTTPS address under your domain |
| **Cloudflare Tunnel** | Access without opening router ports; uses Cloudflare's service and is subject to its terms |

For a Tailscale-only server, leave **Allow connections from the internet** off. Enable it for public port forwarding or a tunnel. Reverse proxies also need `TrustedProxies` configured.

## Settings

Use **Server Settings** on an administrator device for everyday options. Advanced settings are stored in `flower-server.json` in the server data folder. Environment variables and command-line options override this file.

See [Server reference](SERVER-REFERENCE.md) for locations, configuration, certificates, and file permissions.

## Backups

**Back up both your music files and the server's data folder.** The data folder includes the database, playlists, pairing information, and server identity. Losing it means pairing your devices again.

For Docker, stop the server before copying the data:

```bash
docker compose stop flower
docker compose cp flower:/data ./flower-backup
docker compose up -d
```

Use a fresh backup destination each time. Backups contain private keys and diagnostic logs, so keep them private. Without Docker, stop Flower and copy [its data folder](SERVER-REFERENCE.md#data-and-logs).

`docker compose down` preserves the data volume; **`docker compose down -v` deletes it**. See [Restoring a backup](SERVER-REFERENCE.md#restoring-a-backup) to recover or move your server.

## Updating

Back up first, then run:

```bash
docker compose pull
docker compose up -d
```

Keep the same Compose overrides used during installation. The `latest` image follows stable releases and changes when pulled. For betas or pinned versions, see [Image versions](SERVER-REFERENCE.md#image-versions).

While Flower is in early development, keep apps and server versions close together: protocols and stored data can change between releases.

## Restore administrator access

If no administrator device is available, restart Flower with `--pairing-code` to issue a new administrator invite. Existing pairings remain intact.

**From source:**

```bash
dotnet run --project Flower.Server -- --pairing-code
```

**Docker:** stop the regular container, then start a temporary one:

```bash
docker compose stop flower
docker compose run --rm --service-ports flower --pairing-code
```

Redeem the invite while that container runs, press `Ctrl+C`, and restart with `docker compose up -d`. On macOS or Windows, include both Compose files in every `stop`, `run`, and `up` command.

## Troubleshooting

| Problem | Check |
|---|---|
| **No songs** | Music-folder path, Docker `/music` mount, and read permissions |
| **Uploads or edits fail** | Server write permissions; read-only warnings in Settings or logs |
| **Server not found** | Same local network for discovery; otherwise use an invite link. Linux Docker uses host networking. |
| **Invite expired** | Generate another; a restart cancels unused invites |
| **Browser can't pair** | Use HTTPS, or `http://localhost:4533` on the server itself |
| **Works at home, not away** | [Remote access checks](REMOTE-SETUP.md#checking-the-connection) |
| **Requests receive no reply** | Network allow-list, client address, and `TrustedProxies` |
| **`429` / too many requests** | Wait a minute; inspect proxy settings if requests share a source address |
| **No administrator remains** | Use the recovery command above |

For help, choose **Report an Issue** in Flower or open a [GitHub issue](https://github.com/yanos/Flower/issues). Review logs before sharing them: they can contain filenames and network addresses. Release servers send crash reports by default; see [Privacy](PRIVACY.md) to opt out.
