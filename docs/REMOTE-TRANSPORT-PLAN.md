# Getting to the server without installing a VPN on the phone

`REMOTE-ACCESS-PLAN.md` answered *which address to dial* once a path exists.
This one asks the question underneath it: **who provides the path at all**, when
the server sits behind a home router with no public address and the client is on
cellular. It exists because "install Tailscale on every device" — the answer
`SELF-HOSTING.md` currently gives — is the one step in the whole setup that
happens on the phone, and the phone is where setup friction actually costs
something.

Nothing here is built. It is a decision record written before the code, same as
`REMOTE-ACCESS-PLAN.md` was.

## The thing that is actually scarce

It is tempting to read this as a packaging problem: Tailscale is an app, apps
can be vendored, therefore vendor it. That reading is wrong, and seeing why is
most of the decision.

Two devices behind two different NATs cannot simply connect. Something has to
(a) introduce them to each other and (b) carry the bytes when a direct path
cannot be punched through — a CGNAT carrier, a symmetric NAT, a hotel Wi-Fi.
Tailscale's answer is a control plane for the introductions and a fleet of DERP
relays for the fallback, both of which live on **publicly reachable machines**.

That is the scarce resource. Not the client software — the client is open
source and embeddable. **A public address is the dependency, and no amount of
vendoring produces one.** Every option below is really a different answer to
"whose public box are we borrowing, and what can it see?"

## Option A — embed Tailscale in the app (`tsnet`)

`tsnet` runs a full Tailscale node in userspace: `wireguard-go` plus a gVisor
netstack, no TUN device, no root, and — the part that makes this thinkable on
mobile — **no NetworkExtension entitlement on iOS**. To the OS it is a program
opening sockets. So "be a tailnet node without installing a VPN app" is a real
capability, not a workaround.

### How it would attach to this codebase

`tsnet` is Go, and there is no .NET binding (Go, Rust and Elixir only). The
route is the one `native/miniaudio/` already established here: a small Go shim
exposing a C API, built `-buildmode=c-archive` for iOS arm64 and
`-buildmode=c-shared` for Android, vendored in-repo and P/Invoked. Go supports
both targets officially.

The shim should **not** expose a dialer. Eleven call sites build
`$"http://{device.EndPoint}"` (`PeerOpenSubsonicClientFactory.cs:15`,
`NetworkDiscoveryService.cs:400`, `LibrarySyncService.cs:130`,
`PlaylistSyncService.cs:127`, `ICoverArtUrlResolver.cs:53` and the rest), and
LibVLC takes a URL of its own in `TrackDecoder.EnsureMedia` — teaching all of
them about a tunnel is twelve chances to miss one.

Instead the shim starts `tsnet` **and a loopback reverse proxy**: listen on
`127.0.0.1:PORT`, forward everything over the tunnel to the paired server. The
tunnel then surfaces as an ordinary candidate address, `PairedServerReachability`
ranks it with everything else, and LibVLC never learns anything changed. One new
concept, zero call-site edits — which is the strongest argument this option has.

### Why it still does not solve the problem

A `tsnet` node must authenticate against a control plane, and fall back to a
relay when hole punching fails. Two ways to supply those, both bad here:

- **Tailscale's own.** Free tier, 100 devices, and their global DERP fleet. But
  it needs an account and an auth key pasted into the server *and* the phone.
  That trades an app install for a credential chore. Setup got worse, not better.
- **Self-hosted Headscale.** Removes the account, and it can run an embedded
  DERP — but only somewhere publicly reachable, which is the thing we do not
  have. The argument closes a loop on itself.

Cost is real too: a Go runtime plus `tsnet` is roughly 15–30 MB per architecture
(estimate, not measured) shipped inside the mobile app, alongside a second GC in
the process.

**Verdict: rejected.** Not because it cannot be built — it can — but because
after building it the user still needs an account or a VPS, and the phone-side
simplicity that motivated the whole exercise is gone.

## Option B — Cloudflare Tunnel, vendored server-side (recommended)

`cloudflared` makes an **outbound** connection from the server to Cloudflare's
edge and holds it open. Cloudflare publishes a hostname; requests arriving there
travel back down that already-open connection. No inbound port, no port
forwarding, no public IP, no router configuration.

The decisive property: **it is server-side only.** The phone gets a normal
HTTPS URL and needs nothing — no app, no VPN, no tunnel, no client code path.
Against the stated goal, simplest possible installation and use, that is a win
no client-side option can match, and the client work is zero.

