# The review before anything faces the open internet

Step 2 of `REMOTE-TRANSPORT-PLAN.md`'s sequence: re-examining `LanGuard`, the
rate limiters and the signature scheme on the assumption that requests arrive
from the open internet rather than from the LAN. Non-negotiable before either
Cloudflare Tunnel or a mapped port is switched on, and a shared prerequisite
for both — so none of it is wasted whichever transport is eventually enabled.

Nothing here is a live vulnerability today. Every path into both servers is
still fronted by `LanGuard`, and no transport has been added. What this
records is which of those defences stop working the moment one is, and which
were never load-bearing to begin with.

## The finding that reframes the rest: LanGuard is doing more work than it looks

`LanGuard.IsPrivateOrLoopback` is one `if` in one middleware, but it is cited
by name as the containing control in five separate places:

- `DiscoveryEndpoints` — `/info` is deliberately ungated ("LanGuard still
  fronts it, so this is reachable from the local network and not the open
  internet").
- `AdminSessionService` — a 60-minute bearer token handed over in a URL
  fragment ("LanGuard keeps it unusable from off the LAN even in the window
  where it is live").
- `PeerOrSessionAuth` — widening that bearer token past `/api/admin` to the
  catalog and to ticket minting ("LanGuard still keeps it unusable from off
  the LAN").
- `SyncHttpServer`'s class comment — "the only thing standing between
  LAN-only and reachable from the internet if the port is ever forwarded."
- `Program.cs`'s Kestrel body cap — the LanGuard middleware described as "the
  only thing between an unauthenticated caller and a 30 MB buffered upload."

Each of those trades is individually defensible while the premise holds. The
problem is that they were reasoned about separately and they all fail
together, at the same instant, on the first deployment where the premise stops
being true.

**And the premise stops being true silently.** Cloudflare Tunnel works by
`cloudflared` dialling out and delivering requests onward over loopback.
`IPAddress.IsLoopback` returns true, the middleware admits the request, and
`LanGuard` degrades from a perimeter to a no-op without logging anything, without
a config change, and without any of the five comments above becoming visibly
wrong. The same is true of any reverse proxy on the same host.

**What to do about it:** `LanGuard` has to stop being an implicit perimeter and
become an explicit one. Concretely, the server needs to know whether it is
exposed — a `FlowerServerOptions` switch that a transport sets — and the five
sites above need to state what they do when it is on, rather than inheriting an
answer. `/info` is the one with a clean fix (below); the admin-session widening
is the one that most needs an actual decision rather than a comment.

## Findings

### 1. `/info` hands its address list to anyone who asks

`DiscoveryEndpoints` answers `SyncProtocol.InfoPath` with no authentication at
all, and `REMOTE-ACCESS-PLAN.md` just added `LocalAddresses.Reachable(...)` to
that response. On a LAN that is a peer learning where to dial. Exposed, it is
an unauthenticated caller learning the server's internal subnet layout, its ULA
addresses and — the one that matters — its tailnet address, which is otherwise
the thing a listener is *supposed* to have had to be invited to.

The alias, fingerprint, public key and library change token leak alongside it.
Those are less interesting (a fingerprint is a public key's hash, and pairing
needs it), but the address list is a real disclosure.

**Fixed.** The first instinct was to gate on the `IsTrusted(callerFingerprint)`
the handler already computes for `TrustsCaller`, but that check takes an
unsigned header at its word, and a fingerprint is public — it is in this very
response and in every pairing invite — so it would only have asked an attacker
to repeat something it had read. So the caller now *proves* it instead:
`NetworkDiscoveryService.ResolveAliasAsync` signs its poll through the same
`IPeerCredentials` seam every other outbound peer call uses, and both `/info`
implementations verify it with `AuthenticateTrustedPeer` before including
`Addresses`. `TrustsCaller` moved to the verified fingerprint too, and the
unsigned lookup is gone rather than kept as a fallback.

`/info` itself stays open — a peer still has to be able to learn our
fingerprint and public key before either side can evaluate trust. What a
signature buys is a fuller answer, not access.

### 2. Every per-IP budget collapses to one bucket behind a tunnel

Five limiters key on `Connection.RemoteIpAddress`: `RedeemRateLimiter` (5/60s),
`SyncEndpoints.BulkLimiter` (20/60s), `SubsonicEndpoints.FailedAuthLimiter`
(10/60s) and `RequestLimiter` (600/60s), plus `SyncHttpServer`'s info and pair
categories. Behind a tunnel or proxy with `TrustedProxies` unset, every client
arrives as one address and shares one bucket.

`FlowerServerOptions.TrustedProxies` exists precisely for this and its comment
says so. The gap is that **nothing enforces it** — an operator who enables a
tunnel and forgets it gets a server that looks like it is working.

The sharpest consequence is not fairness, it is availability, and it is worth
stating separately because the plan's framing ("so rate limiting stays
per-client") undersells it:

**`FailedAuthLimiter` is a lockout, not a throttle.** `SubsonicEndpoints`'
filter peeks it *before* authenticating, so a source over budget is refused the
entire `/rest` surface — including requests that would have authenticated
perfectly. Ten bad-password attempts per minute from any one caller therefore
take `/rest` away from every other caller sharing that key. Behind an
unconfigured tunnel that is everyone; on a LAN it is already everyone behind
the same NAT. Two listeners in the same house share a public IP.

**Fix:** refuse to start a remote transport with `TrustedProxies` unset, or at
minimum log a warning that names the consequence. Separately, reconsider
whether the failed-auth budget should lock out the whole surface or only the
unauthenticated portion of it — a request that presents a valid credential has
already proved it is not the guesser.

### 3. Per-IP keying is close to free to evade over IPv6

Every key above is a full address string. A caller with an ordinary IPv6 /64 —
which is what a residential or hosting allocation is — has 2^64 distinct
rate-limit buckets, so per-IP budgets do not bound anything an attacker is
willing to rotate addresses for. It also feeds the memory sink
`RateLimiter.IdleWindowsBeforeEviction` exists to bound: keys are
attacker-chosen and eviction is four windows behind.

This does not matter on a LAN, where addresses are scarce and the caller is
already inside. It matters as soon as the caller is not.

**Fix:** key IPv6 by its /64 prefix rather than the full address, in one helper
shared by both servers. Cheap, and it makes the budgets mean what they read as
meaning.

### 4. `/api/admin` has no rate limit at all

`SubsonicEndpoints`' own comment says its surface "had no rate limiting at all,
unlike AdminEndpoints and PairingEndpoints." `PairingEndpoints` does.
`AdminEndpoints` does not — there is no `RateLimiter` in the file.

Severity is low: the routes are gated on a device signature or a live session,
and an unknown fingerprint is refused by a dictionary lookup before any ECDSA
verification happens, so an unauthenticated flood is cheap to refuse. But it is
the one surface where a request can trigger a rescan or a settings write, it is
unbudgeted, and the comment asserting otherwise is wrong. Worth fixing for the
same reason `PairingEndpoints` was.

### 5. The signed canonical form is ambiguous across `&` and `=`

`SignedRequestCanonicalizer.Build` joins query parameters as
`$"{Key}={Value}"` separated by `&`, with no escaping and no length prefix. So
these two different requests canonicalize to the same bytes:

```
?a=1&b=2                 ->  "a=1&b=2"
?a=1%26b%3D2             ->  "a=1&b=2"
```

A signature over one therefore verifies against the other. Exploiting it needs
an attacker positioned to modify a request in flight before it is delivered —
the nonce guard means a request already delivered cannot be replayed — so on
plain HTTP between a TLS-terminating proxy and the server, or on a hostile LAN
segment, it is reachable; over WireGuard it is not.

**Fixed.** Each key and value is percent-encoded before joining, so a value can
no longer imitate a separator. Both ends build this through the one shared
method, so the change is symmetric by construction. The sort now runs on the
encoded form and orders by value as well as key, which also removes an unstated
assumption that both ends flatten a repeated parameter in the same arrival
order.

Related and much smaller: `X-Flower-*` query params are excluded from the
canonical form by design (correctly — the reasoning in the file holds), which
means `X-Flower-Alias` and `X-Flower-PairingCode` are unsigned. Swapping the
alias in flight changes a display string. Swapping the code requires already
holding a valid one. Neither is worth a change; recorded so the exclusion is
not re-derived from scratch next time.

### 6. Classic Subsonic auth is unencrypted-transport-hostile by construction

`SubsonicAuth` accepts `t=md5(password+salt)` and `apiKey=<password>`, both in
the query string, both with no expiry and no nonce. That is the published
protocol and third-party clients require it — it is not a defect in this
implementation, and the credentials themselves are strong (32 chars of CSPRNG
from `SubsonicCredentialStore`, per-client and individually revocable, which is
the part that was gotten right).

What it means is specific: **the mapped-public-port path cannot ship without
TLS.** A captured query string is a permanent credential for the whole library.
Cloudflare Tunnel is fine here because it terminates TLS at the edge; a
UPnP-mapped port on a plain-HTTP Kestrel is not, and that is a hard gate on
step 4 of the transport sequence rather than a nice-to-have. It is the same
conclusion `REMOTE-TRANSPORT-PLAN.md`'s certificate section reaches from the
other direction.

### 7. Bearer tokens in URLs, once URLs leave the house

Two tokens travel in URLs: stream tickets (`/rest/stream?...&ticket=`, 15
minutes, bound to one track id) and the admin session token (URL fragment, 60
minutes, full admin). Both are narrow by design and the reasoning in
`StreamTicketService` and `AdminSessionService` is sound for a LAN.

Exposed, query strings are logged by every proxy in the path and leak via
`Referer`. The stream ticket is defensible — one track, fifteen minutes. The
admin session is the one to look at again: `PeerOrSessionAuth` has already
widened it from "the settings page" to "the catalog and the right to mint
stream tickets," and its own comment names the principled answer (the
non-extractable WebCrypto keypair from `SYNC-PLAN.md`). Off-LAN exposure is the
event that turns that from an interim into a debt.

### 8. Smaller notes, recorded rather than acted on

- `NonceReplayGuard` is per-process and in-memory, so a restart re-opens the
  60-second replay window for a captured request. Bounded and low-value; not
  worth persisting.
- `NonceReplayGuard.Prune` walks the whole dictionary on every record, and the
  self-signed path lets an unauthenticated caller add entries under an
  attacker-chosen fingerprint. Bounded by the redeem rate limit today; would
  need a bound of its own if that limiter is ever keyed more loosely.
- `SyncHttpServer` keys its bulk/browse/stream limiters by self-reported
  fingerprint with no per-IP backstop. Its own comment argues this correctly —
  fake fingerprints buy only rejections — but the rejections are unbudgeted and
  the keys are attacker-chosen. This is the desktop app's embedded server,
  which no transport in the plan exposes, so it stays a note.
- Track and cover-art ids resolve through `Library.Find` and the album grouping,
  never through a caller-supplied path, so there is no traversal surface on
  `/rest/stream`, `/download` or `/getCoverArt`. Checked; nothing to do.
- `Program.cs`'s `ForwardLimit` is sized from the count of configured proxy
  *networks* rather than hops. The middleware re-checks each popped address, so
  this is a ceiling and not a grant, but the two numbers are unrelated and
  happen to coincide only in the one-proxy deployment.

## What this means for the transport sequence

Ordered by what blocks what, not by severity:

1. ~~**Before any transport:** #1 (`/info` gating) and #5 (canonicalization).~~
   **Done** — see each finding above.
2. **Before Cloudflare Tunnel:** #2 — an exposed server must not start with
   `TrustedProxies` unset, and the failed-auth lockout needs revisiting. Plus a
   decision on #7's admin session.
3. **Before a mapped public port:** #6 — TLS, which means the certificate
   design in `REMOTE-TRANSPORT-PLAN.md` has to land first. This is the reason
   step 4 of that sequence is genuinely downstream of step 3 rather than
   parallel to it.
4. **Whenever convenient:** #3 (IPv6 /64 keying) and #4 (`/api/admin` budget).

## Status

**Reviewed; the two pre-transport findings are fixed, the rest is backlog.**

Built: #1 (`/info` answers its address list and `TrustsCaller` only to a
verified trusted peer, and the client signs its poll to be one) and #5 (an
unambiguous canonical form). Both suites pass — `Flower.Tests` 1086/1086,
`Flower.Server.Tests` 169/169 — and a live server was confirmed by hand to
answer an anonymous `/info` with `addresses: null`, where it previously listed
every address it had.

Not verified by hand: that a *paired* client still learns those addresses over
a real network. It is covered by tests at both ends — including
`SyncHttpServerRoundTripTests`, which runs a real `HttpListener` over real
sockets — but the failure mode this change could plausibly introduce is
"pairing still works and the client quietly stops learning addresses", and only
pairing a real client proves it did not happen. That is step 3 of
`REMOTE-ACCESS-PLAN.md`'s own hand-run, which was already outstanding.

Findings #2, #3, #4, #6, #7 and #8 are untouched, and sequenced above against
the transports they gate.
