# Self-hosting Flower.Server

A guide for running your own Flower server and reaching it from your phone,
laptop, or a browser — including from outside your house, without forwarding a
port.

Unlike everything else in `docs/`, this is a user-facing guide rather than a
design record. The reasoning behind these choices lives in `SYNC-PLAN.md`.

---

## What the server is

`Flower.Server` is a headless music server: it scans folders you point it at,
serves that library to Flower clients over Flower's own sync protocol, serves it
to third-party apps over OpenSubsonic, and serves a full browser UI of its own
that plays music in a tab.

```bash
dotnet run --project Flower.Server
```

It listens on `http://0.0.0.0:4533` by default and keeps everything it owns —
the database, its device key, its trusted-device list, logs, and an
operator-editable settings file — in one directory:

| OS | Data directory |
|---|---|
| macOS | `~/Library/Application Support/Flower/Server` |
| Linux | `~/.local/share/Flower/Server` |
| Windows | `%LOCALAPPDATA%\Flower\Server` |

Point it somewhere else with `--Flower:DataDirectory=/srv/flower`.

On first run it prints a pairing code and a browser link to the terminal,
because no device can administer it yet:

```
  No device can administer this server yet.
  Pair one with this code (valid until 14:32:07): 384-915
  Or open: flower://pair?host=<this-server>:4533&code=384-915&fp=...

  Settings in a browser (valid for one hour):
  http://localhost:4533/#admin=...&page=settings
```

Open that second link and you get the settings page. Enter the code in a Flower
client's **Pairing code** box and that device becomes an admin. Lost the moment?
Restart with `--pairing-code` to issue a fresh one.

### Settings

Edit `flower-server.json` in the data directory — it is seeded with a commented
description of every knob. Anything there overrides the `appsettings.json` next
to the binary and survives redeploying it. Environment variables
(`Flower__Alias=Basement`) and command-line switches (`--Flower:Alias=Basement`)
still win over both.

---

## Who is allowed to connect

Every request is checked against an allow-list before anything else looks at it.
Private, loopback and link-local addresses are allowed, plus `100.64.0.0/10` —
the range Tailscale hands out. Everything else gets a flat `403`, whether or not
it has a valid credential.

This matters for the rest of this guide: **the server is not designed to be
port-forwarded.** A directly-exposed music server gets found by scanners within
hours, and the allow-list is what stands between "on my LAN" and "on the
internet". The remote-access path below goes around that problem instead of
punching through it.

Two settings widen the list, and they answer different questions:

- **`AllowedCidrs`** — *who is allowed in*. Extra networks to admit, e.g. a VPN
  subnet of your own. Editable from the settings page, applies immediately.
- **`TrustedProxies`** — *who is allowed to tell the server who someone else
  is*. See below. Deployment-shaped, so it is read once at startup.

---

## Remote access with Tailscale

