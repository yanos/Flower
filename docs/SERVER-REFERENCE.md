# Server reference

Configuration settings, data paths, Docker permissions, and HTTPS details.

For installation, see [Self-hosting](SELF-HOSTING.md). For remote connections, see [Listening away from home](REMOTE-SETUP.md).

[Settings](#settings) · [Data and logs](#data-and-logs) ·
[Docker permissions](#docker-file-permissions) · [HTTPS](#https-and-certificates) ·
[Restore a backup](#restoring-a-backup)

## Settings

Change everyday options in **Server Settings** on an administrator device. Advanced deployment settings are in `flower-server.json` in the server's data folder. Flower creates the file with descriptions of the available settings.

Example for a server running outside Docker:

```json
{
  "Flower": {
    "Alias": "Home music",
    "LibraryPaths": ["/srv/music"],
    "SendCrashReports": false
  }
}
```

Edit the existing file and leave unrelated settings in place. Keys starting
with `_` are descriptions, not active settings; use the name without `_`.

Settings are applied in this order, from highest to lowest priority:

1. Command-line options, such as `--Flower:Alias=Basement`.
2. Environment variables, such as `Flower__Alias=Basement`.
3. `flower-server.json` in the data directory.
4. The shipped `appsettings.json` and built-in defaults.

`DataDirectory` must be set by command line, environment or `appsettings.json`:
it locates `flower-server.json` and can't be set inside it.
Environment values pinned by Docker can prevent changes in the settings page.
The image pins `DataDirectory=/data` and the first library folder to `/music`.

### Common options

Names below belong inside the `Flower` object.

| Setting | Default | Purpose |
|---|---|---|
| `LibraryPaths` | No explicit folders | Music folders to scan. Music.app integration can add its folder. |
| `Alias` | Machine name | Name shown in Flower apps |
| `LibraryListingMinutes` | `5` | Check for changes on network drives; `0` disables periodic checks |
| `AdvertiseOnLan` | `true` | Let nearby apps discover the server |
| `AdvertisedHost` | Empty | Full address clients should use, such as `https://music.example.com` |
| `HttpsPort` | `4534` | Built-in HTTPS listener; `0` disables it |
| `CertificatePath`, `CertificateKeyPath` | Empty | PEM certificate and key to use instead of the built-in certificate |
| `AllowPublicAccess` | `false` | Allow callers outside the private-network allow-list |
| `TrustTailscaleRange` | `true` | Allow addresses in `100.64.0.0/10` |
| `AllowedCidrs` | Empty | Additional allowed networks, such as `10.8.0.0/24` |
| `TrustedProxies` | Empty | Proxies allowed to supply the caller's IP address |
| `SendCrashReports` | `true` | Send crash and sampled performance reports; [details](PRIVACY.md) |
| `AiFeaturesEnabled` | `false` | Enable server AI analysis and features |
| `VibeEnabled` | `true` | Offer Vibe Playlists when AI features are enabled |
| `EndlessPlayEnabled` | `false` | Offer Endless Play when AI features are enabled |
| `VibeClipsPerSong` | `6` | Analyse 3, 6 or 12 clips per song, or `0` for the whole song |
| `SemanticAudioPaused` | `false` | Keep AI analysis paused across restarts |
| `IntegrateWithITunes` | `true` | Import a local Music.app library when found |
| `SyncPlayCountFromITunes` | `true` | Import Music.app play counts and last-played dates |
| `SyncDateAddedFromITunes` | `true` | Import Music.app Date Added values |
| `WebUiPath` | Empty | Override the browser UI bundle location |

Restart after changing `TrustedProxies`, ports, certificates or
`SendCrashReports`. Network access switches apply to new requests without a
restart. Prefer the settings page for changes it supports.

`AdvertisedHost` needs a scheme and, for a nonstandard port, that port too.
Avoid `.local` names: the app's remembered-address lookup doesn't resolve them
reliably on iOS. With public access on and this setting empty, Flower advertises
its public IP and HTTPS port automatically.

## Data and logs

| Installation | Data directory |
|---|---|
| Docker Compose | Named volume `flower_flower-data`, mounted at `/data` |
| macOS | `~/Library/Application Support/Flower/Server` |
| Linux | `~/.local/share/Flower/Server` |
| Windows | `%LOCALAPPDATA%\Flower\Server` |

For a server outside Docker, override the path with
`--Flower:DataDirectory=/srv/flower`. The app and server use separate data folders.

The data folder contains the library database, server identity key, trusted-device list, settings,
upload staging, and caches. Logs are under `logs/`. Client logs are stored in
`logs/devices/<alias>--<fingerprint>/`, retained for seven days and capped at
128 MB per device. Treat this folder and its backups as private.

To edit Docker's settings file:

```bash
docker compose cp flower:/data/flower-server.json .
# Edit the local flower-server.json, then copy it back:
docker compose cp ./flower-server.json flower:/data/
docker compose restart flower
```

Compose examples assume the repository's `docker/` directory. Keep any `-f`
overrides used by your deployment when restarting or creating containers.

## Docker file permissions

By default, the Docker image runs as user **1654**. Docker creates the named data volume with the
correct owner. Your music folder must be readable by that user, and writable
for uploads, tag edits, moves and file deletion.

A read-only music folder is supported. Flower shows a warning, but scanning,
streaming, pairing, playlists and play counts still work. Add `:ro` to the
music mount to make that choice explicit:

```yaml
volumes:
  - flower-data:/data
  - ${FLOWER_MUSIC}:/music:ro
```

If the host's permissions require a different user, set `user:` on the service:

```yaml
user: "1000:1000"
```

Replace those numbers with the UID and GID that should access your files.
Then stop Flower and change the data volume's owner once:

```bash
docker compose stop flower
docker compose run --rm --user root --entrypoint chown flower -R 1000:1000 /data
docker compose up -d
```

A bind mount for `/data` needs its ownership prepared manually. Keep the named
volume unless you have a reason to manage this yourself.

On Linux, ordinary file deletion may move music into `/music/.Trash-1654/`
when a Trash is available (the number follows the server UID). Scans ignore
it. **Removed Songs → Delete Files…** permanently deletes files after confirmation.

## Docker networking

| Host | Setup | Discovery |
|---|---|---|
| Linux | Default Compose file uses host networking | Nearby apps can discover the server |
| macOS / Windows | Add `docker-compose.non-linux.yml` for published ports | Use an invite or address; LAN discovery isn't available through the bridge |

On macOS and Windows, the Compose override publishes ports 4533 and 4534. Remap the host ports with
`FLOWER_HTTP_PORT` and `FLOWER_HTTPS_PORT`, and update
`FLOWER_ADVERTISED_HOST` to the reachable scheme, host and port.

The Caddy and Cloudflare overrides assume Linux host networking. They are not
complete proxy deployments for a macOS or Windows Docker VM. A VM can hide the
original client address behind its gateway, affecting network checks and
anonymous rate limits. Use Linux or run the server and proxy directly for
public access.

## HTTPS and certificates

| Port | Use |
|---|---|
| `4533` | HTTP for local access and a local reverse proxy; keep private |
| `4534` | HTTPS with Flower's built-in certificate by default |

Installed Flower apps verify the built-in certificate against the server's identity saved during pairing. They prefer HTTPS; HTTP is allowed only on networks
covered by the app's cleartext rules, including local and Tailscale addresses.
A pairing link includes the identity needed to check the first connection.

Browsers require HTTPS or `localhost` to pair. The built-in certificate
causes a warning and is regenerated at restart. Use
[Tailscale Serve or Caddy](REMOTE-SETUP.md) for a trusted browser address.

### Using your own certificate

Set both PEM paths and restart:

```json
{
  "Flower": {
    "CertificatePath": "/etc/flower/fullchain.pem",
    "CertificateKeyPath": "/etc/flower/privkey.pem"
  }
}
```

The server must be able to read both files; Docker needs them mounted inside
the container. A missing path stops startup. Flower loads certificates at
startup and doesn't obtain or renew them: arrange renewal and restart it
when the files change.

A proxy serving an authority-issued certificate should be reached by its DNS
name. At a bare IP address, Flower apps require the server's own pinned
certificate. A proxy forwarding TCP without terminating TLS can still use an IP.

For local browser HTTPS without an open router port, use a name you control
and DNS-based certificate validation, or a local certificate authority trusted
by each browser. See [Caddy's local HTTPS documentation](https://caddyserver.com/docs/automatic-https#local-https).

### Using HTTPS on port 443

For direct app access on networks that block 4534, set `HttpsPort` to `443`,
restart Flower and forward router port 443 to server port 443. Set
`AdvertisedHost` if the external and internal ports differ.

A non-root Linux process needs permission to bind a low port. For the
host-networked Docker deployment, this is a host setting, not a container
`cap_add` option. One host-wide option is:

```bash
echo 'net.ipv4.ip_unprivileged_port_start=443' | sudo tee /etc/sysctl.d/99-flower-443.conf
sudo sysctl --system
```

This permits unprivileged processes on that host to bind ports from 443 upward.
Check that another service isn't already using 443. Keep the old router port
forwarded to the new listener until remote apps learn the changed address.
Port changes don't bypass a network that intercepts TLS certificates.

## Image versions

| Image tag | Follows |
|---|---|
| `latest` | Newest stable release |
| `beta` | Newest stable release or beta, whichever is newer |
| Full version, such as `1.2.3` | That version only |

Set `FLOWER_IMAGE=ghcr.io/yanos/flower-server:beta` in `.env`, or use a full
version to pin it. Then pull and recreate with your deployment's Compose files.
Alpha and release-candidate tags don't update `latest` or `beta`.
Images are published for Linux amd64 and arm64.

Keep a backup before trying a beta. Turning betas off doesn't make an older
release able to read a newer database; wait for a newer stable release or
restore the earlier backup.

To remove replaced, untagged Flower server images:

```bash
docker image prune -f --filter label=flower.image=server
```

This leaves images with version tags alone. Older images without this label
need manual removal. Source-image build options are in [Building Flower](BUILDING.md).

## Restoring a backup

Restore the complete data folder onto a fresh data volume while Flower is
stopped. Keep your music backup separate and restore it to the music mount.
Use the same server version as the backup before attempting an upgrade.

For a new Docker deployment, with `.env` and the Compose files already prepared:

```bash
docker compose create flower
docker compose cp ./flower-backup/. flower:/data/
docker compose run --rm --user root --entrypoint chown flower -R 1654:1654 /data
docker compose up -d
```

If you configured a custom `user:`, use its UID and GID instead of `1654:1654`.
Use this procedure with an empty destination volume; preserve any existing data
before replacing it. Start only one server with the restored identity.

For a server outside Docker, stop it, restore the data folder with permissions
for the server's user, then restart. Check the music paths if moving machines.
A complete backup preserves device pairings; clearing browser data still
requires pairing that browser again.