Same vendoring technique as Option A, minus the mobile constraints: Go, Apache-2,
built for the server's platforms only, where 15–30 MB does not matter. It can
also simply be run as its own daemon; embedding is a packaging nicety, not a
requirement, and should not be done first.

### What it costs

**Cloudflare terminates TLS at their edge.** Requests are decrypted on someone
else's machine before being re-encrypted down the tunnel. With Tailscale nothing
between the two devices can read anything; here, one party in the middle can.
For a personal music library that is a defensible trade — it is worth making
deliberately rather than discovering later. `SELF-HOSTING.md` already frames
this correctly ("reasonable for sharing, a bad default for your own library");
adopting it as the primary path means rewriting that line honestly, not deleting
it.

It also needs a Cloudflare account and a domain on it. That is one setup, on a
desktop browser, by the person running the server — not per listener, not on a
phone.

### The work this actually implies

1. **HTTPS on the native client.** Every peer URL is hardcoded `http://`, so a
   public HTTPS endpoint is unreachable from the app today. This is the deferred
   scheme rework `REMOTE-ACCESS-PLAN.md` names: eleven call sites, plus widening
   `DiscoveredDevice.EndPoint` past `IPEndPoint` so it can hold a name — already
   partly done for remembered candidates. Nothing conceptually hard; it is the
   bulk of the effort and it is client-side after all, which weakens the "zero
   client work" claim to "no new client *concepts*."
2. **`LanGuard` faces the internet** — done, and it needed a setting of its own.
   Turning the guard off is one decision with five consequences (it is the
   containing control named in five separate places, see
   `OPEN-INTERNET-REVIEW.md`), so it is now `FlowerServerOptions
   .AllowPublicAccess` rather than an `AllowedCidrs` of `0.0.0.0/0` that a
   reader might not recognise for what it is. Deliberately not editable from the
   browser settings page, and it warns at every startup. The original note
   follows, and it is what the setting is for. Today it admits RFC1918, loopback and
   `100.64/10`, and everything else gets a flat 403 — so authentication has a
   network-level backstop underneath it. Behind a tunnel every request arrives
   from the tunnel daemon, inside the allow-list by definition, and that backstop
   is gone. `TrustedProxies` must name the daemon so `X-Forwarded-For` is
   believed and rate limiting stays per-client rather than pooling every listener
   into one bucket (`FlowerServerOptions.TrustedProxies`, already documented for
   exactly this in `SELF-HOSTING.md` step 3). Signature auth
   (`PeerSignatureAuth`, `NonceReplayGuard`, `RateLimiter`) becomes the only
   thing standing in front of the server, so it should be reviewed on that
   assumption before this is switched on, not after.
3. **Cloudflare Access, optional.** Puts an identity check in front of the
   hostname so unauthenticated requests never reach the server. Worth knowing
   about; it does not remove point 2, since a listener has to get through it.
4. **Transcoding is unchanged and still absent.** A FLAC is ~1000 kbps whatever
   carries it. Cloudflare's free tier is not a CDN cache for this traffic.

5. **Their terms do not merely discourage this; they reserve the right to stop
   it.** Checked against the primary source while writing the setup guide, and
   it is sharper than the line above assumed. Cloudflare retired the old
   HTML/non-HTML section 2.8, but moved the restriction into the CDN-specific
   service terms, which reserve the right to "disable or limit your access to or
   use of the CDN … if you use or are suspected of using the CDN without such
   Paid Services to serve video or a disproportionate percentage of pictures,
   audio files, or other large files." A music library is a disproportionate
   percentage of audio files by construction, and Cloudflare has said the same
   about self-hosted media servers specifically.

   This does not make the option unbuildable — it is built, and it works — but
   it does downgrade what it can be. **It is a sharing path that may be limited
   at Cloudflare's discretion, not a foundation to build listening on.** The
   recommendation above stands only in that narrower sense, and the honest
   consequence is that the *owner's* own listening should go over Tailscale or a
   mapped port, with the tunnel as the thing offered to a friend who will not
   install a VPN. `SELF-HOSTING.md` leads with this rather than burying it.

## Option C — keep Tailscale, document only

The status quo: `SELF-HOSTING.md` tells the reader to install it on the server
and every device. Best privacy of the three (nothing in the middle can read
anything), zero code, and it is the path `REMOTE-ACCESS-PLAN.md`'s ranking was
designed against — the `100.64/10` rank exists for it.

