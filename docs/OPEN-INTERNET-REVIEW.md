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
- ~~`AdminSessionService` — a 60-minute bearer token handed over in a URL
  fragment ("LanGuard keeps it unusable from off the LAN even in the window
  where it is live").~~ **Gone** — both this class and the token are deleted;
  see #7.
- ~~`PeerOrSessionAuth` — widening that bearer token past `/api/admin` to the
  catalog and to ticket minting ("LanGuard still keeps it unusable from off
  the LAN").~~ **Gone** — deleted with it; see #7.
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
was the one that most needed an actual decision rather than a comment, and #7
records the decision taken — the two citations above are struck through because
the code that made them is gone, so three of the five sites remain.

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

**Fixed, in both halves.**

*Refusing to start turned out not to be available.* `cloudflared` dials **out**
and delivers over loopback, so nothing about the process, the bind or the config
says a tunnel exists — there is no startup state to check, and a check on
`TrustedProxies` being empty alone would fire on every ordinary LAN deployment.
The one signal that does exist is a request carrying an `X-Forwarded-For` from a
hop that is not trusted to write one, which is exactly the shape of an
undeclared proxy. `ProxyHeaderAudit` watches for it and logs a warning naming
the address and the consequence, at most once every five minutes. Also catches
the likelier Docker mistake: `TrustedProxies` set, but to the wrong address, so
nothing is believed and the operator has no way to tell.

A warning rather than a refusal on purpose. A refusal would have to be a 403 at
request time — turning "your rate limits are pooled" into "your server is down",
on a signal any caller can produce by sending a header.

*The lockout is now a throttle on guessing rather than on the surface.*
`FailedAuthLimiter` gates only the password path, and only after signatures and
stream tickets have had their turn — neither can be guessed, so nothing is
bought by refusing one because somebody sharing the address got a password
wrong. A paired Flower device is therefore unaffected by a guesser behind the
same NAT, which was the sharp consequence above.

The guessing bound is preserved where it matters: an over-budget attempt is
refused **without being evaluated**, so burning the budget can never admit a
lucky guess. The budget is now keyed by source *and* username, so hammering one
account cannot lock out another client behind the same address. An attacker
rotating usernames does get a fresh budget each, bounded only by
`RequestLimiter` at 600/60s — which is worth stating plainly: against
`SubsonicCredentialStore`'s 32-char CSPRNG secrets, never human-chosen,
guessing was never the threat this bounds. What it bounds is a probe flood, and
it still does.

### 3. Per-IP keying is close to free to evade over IPv6

Every key above is a full address string. A caller with an ordinary IPv6 /64 —
which is what a residential or hosting allocation is — has 2^64 distinct
rate-limit buckets, so per-IP budgets do not bound anything an attacker is
willing to rotate addresses for. It also feeds the memory sink
`RateLimiter.IdleWindowsBeforeEviction` exists to bound: keys are
attacker-chosen and eviction is four windows behind.

This does not matter on a LAN, where addresses are scarce and the caller is
already inside. It matters as soon as the caller is not.

**Fixed.** `RateLimiter.KeyFor(IPAddress?)` is the one helper every per-source
budget in both servers now keys through — the four call sites that each built
their own `RemoteIpAddress?.ToString() ?? "unknown"` string no longer do.

Two carve-outs, both deliberate. An IPv4-mapped address (`::ffff:a.b.c.d`, what
Kestrel hands out for an IPv4 client on a dual-stack socket) is keyed as the
IPv4 address it is; collapsing it as IPv6 would have put *every* IPv4 caller in
one bucket, which is worse than the problem being fixed. And link-local is left
at full precision: nothing off-link can source an `fe80::` address, so there is
no rotation to bound, while collapsing it would put every device on a link into
one bucket — exactly the LAN case these limiters have to keep working for.

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

**Fixed.** A 120/60s per-source budget on the `/api/admin` group's filter,
charged before authentication runs — "cheap to refuse" is an argument for a
generous ceiling, not for none. Sized for a human driving the settings page,
which opens by fetching devices, credentials, settings and the log at once,
rather than for a poll loop; nothing polls these routes. The wrong comment in
`SubsonicEndpoints` is gone with it.

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

### 7. Bearer tokens in URLs, once URLs leave the house — **fixed**

Two tokens travelled in URLs: stream tickets (`/rest/stream?...&ticket=`, 15
minutes, bound to one track id) and the admin session token (URL fragment, 60
minutes, full admin). Both were narrow by design and the reasoning in
`StreamTicketService` and `AdminSessionService` was sound for a LAN.

Exposed, query strings are logged by every proxy in the path and leak via
`Referer`. The stream ticket is defensible — one track, fifteen minutes, and
it is what an `<audio>` element plays on because a media element cannot present
a credential of its own. It stays.

The admin session was the one to look at again, and looking at it the answer
was not to narrow it. It had already widened once — `PeerOrSessionAuth` took it
from "the settings page" to "the catalog and the right to mint stream tickets"
— and every narrowing available was a smaller version of the same wrong shape:
a bearer credential, in a URL, standing in for a device that could not
authenticate itself. Off-LAN exposure removes the one thing that made it
tolerable.

**Fixed by making the browser a device**, which is what `SYNC-PLAN.md`'s "the
browser is a device" always said and what `AdminSessionService`'s own comment
named as the principled answer.

`Flower.Web/wwwroot/webcrypto.js` generates an ECDSA P-256 keypair with
`extractable: false` and keeps it in IndexedDB as a `CryptoKey`, never as
bytes. The private half is a handle the page can sign with and nothing —
including that module — can read out, which is a *stronger* storage guarantee
than the file-backed key the desktop has. The formats needed no bridging: `raw`
export is `0x04 || X || Y`, exactly `DeviceKeyStore.PublicKeyRaw`, and a
WebCrypto ECDSA signature is raw `r‖s`, which is
`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`. `SignatureVerifier`
accepts a browser's signature with no new code path at all.

What the URL fragment now carries is a **single-use pairing code**, the same
one every other device redeems, spent within a second of the page loading and
worthless afterwards. The desktop's "Server Settings…" button issues an
admin-granting code instead of minting a session; the server's first-run console
prints the code it already prints, addressed at a browser as well as at an app.

Deleted outright rather than kept as a fallback (`CLAUDE.md`, "No Users Yet"):
`AdminSessionService`, `AdminSessionCredentials`, `PeerOrSessionAuth`,
`POST /api/admin/sessions`, and the `X-Flower-Admin-Session` header. There is
no bearer credential left in the project. `/api/admin`, `GET /library` and the
ticket route all gate on a device signature and nothing else.

**What it cost.** `IPeerCredentials.Authorize` had to become
`AuthorizeAsync` — `crypto.subtle` is a promise — which is the re-shaping of
every signing call site that `PeerOrSessionAuth`'s comment predicted would make
this not a drop-in. It reached twelve call sites plus
`OpenSubsonicClient.BuildUrl`/`GetStreamUrl`/`GetCoverArtUrl`, all of which
still complete synchronously on every head but the browser.

**What it requires, stated plainly: a secure context.** Browsers expose
`crypto.subtle` only over HTTPS and on `localhost`, so a tab opened at
`http://192.168.1.x:4533` now has no key and cannot pair. That is a real
regression for plain-HTTP LAN browsing, and it is not one this change can avoid
— it is the browser's rule, not the server's. It is also aligned with where
everything else here is going: every remote transport under consideration
terminates TLS (Cloudflare Tunnel at the edge, Tailscale via `tailscale cert`),
and #6 already makes TLS a hard gate on the mapped-port path.

Two things were then needed to keep that regression from reading as a bug. The
first surfaced immediately in testing: the desktop's "Server Settings…" button
built its link from the address the client had dialled, which for a client that
found the server over mDNS is the LAN one — so the button reliably opened a tab
that could not pair, even with both ends on the same machine. The server now
chooses the origin (`WebUiHosting.BrowserOriginFor`): `localhost` when the
caller is on this machine, the dialled address otherwise. Answering *that*
needs more than a loopback check, since a same-machine client that dialled our
LAN address arrives with that address as its source — hence
`LocalAddresses.IsThisMachine`, which asks the question of every address we
hold.

The second is what the failure says. A 401 carries no reason, and the message
inferred from it — "This device is not paired with that server." — sent the
user to pair again, which cannot ever succeed when there was no key to pair.
`BrowserPeerCredentials` keeps the real reason (no secure context, or a browser
refusing IndexedDB) and `ServerAdminClient` asks the caller for it before
falling back to its guess.

**Verified.** The real `webcrypto.js` was run against a live server: it
generated a key, redeemed a printed pairing code with a self-signed request
(200, `isAdmin: true`), then signed its way into `GET /api/flower/v1/library`,
`GET /api/admin/settings` and `POST /api/flower/v1/stream-tickets` — all 200,
with no bearer token anywhere. The same server answered 401 to an
`X-Flower-Admin-Session` on `/api/admin`, 403 on the sync surface, and 404 for
`POST /api/admin/sessions`. `BrowserSignatureFormatTests` pins the format
contract as a fixed vector generated from that module, so the three encoding
choices it depends on cannot drift unnoticed.

**And verified in a real browser**, which is the leg no test reaches: clicking
"Server Settings…" in the desktop client opens a tab that generates its key,
spends the code, and administers the server it was opened against. That is the
whole path end to end, in the thing it was written for.

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
2. ~~**Before Cloudflare Tunnel:** #2 — `TrustedProxies` enforcement and the
   failed-auth lockout, and #7 — the admin session bearer.~~ **Done** — see
   each finding above. Nothing is left between here and turning the tunnel on.
3. ~~**Before Cloudflare Tunnel:** the `LanGuard` question the framing finding
   raises — what actually replaces it.~~ **Answered by building the switch**:
   `FlowerServerOptions.AllowPublicAccess` retires the guard by name rather than
   by an `AllowedCidrs` of `0.0.0.0/0`, warns at every startup, and cannot be
   set from the browser page. `PublicAccessTests` pins both halves — that a
   public caller gets in, and that getting in still buys them nothing unsigned.
4. **Before a mapped public port:** #6 — TLS, which means the certificate
   design in `REMOTE-TRANSPORT-PLAN.md` has to land first. This is the reason
   step 4 of that sequence is genuinely downstream of step 3 rather than
   parallel to it.
5. ~~**Whenever convenient:** #3 (IPv6 /64 keying) and #4 (`/api/admin`
   budget).~~ **Done** — see each finding above.

## Status

**Reviewed; everything Cloudflare Tunnel needs is fixed, including the bearer
token that was the last thing standing before it.**

Built: #1 (`/info` answers its address list and `TrustsCaller` only to a
verified trusted peer, and the client signs its poll to be one), #5 (an
unambiguous canonical form), #3 (one shared per-source rate-limit key, IPv6
collapsed to its /64), #4 (a budget on `/api/admin`) and #2 (an undeclared proxy
warns about itself, and the failed-auth lockout no longer takes the surface away
from bystanders) and #7 (the browser holds a non-extractable WebCrypto keypair
and signs; every bearer credential is deleted). Both suites pass —
`Flower.Tests` 1095/1095, `Flower.Server.Tests` 178/178 — and a live server was
confirmed by hand to answer an anonymous `/info` with `addresses: null`, where
it previously listed every address it had. Pairing was confirmed by hand to
still work afterwards.

Still outstanding from `REMOTE-ACCESS-PLAN.md`'s own hand-run, and unchanged by
any of this: the tailnet leg — that a paired client off the LAN reaches the
server on its `100.x` address and hands back to the LAN one on the way home.
Nothing here can stand in for it.

The proxy warning was confirmed by hand as well: a request carrying an
`X-Forwarded-For` logs it naming `127.0.0.1`, a second one inside the interval
does not, and a request without the header logs nothing.

The browser's own path was confirmed against that same live server, driving the
real `webcrypto.js`: key, redeem, and then signed access to the catalog, the
admin settings and a stream ticket, with the bearer header refused everywhere.
It has since been confirmed inside an actual browser tab as well, opened the way
a user opens it — the "Server Settings…" button — which is what turned up the
LAN-address link and the misleading 401 described in #7.

Finding #6 is untouched — a decision attached to a mapped public port rather
than a fix that stands on its own, and sequenced above against it. #8 is notes.
