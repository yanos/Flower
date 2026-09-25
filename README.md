# Flower

A cross-platform music player, and a small server to keep your library on.

One person runs one server; a handful of friends or family listen. Think Plex,
but only for music, and smaller.

Full detail: **[docs/SELF-HOSTING.md](docs/SELF-HOSTING.md)**. This page is the
short version.

---

## The server, in Docker

```bash
cd docker
echo "FLOWER_MUSIC=/srv/music" > .env    # where your music is
docker compose up -d
docker compose logs flower               # prints the pairing code
```

That is the whole install — no directories to create, no ownership to fix, no
`sudo`. Every `docker compose` command runs from `docker/`, where the compose
files and that `.env` live.

**On macOS or Windows, add the non-Linux override.** Containers there run in a
Linux VM, so host networking binds to the VM and nothing reaches you:

```bash
docker compose -f docker-compose.yml -f docker-compose.non-linux.yml up -d
```

| | |
|---|---|
| Image | `ghcr.io/yanos/flower-server:latest` (amd64 + arm64) |
| Music | mounted read-only at `/music` |
| Data | named volume at `/data` — back it up, losing it unpairs every device |
| Update | `docker compose pull && docker compose up -d` |

## The server, without Docker

Needs .NET 10. Nothing else — no SQLite, no ffmpeg.

```bash
dotnet run --project Flower.Server
```

Serves `http://0.0.0.0:4533` and prints a pairing code on first run.

| OS | Data directory |
|---|---|
| macOS | `~/Library/Application Support/Flower/Server` |
| Linux | `~/.local/share/Flower/Server` |
| Windows | `%LOCALAPPDATA%\Flower\Server` |

Move it with `--Flower:DataDirectory=/srv/flower`. Point it at music with
`--Flower:LibraryPaths:0=/srv/music`.

## Pairing

First run prints a code and a link:

```
Pair one with this code (valid until 02:36:56): G6RJR
Or open: flower://pair?host=...&code=G6RJR&fp=11ada0ed...
```

- **Send the link, not the code.** The link carries the server's fingerprint, so
  the app verifies what it is trusting. A bare code trusts whatever answers.
- One code, one device, ten minutes. It grants **admin**, so whoever redeems it
  can change the library paths and add or remove devices.
- Later codes: settings page → **Generate Pairing Code**, or the same button in
  the desktop and phone apps. Needs a device that is already an admin.

### Lost every admin device

Then nothing can reach `/api/admin` to mint a code, and the way back in is a
flag on the process — codes live in memory, so the process that prints one has
to be the process that answers the redeem.

```bash
dotnet run --project Flower.Server -- --pairing-code
```

In Docker the entrypoint takes the flag as an argument, and the container
holding the port has to be the one that prints:

```bash
docker compose stop flower
docker compose run --rm flower --pairing-code   # prints the code, keeps serving
# redeem it against this container, then Ctrl-C and:
docker compose up -d
```

Under the non-Linux override, add `--service-ports` to that `run`, or it publishes
no ports and nothing can reach the container to redeem against it.

Or add `command: ["--pairing-code"]` to the `flower` service, `docker compose up
-d`, read `docker compose logs flower`, and take the line out again afterwards —
left in, it prints a live admin credential to the log on every boot.

---

## Reaching it from outside

Two ports: **4533** plain, **4534** TLS. The TLS certificate is minted from the
server's own device key, which paired clients already hold — so they validate it
with no certificate authority involved.

### Tailscale — the easy one

Install it on the server. Nothing else to configure: a tailnet address is inside
the range the allow-list already admits.

### Port forwarding

**Forward `4534`. Never `4533`.** Then two settings, and it is easy to
remember only the first:

```jsonc
// flower-server.json, or the settings page
{ "Flower": {
    "AllowPublicAccess": true,
    "AdvertisedHost": "https://203.0.113.10:4534"
} }
```

In Docker, `AdvertisedHost` is `FLOWER_ADVERTISED_HOST` in `docker/.env`.

Both are required, and they fail differently:

| Missing | Symptom |
|---|---|
| `AllowPublicAccess` | every outside listener refused, proxy working perfectly |
| `AdvertisedHost` | server only advertises LAN addresses — unreachable off Wi-Fi |

### Three traps

**Write the scheme and port.** `https://203.0.113.10:4534`, not the bare IP. A
public host over plain `http://` is refused by the client outright.

**Never a `.local` name.** It looks like the value that survives a DHCP change.
iOS will not resolve it, so the client discards the address without trying it —
and nothing appears in the server log.

**Changing the server is not enough.** A client learns addresses only from a
successful handshake, so once every address it holds is dead it can never learn
the new one. Break the deadlock by putting the device back on the LAN for a
moment, or by adding the address by hand in the app.

### Behind a proxy, tunnel, or Docker bridge

Set `TrustedProxies`, or every listener arrives as the proxy and shares one
rate-limit bucket.

> **Docker bridge on macOS/Windows hides who is calling.** Every request arrives
> as the Docker gateway, which reads as LAN — so the allow-list, the rate limit
> and the pairing throttle all go quiet. A device signature is still required on
> every route, but do not forward a router port at that setup. Use host
> networking on Linux, or the Caddy / Cloudflare override.

---

## The web UI, and the certificate warning

The server serves a full Flower UI in a browser — `http://localhost:4533` on the
machine running it, and nothing to install.

From any *other* machine that address stops working, and the reason is not the
network: a tab holds its own key, and browsers only expose the cryptography for
it over **HTTPS or `localhost`**. So `http://192.168.1.40:4533` loads a page
that says it cannot pair. `https://192.168.1.40:4534` does work — after an
interstitial, because that certificate is self-signed and a browser has no
pairing to check it against. Fine for you, not something to ask of a guest, and
it asks again after every restart.

The warning goes away with a real certificate, which needs a name rather than an
address — Let's Encrypt does not issue for IPs. It does *not* need an open port:

| You have | Do this |
|---|---|
| A tailnet | `tailscale serve` — a `ts.net` name and a certificate, no Caddy at all |
| A domain, server reachable from outside | Caddy in front, 80 and 443 forwarded: `FLOWER_HOSTNAME=music.example.com docker compose -f docker-compose.yml -f docker-compose.caddy.yml up -d`. Also turn **AllowPublicAccess** on, from the settings page |
| A domain (or a free DuckDNS name), LAN only | Point it at the LAN address and issue over **DNS-01** — proves ownership by TXT record, so nothing is forwarded and nothing is public |
| No name at all | Caddy with `tls internal`, then install its root CA on every device that will browse. No warning afterwards, at the cost of that one step per device |

Leave **AllowPublicAccess** off for the LAN-only rows: with `TrustedProxies`
set, the server sees each listener's real LAN address through the proxy, and the
allow-list already admits it.

The steps for each, with and without Docker, are in
[docs/SELF-HOSTING.md](docs/SELF-HOSTING.md) — "Port forwarding" for Caddy,
"Reaching it from a browser" for the LAN-only options. Flower's own apps need
none of this: they pin the server's key and move to `4534` on their own.

---

## Building the apps

Every head runs from a clean clone, no setup:

```bash
dotnet run --project Flower.Desktop     # Windows/Linux
dotnet run --project Flower.MacOS       # macOS (needs the macos workload)
dotnet test Tests/Flower.Tests/Flower.Tests.csproj --filter 'Category!=RequiresFfmpeg'
```

See [CLAUDE.md](CLAUDE.md) for the architecture and [docs/](docs/) for the
design notes.