Its only real cost is the phone-side install, which is a five-minute one-off, not
an ongoing tax.

**This stays the recommended path for the server owner's own devices.** Option B
is what to reach for when someone else has to listen, or when installing a VPN on
a device is not possible at all.

## How Plex avoids asking for any of this

Worth understanding, because Plex asks the user for no domain, no certificate
and no tunnel account, and the reason is instructive rather than magical. It
does four things:

1. **plex.tv is the control plane.** The server signs in and publishes the
   addresses it thinks it has; clients ask plex.tv where their server is. The
   account did not disappear — it is Plex's, and it is bundled into the product
   so it reads as "signing into Plex" rather than as infrastructure setup.
2. **It opens the router port automatically**, via UPnP or NAT-PMP. This is the
   step people believe Plex skips. It does not skip it; it just does it without
   asking, so the user never learns it happened.
3. **`plex.direct` solves TLS without the user owning a domain.** Plex owns that
   domain and a wildcard certificate for it, and hands each server a subdomain
   encoding its own IP — roughly `192-168-1-40.<hash>.plex.direct`, which
   resolves straight back to that address, private ones included. Real HTTPS,
   valid cert, no ACME, nothing for the user to renew.
4. **Plex Relay when port forwarding fails** — CGNAT, UPnP disabled, symmetric
   NAT. Traffic goes through Plex's own machines, bandwidth-capped per stream
   (low enough to matter for video; details vary by tier and have changed over
   time). Their DERP equivalent, and the reason Plex still works on connections
   where step 2 cannot.

The thing to take from it: **the domain requirement never went away — Plex paid
it once, centrally, for everyone.** Same for the relay. That is a hosted service
with a permanent bill attached, which is exactly what this project does not want
to become. So steps 1, 3 and 4 are not available to us without becoming Plex.

**Step 2 is.**

## Option D — open the router port ourselves (UPnP / NAT-PMP)

The one genuinely borrowable piece. `Flower.Server` asks the router to map its
port, discovers its own public IP, and reports that address in the `/info`
handshake — where `REMOTE-ACCESS-PLAN.md`'s machinery already picks it up. The
client remembers it, ranks it below LAN and tailnet, probes it when nothing
better answers. **No new client concepts, no accounts, no third party, no
ongoing cost**, and it slots into `LocalAddresses.Reachable` as one more source.

It has to be weighed against three things, and the first is our own position:

- **`SYNC-PLAN.md:190` argues against exactly this** — a port-forwarded media
  server gets found by scanners within hours. That argument is correct and does
  not stop being correct because Plex does it anyway. Plex survives it on the
  strength of auth and TLS, not because exposure is fine. Adopting this means
  consciously revising that stance, with the `PeerSignatureAuth` /
  `RateLimiter` review from Option B as a hard prerequisite rather than a
  follow-up.
- **Plain HTTP over the open internet is not acceptable**, which drags in the
  certificate problem Tailscale and Cloudflare each solved for free. Without a
  `plex.direct` of our own that means LettuceEncrypt plus a domain the user
  owns — and the domain requirement is back, so this stops being the zero-setup
  option it first appears to be.
- **CGNAT defeats it entirely.** No public IP to map, no port to open, no
  fallback without a relay we do not have. A growing share of home connections,
  and the user cannot tell from the outside whether theirs is one.

### It really does need no credentials, and that is the problem

The natural objection is that opening a router port requires logging into the
router. It does not, and the reason is the protocol's design rather than a
loophole.

**UPnP IGD** works over SSDP: multicast a discovery request to
`239.255.255.250:1900` asking for an `InternetGatewayDevice`, fetch the
description XML the router answers with, find its `WANIPConnection` service, and
make two SOAP calls — `AddPortMapping` (external port, internal IP, protocol,
lease) and `GetExternalIPAddress`, which hands back the public IP we need for the
candidate list anyway. **NAT-PMP** and its successor **PCP** do the same thing in
a few UDP packets to the default gateway on port 5351.

At no point is anything authenticated. The router's entire security model is
"you are on the LAN, so you are trusted" — which is exactly the criticism the
protocol has attracted for twenty years, and why a good number of routers now
ship with it disabled and some ISPs disable it remotely. So the mechanism is
real, it genuinely needs no credentials, and *whether it is available at all* is
outside our control and unknowable until we try.

