# Self-hosting Flower

Run a Flower server on a computer or NAS to share your music across devices
and with friends or family. It includes a browser player, so listeners don't
have to install an app.

[Install](#running-it-in-docker) · [Pair](#pair-your-first-device) ·
[Add music](#adding-music-to-the-servers-folder) · [Invite people](#adding-devices-later) ·
[Back up](#backups) · [Troubleshoot](#troubleshooting)

## Before you start

You'll need a music folder and a machine that stays on while people listen.
Docker is the simplest server install; running from source is also supported.

Flower streams the original music files without converting them. Large
lossless files use more upload bandwidth and mobile data.

## Running it in Docker

### Linux

Install Docker with Compose, then download the repository to get its Compose
files. You don't need .NET or FFmpeg on the host.

```bash
git clone https://github.com/yanos/Flower.git
cd Flower/docker
```

Create a `.env` file in this directory with the full path to your music folder:

```dotenv
FLOWER_MUSIC=/srv/music
```

Start the server and read its setup links:

```bash
docker compose up -d
docker compose logs flower
```

Run the Docker commands in this guide from `Flower/docker`.

### macOS or Windows

Use the same `.env` file, with your music folder's path, and start with the
additional Compose file:

```bash
docker compose -f docker-compose.yml -f docker-compose.non-linux.yml up -d
docker compose logs flower
```

When using `up`, `stop`, `restart`, `create` or `run`, keep both `-f` options.
Other devices won't discover this container automatically. Add your computer's
LAN address to `.env` so setup links point to it:

```dotenv
FLOWER_ADVERTISED_HOST=192.168.1.40
```

Replace the example with your computer's address, then run the `up` command
again. Use a reserved LAN address or a DNS name your devices can resolve;
avoid `.local` names. See [Docker networking](SERVER-REFERENCE.md#docker-networking)
for remote-access limitations on these hosts.

## Running without Docker

Install the .NET 10 SDK, clone the repository, and run from its root:

```bash
dotnet workload install wasm-tools
dotnet run --project Flower.Server
```

The workload is needed to build the browser UI. See
[Building Flower](BUILDING.md) for other build options.

After pairing, add a music folder in the server's **Settings → Library**.
You can also set `LibraryPaths` in
[the settings file](SERVER-REFERENCE.md#settings).

## Pair your first device

On first launch, the server prints a code and links. **This first invite grants
administrator access.** It works once and expires after ten minutes.

Choose one:

- **App:** open the printed `flower://pair` link on a device with Flower installed,
  or paste it into **Settings → Servers → Pairing code or link** on desktop.
- **Browser on the server:** open the printed `http://localhost:4533` setup link.
- **Browser on another computer:** open the printed HTTPS setup link, usually
  `https://192.168.1.40:4534/#pair=…&page=settings`.

The built-in HTTPS certificate causes a browser warning. Confirm the address
is your server before continuing. A warning can return after a restart;
[remote access setup](REMOTE-SETUP.md) covers trusted HTTPS addresses.
Plain HTTP at a LAN IP cannot pair a browser.

Once paired, the app or browser remembers its access. Bookmark the plain server
address for later visits. Clearing browser site data removes that browser's access.

## Adding music to the server's folder

Copy files into the folder you set as `FLOWER_MUSIC`, or one listed in the
server's **Settings → Library**. Songs appear automatically:

- **Local disk:** usually within seconds.
- **Network drive:** within five minutes by default. Use **Rescan files** to
  check sooner; `LibraryListingMinutes` changes the interval.

Administrators can also drag songs into their desktop app to upload them.
Their tag edits, covers and library removals sync to everyone.

**Removing a server song affects every listener.** Deleting files from a
library folder retained on an admin device from before pairing can also remove
songs from the server. See [Managing songs](MANAGING-SONGS.md) before moving or
removing a collection.

The Docker music mount is writable by default. Uploads, file edits and file
deletion require write access for the server's user (`1654`). To prevent the
server from changing files, add `:ro` to the `/music` mount in
`docker-compose.yml`. Streaming, playlists and play counts still work.
[Permissions help](SERVER-REFERENCE.md#docker-file-permissions).

## Adding devices later

On a device already paired as an administrator:

1. Select the server in the desktop sidebar, open phone **Settings**, or open
   **Server Settings → Devices** in the browser.
2. Leave **Administrator** unchecked for a listener. Check it only for a device
   that should manage the server.
3. Choose **Generate Pairing Code** and send the app invite or browser link.

Each invite is valid for ten minutes and one device. Prefer the app link to a
bare code: the link checks the server's identity before pairing.

| Access | Permissions |
|---|---|
| Listener | Play and download the library; manage their own playlists |
| Administrator | Also edit the shared library and server settings, invite devices and revoke other devices |

The owner's administrator devices share playlists. Each listener device has
its own playlists. Pair your own second device as an administrator if it should
share the owner's collection and controls.

Remove a lost or unwanted device from the server's device list. Revocation
stops future server access; it can't erase files already copied to that device.

## Optional AI features

In the server's **Settings → AI Features**, enable **AI Features** to download
the model and analyse your library. This is off by default and can take hours
of CPU time for a large collection.

- **Vibe Playlists:** make playlists from a description of the sound or mood.
- **Endless Play:** continue a finished queue with similar songs. Enable it on
  the server and on each device that wants it.

Music analysis and prompts run on your server. Paired devices don't download
the model. You can pause analysis in Settings; turning off all server AI
features deletes the model and analysis. See [Privacy](PRIVACY.md).

## Listen away from home

Choose an option in [Remote access setup](REMOTE-SETUP.md):

| Option | Best fit |
|---|---|
| Tailscale | Your own devices, or listeners willing to install Tailscale |
| Port forwarding | App access without a VPN; add a trusted certificate for browsers |
| Cloudflare Tunnel | No router port to open; check the provider's media-use terms |

Keep **Allow connections from the internet** off for a Tailscale-only setup.
Public port forwarding and tunnels require it on. A proxy also needs
`TrustedProxies` configured.

## Settings

Use **Server Settings** from an administrator device for everyday changes.
For deployment settings, edit `flower-server.json` in the server's data folder.
Environment variables and command-line options override that file.

See the [server reference](SERVER-REFERENCE.md) for file locations, settings,
certificates and Docker permissions.

## Backups

**Back up your music and server data separately.** The data folder contains the
database, playlists, device identity and paired-device list. Losing it means
pairing every device again.

For Docker, stop the server while copying its data for a consistent backup:

```bash
docker compose stop flower
docker compose cp flower:/data ./flower-backup
docker compose up -d
```

Use a fresh backup destination each time. Keep backups private: they include
server keys and diagnostic logs. For a server outside Docker, stop it and copy
[its data folder](SERVER-REFERENCE.md#data-and-logs).

`docker compose down` preserves the data volume. **`docker compose down -v`
deletes it.** See [Restoring a backup](SERVER-REFERENCE.md#restoring-a-backup)
when moving or recovering a server.

## Updating

Back up first, then run:

```bash
docker compose pull
docker compose up -d
```

Use the same Compose files you used to start the server. The `latest` image
tracks stable releases and changes only when you pull it. For betas or a pinned
version, see [Image versions](SERVER-REFERENCE.md#image-versions).

Update apps alongside the server while Flower is in early development;
protocols and stored data can change between releases.

## Getting back in, with no admin device left

Restart the server with `--pairing-code` to print a new administrator invite.
Existing devices stay paired.

From source:

```bash
dotnet run --project Flower.Server -- --pairing-code
```

With Docker, stop the regular container, then run a temporary one:

```bash
docker compose stop flower
docker compose run --rm --service-ports flower --pairing-code
```

Redeem the invite while this container is running. Then press Ctrl+C and run
`docker compose up -d`. On macOS or Windows, include both Compose files in the
`stop`, `run` and `up` commands.

## Troubleshooting

| Problem | Check |
|---|---|
| Empty library | The music path, mount and read permissions. Docker uses `/music` inside the container. |
| Uploads or edits fail | The server needs write access to the music folder. Look for a read-only warning in Settings or the logs. |
| Server missing from the app | Discovery works only on the same local network. Use an invite link; Linux Docker needs host networking for discovery. |
| Invite expired | Generate another. Restarting the server also cancels unused invites. |
| Browser won't pair | Use HTTPS, or `http://localhost:4533` on the server itself. |
| Works at home but not away | Check [remote access](REMOTE-SETUP.md#checking-the-connection), including the public-access switch and proxy settings. |
| Requests get no reply | The network allow-list may be dropping them. Check the caller's address and `TrustedProxies`. |
| Too many requests (`429`) | Wait a minute. Behind a proxy, check `TrustedProxies`; anonymous and failed requests use address-based limits. |
| No administrator left | Use the recovery command above. |

For more help, use **Report an Issue** in Flower or
[GitHub Issues](https://github.com/yanos/Flower/issues). Review logs before
posting: they can include file names and network addresses. Release servers
send crash reports by default; see [Privacy](PRIVACY.md) to turn them off.