[Tailscale](https://tailscale.com) is a mesh WireGuard VPN. Your devices join a
private network — a *tailnet* — and talk to each other directly wherever they
are. No port forwarding, no public DNS name, no certificate to renew, and the
server never becomes reachable from the open internet.

It also solves TLS for free, which is why it is the recommended path rather than
just a convenient one.

### 1. Install Tailscale on the server and on every device

Follow [tailscale.com/download](https://tailscale.com/download), then on the
server:

```bash
sudo tailscale up
```

Log in on your phone and laptop with the same account. Each device gets a
`100.x.y.z` address and a name like `basement.tail1234.ts.net`.

At this point the server is already reachable at `http://basement.tail1234.ts.net:4533`
from anywhere — the allow-list already admits the tailnet range. If plain HTTP
over WireGuard is good enough for you, you are done; skip to *Tuning for a
tailnet* below.

### 2. Put HTTPS in front of it

WireGuard already encrypts the traffic, so this is not about secrecy. It is
about the browser UI: browsers reserve some capabilities for secure contexts,
and a `https://` address avoids a class of mixed-content and cookie surprises
that are tedious to debug.

```bash
sudo tailscale serve --bg 4533
```

That is the whole step. Tailscale provisions and renews a real Let's Encrypt
certificate for your MagicDNS name, terminates TLS itself, and proxies to
Flower on `4533`. The server keeps speaking plain HTTP on localhost. Your
library is now at `https://basement.tail1234.ts.net`.

`tailscale serve status` shows what is proxied; `sudo tailscale serve --bg
off` undoes it.

> Do **not** use `tailscale funnel`, which publishes the same thing to the
> entire internet. That is exactly the exposure the allow-list exists to
> prevent, and it will be refused — funnel traffic does not arrive from a
> tailnet address.

### 3. Tell the server it is behind a proxy

**Do this whenever you use `tailscale serve`.** With a proxy in front, every
request reaches Flower from `127.0.0.1`, because that is genuinely who delivered
it. The real client address is in the `X-Forwarded-For` header, and the server
ignores that header unless you have said which hop is allowed to write it.

In `flower-server.json`:

```json
{
  "Flower": {
    "TrustedProxies": ["127.0.0.1/32"]
  }
}
```

Restart the server. The log will confirm it:

```
Believing X-Forwarded-For from 1 configured proxy network(s): 127.0.0.1/32
```

Skipping this does not break anything visibly, which is what makes it worth
calling out. What breaks is per-device accounting: every device on your tailnet
arrives as the same address, so they share one rate-limit bucket. Five bad
pairing attempts from one phone then lock out every other device for a minute,
and the logs attribute all of it to `127.0.0.1`.

The empty default is deliberate. `X-Forwarded-For` is written by whoever sent
the request, so a server that believed it from anyone would let any caller
choose its own source address — past the allow-list, and out of any bucket it
had exhausted. Naming the proxy is what makes the header evidence rather than a
suggestion.

Note that the allow-list still applies to the *forwarded* address. That is the
point: putting a proxy in front of Flower does not turn the gate off, it just
moves it to the address that actually matters.

### Tuning for a tailnet

Two settings worth changing on a tailnet-only deployment:

- **`AdvertiseOnLan: false`** — stops the mDNS announcement. On a server only
  ever reached over the tailnet, the multicast announcement is noise no client
  that matters can hear.
- **`AdvertisedHost: "basement.tail1234.ts.net"`** — the address put into
  pairing invites. The default is the address your own browser reached the
  server on, which is usually right; set this if it is not.

### Pairing a device over the tailnet

**Pair on your home Wi-Fi first, before you travel.** Open the settings page,
press **Add Device…**, and enter the code in the client's **Pairing code** box.

The Flower app finds servers by mDNS, which is a local-network protocol — it does
not reach into a tailnet. So a client that has never shared a network with this
server cannot see it to pair with it. Pairing at home once is the whole setup;
after that the server tells the client every address it can be reached on,
including its tailnet one, and the client uses whichever works from wherever it
is.

The **browser UI** has no such constraint — it talks to whatever host you opened
it on, so `https://basement.tail1234.ts.net` works from anywhere without pairing
anything. See "Reaching it from a browser" below.

---

## Cloudflare Tunnel — the option for people who won't install a VPN

If you want to share your library with someone who will not install Tailscale,
[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/)
publishes a service to the internet without opening a port, the same way. Set
`TrustedProxies` to the tunnel daemon's address, and `AllowedCidrs` to whatever
range it forwards from.

Understand the trade before you do: **Cloudflare terminates TLS at their edge**,
so your traffic is decrypted on someone else's machine. That is a materially
different trust boundary from Tailscale, where nothing between your devices can
read anything. It is a reasonable choice for sharing; it is a bad default for
your own library.

---

## Reaching it from a browser

The server serves a full Flower UI of its own, which plays music in a tab and
needs nothing installed. Over `tailscale serve` it lives at
`https://basement.tail1234.ts.net`.

One caveat worth knowing before you rely on it: a tab has no authority on its own.
It needs a session token, which the settings page mints for it, and that token
lasts **one hour** with no renewal. So the browser is excellent for a quick listen
or for a device you do not own, and the app is what you want on your own phone.

## Not yet covered

- **A public domain without a VPN**, via LettuceEncrypt for automatic Let's
  Encrypt certificates. Planned; see `SYNC-PLAN.md`.
- **Docker packaging.** Planned. Note that mDNS announcement (`AdvertiseOnLan`)
  needs host networking to work from a container, since it is link-local
  multicast — a tailnet deployment turns that off anyway.

---

## Troubleshooting

**Everything returns 403.** The caller is outside the allow-list. Check the
address it actually arrives from — if there is a proxy in front, that is the
proxy's address unless `TrustedProxies` names it.

**403 only from one device.** It is not on the tailnet, or `TrustTailscaleRange`
has been turned off.

**One device's failed attempts lock out the others.** `TrustedProxies` is not
set. See step 3.

**The server does not appear in a client's sidebar.** mDNS does not cross
subnets and does not reach into a tailnet, so a client only ever *discovers* a
server it shares a network with. Pair at home once; after that the client
remembers how to reach it. If you are at home and it is still missing, check
`AdvertiseOnLan`.

**A client paired at home cannot reach the server while away.** Check that both
ends are on the tailnet (`tailscale status`), and that the server's tailnet
address has not changed since the client last synced at home.

**Two servers fight over one name in the sidebar.** Two instances are running
and advertising. Stop one, and give the survivor a distinct `Alias`.