`Mono.Nat` (MIT, on NuGet, maintained as part of MonoTorrent) speaks UPnP IGD,
NAT-PMP and PCP behind one API and is the obvious dependency rather than hand-
rolling SOAP.

Two consequences for how this is presented. Mappings carry a **lease** and are
lost on router reboot, so it has to be renewed on a timer, not done once at
startup. And because it can fail for at least four unrelated reasons — disabled,
unsupported, double NAT, CGNAT — the server must **report what actually
happened** rather than silently trying. Plex's "Fully accessible outside your
network" indicator exists for precisely this, and is the right thing to copy.

**Verdict: worth building the port-mapping half, not worth relying on.** As an
*additional* candidate address it is cheap, self-contained, and strictly better
than nothing when it happens to work. As the primary remote path it needs a
certificate story it does not have and fails silently behind CGNAT.

## The certificate, once TLS actually lands

Decided ahead of the code, because it is the question that determines whether
remote access needs a domain at all. It does not.

**A self-signed certificate is the floor.** The server generates one on first
run and the app pins it to the fingerprint it already paired with. A browser
warns about a self-signed certificate because a browser meets arbitrary servers
and cannot know which one is legitimate; a paired client is in the opposite
position, having already established exactly which server it means. So there is
no warning to show, because there is no ambiguity to resolve — the same
reasoning Syncthing has run on for a decade.

The consequence worth stating: **a LAN-only listener configures nothing and
still gets encryption.** No domain, no certificate authority, no third party,
and it works on a bare IP.

**A real certificate is the optional upgrade**, for the two things that cannot
pin: the browser UI (`Flower.Web`) and third-party OpenSubsonic clients. Three
ways to get one, and only the last needs a purchase:

- **DNS-01 against a free subdomain.** A DuckDNS-style name plus Let's
  Encrypt's DNS-01 challenge, which proves ownership through a TXT record and
  so never has to reach the server — meaning the A record may point at
  `192.168.1.40` and the certificate still issues. Publicly trusted, no open
  ports, entirely LAN.
- **`tailscale cert`**, for anyone already running Tailscale. Tailscale owns
  `ts.net`, so the user owns no domain and it renews itself.
- **A domain the user owns**, via LettuceEncrypt.

All three land in the same place — a certificate file the server loads — so this
is one mechanism with a configuration switch, not three code paths. A local CA
installed on each device was considered and rejected: it is the most privilege
of any option and the most tedious to install on iOS, and the two above make it
unnecessary.

## Decision

**All of them, because they are not alternatives.** This is the point worth
holding onto: `REMOTE-ACCESS-PLAN.md` already built a client that keeps a *list*
of candidate addresses, probes them concurrently and takes the best-ranked one
that answers. Every option here is just another entry in that list. Supporting
several is therefore close to free on the client, and what it buys is graceful
degradation — direct LAN at home, tailnet on the owner's own phone, a mapped
public port when the router cooperates, the tunnel when nothing else works, all
without the listener knowing which one is carrying the audio.

The ranking they slot into extends naturally: mDNS sighting, then LAN, then
tailnet, then a direct public address, then the tunnel — cheapest and most
private first, most dependent on a third party last.

Roles, none replacing another:

- **Tailscale for your own devices.** Documented, already working, best trust
  boundary. Unchanged.
- **Cloudflare Tunnel as the no-install path**, promoted from a footnote to a
  supported option — after the scheme rework and the `LanGuard` review, which are
  its real prerequisites and are worth doing regardless.
- **UPnP/NAT-PMP port mapping as an opportunistic extra candidate**, once its
  prerequisites are met. Never the path anything depends on.
- **`tsnet` embedding: not pursued.** Recorded here so the idea stops looking
  free. It is buildable; it just does not deliver what it appears to.

Sequence, if this is picked up. The first two are shared prerequisites — every
path above needs them, so neither is wasted whichever paths are eventually
enabled:

1. **The scheme rework — done.** `DiscoveredDevice` now carries a `BaseUri`
   (scheme, host, port) with the resolved address kept beside it as `Ip` for
   ranking only, `/info` reports full origins rather than bare `host:port`, and
   the eleven hardcoded `http://` call sites go through `DiscoveredDevice.Url`.
   Nothing serves TLS yet, so behaviour is unchanged — but every HTTPS front end
   is now reachable in principle, `tailscale serve` included, which was
   documented and impossible before.
2. **The `LanGuard` / rate-limit / signature review**, on the assumption that
   requests arrive from the open internet. Non-negotiable before either the
   tunnel or a mapped port is switched on, and the two differ in a way that
   matters: tunnel traffic arrives via a proxy and needs `TrustedProxies` set so
   rate limiting stays per-client, while a mapped port arrives directly and does
   not.
3. **Cloudflare Tunnel**, documented end to end in `SELF-HOSTING.md`.
4. **UPnP/NAT-PMP mapping**, with the lease renewal and the reachability
   indicator.
5. Vendoring `cloudflared` into the server build — last, optional, a packaging
   nicety only.

Each remote path stays **off by default and independently switchable**. A server
that wants none of this keeps the LAN-and-tailnet behaviour it has today, and
nothing above changes what happens to someone who never turns it on.

## Status

**Steps 1, 2 and 3 are built; steps 4 and 5 are still a decision on paper.** The scheme rework landed (see above) and both suites pass —
`Flower.Tests` 1095/1095, `Flower.Server.Tests` 178/178. Nothing serves TLS yet, no transport
was added, and no behaviour changed: what changed is that a scheme other than
`http` is now expressible end to end, which nothing else here could proceed
without.

The `tsnet` rejection is a decision. The Cloudflare adoption and the certificate
design above are directions.

**Step 2 — the auth review — has now been done, and is written up in
`OPEN-INTERNET-REVIEW.md`.** It found no live vulnerability, because nothing is
exposed yet; what it found is that `LanGuard` is cited as the containing control
in five separate places that were each reasoned about on their own, and that a
tunnel delivering over loopback retires all five at once without anything
logging that it happened. The two fixes it called for before any transport are
**built**: `/info` now answers its address list only to a peer whose signature
verifies (the client signs its poll to be one), and the signed canonical form
percent-encodes its parameters so a value can no longer imitate a separator.

Its two unsequenced findings are built as well: every per-source rate-limit key
in both servers now goes through one helper that collapses IPv6 to its /64, so
the budgets bound a caller rather than an address; and `/api/admin`, which had
no budget at all, has one.

**And so is the one this step gates on.** An undeclared proxy now warns about
itself — it cannot be caught at startup, because `cloudflared` dials out and
delivers over loopback, so the server watches for the one signal that exists (an
`X-Forwarded-For` from a hop `TrustedProxies` does not name) and says what it
costs. The failed-auth budget no longer locks out the whole `/rest` surface
either: it gates password attempts only, so a paired device sharing an address
with a guesser keeps playing.

**And #7 is closed, which was the last thing between a test and a deployment.**
The 60-minute full-admin bearer token the browser ran on is deleted, along with
`AdminSessionService`, `AdminSessionCredentials`, `PeerOrSessionAuth` and the
`X-Flower-Admin-Session` header — there is no bearer credential left in the
project. The browser holds a non-extractable P-256 keypair through WebCrypto,
redeems a single-use pairing code like any other device, and signs every
request; `SignatureVerifier` accepts it unchanged, because `raw` key export and
raw `r‖s` signatures are byte-for-byte what the desktop already produces. The
cost was making `IPeerCredentials` asynchronous, which is exactly the
re-shaping that was predicted, and one requirement: `crypto.subtle` exists only
in a secure context, so the browser UI now needs HTTPS or `localhost`. Every
transport considered here terminates TLS anyway, and step 4 already gates on it.

**Cloudflare Tunnel is ready to be turned on**, not merely tested.

What remains there is sequenced against the steps above, including the one that
hardens the ordering here — a mapped public port cannot ship before TLS, because
classic Subsonic auth puts a permanent credential in a query string, so step 4
is downstream of step 3 rather than parallel to it.

**Step 3 — Cloudflare Tunnel — is built and documented end to end** in
`SELF-HOSTING.md`: prerequisites, `cloudflared` setup, the `config.yml`, the
server settings that go with it, how to verify, and how to hand a listener
access. It needed one piece of code rather than none —
`FlowerServerOptions.AllowPublicAccess`, see point 2 above — because
`TrustedProxies` doing its job correctly is precisely what makes `LanGuard`
refuse every remote listener, and the two only make sense set together.

That work also turned up the terms question in point 5, which is the more
important outcome of the step. The tunnel works; what changed is what it should
be used *for*. Nothing here has been run against a real Cloudflare account yet —
that needs a domain, and is the one part of this that cannot be tested locally.
