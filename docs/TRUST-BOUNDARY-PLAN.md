# Trust boundary: rogue clients, budgets, and what a refusal means

**Status (2026-10-02): done.** All five steps landed, one commit each. Where a
step turned out differently from what is written below, its "Done" note says
so, and those notes are the most useful part of re-reading this. `docs/API.md`
is the reference for the result; this file is why it is shaped that way.

## Why

Writing `API.md` turned up three loose ends: two routes with no rate limit, `403`
meaning different things on the device and admin surfaces, and a dead log
branch. Checking them against a running server found something more important.
The design assumes two kinds of caller, strangers and paired devices, and treats
a paired device as well-behaved. CLAUDE.md says otherwise: listeners are other
people, sometimes outside the house, and "it's just for me" reasoning does not
apply at the trust boundary. A listener's phone can be buggy, stolen or hostile.
So can anything that sits between a client and the server, such as a captive
portal, a proxy, or a device that picked up the server's old DHCP address.

What each kind of rogue caller can do today. All of this was read from the code
and, for the budgets, checked against a running server on 2026-10-02:

**A paired device that is not an admin**

- **Rewrites or deletes every playlist on the server.** `POST /playlists/apply`
  has no admin check. `PlaylistSyncPlaylistDto.Deleted` may name any playlist,
  and a pushed copy wins whenever its `UpdatedAt` is later, which is a value the
  caller chooses (`PlaylistSyncMapper.ApplyPushedManifest`, `Supersedes`).
- **Inflates the server's own play counts.** `/plays` goes through
  `Library.RecordPlay`, which bumps `PlayCount` and stamps `LastPlayed` on the
  shared library. It is not filed under the caller's fingerprint, unlike
  `/track-state`.
- **Grows the server's memory.**
  - `PlayReportService` keeps every distinct `EventId` for six hours, keyed by
    id alone. A request may be 20 MB and the bulk budget allows 60 a minute.
  - `StreamTicketService` has no budget at all (`/stream-tickets` is outside
    every group) and no cap per device. It prunes only when a new ticket is
    issued.
  - `ClientLogStore` bounds a device's log by age (seven days) and not by size.
- **Shares a budget with everyone at its address.** Every budget is keyed by
  source IP and charged before authentication. Behind a tunnel or CGNAT that
  does not name its proxy in `TrustedProxies`, every listener is one bucket, so
  one device can spend everyone's.

**Anything that answers at the server's address**

- **Unpairs a client.** Two things make a client discard its pairing for good
  (`PeerSyncCoordinator.HandleTrustRevoked` → `UnpairServer`):
  - a `403` on `GET /library` or `GET /playlists` (`LibrarySyncService`,
    `PlaylistSyncService`);
  - `trustsCaller: false` in an `/info` reply (`NetworkDiscoveryService`).

  Neither is authenticated. `/info` is plain HTTP on the LAN, and a status code
  can come from anything on the path. Over a Cloudflare tunnel TLS ends at
  Cloudflare, so even an `https` origin does not prove a `403` came from
  `Flower.Server`.

**A stranger**

- Polls `/info` and `/stream-tickets` without limit: 300 requests each from one
  address got 300 answers, after the bulk and admin budgets for that address
  were already spent. Both refusals are cheap, so this is the smallest item
  here.

## How comparable servers handle it

- **AWS SigV4** is the closest analogue to Flower's request signing. Every
  authentication failure is a `403`. The reason is a code in the body
  (`InvalidAccessKeyId`, `SignatureDoesNotMatch`, `RequestTimeTooSkewed`), and
  the clock-skew error carries the server's time. The status code stays coarse;
  the body says why.
- **Navidrome** rate-limits login by IP: by default 5 attempts per 20-second
  sliding window (`AuthRequestLimit`, `AuthWindowLength`).
- **Jellyfin** locks a user out after N failed logins, and leaves rate limiting
  to a reverse proxy or fail2ban.
- **Home Assistant** can ban an IP after N failed logins (`ip_ban_enabled`,
  `login_attempts_threshold`). Its documentation warns that behind a reverse
  proxy every request arrives from the proxy's address.

These are password systems, so the threat they defend is guessing. Flower's
only guessable secret is the pairing code, which already allows 5 attempts a
minute. Flower's distinctive risks are the ones they model least: a
*verified* caller that misbehaves, and per-address budgets that collapse behind
a proxy. That is why steps 1 and 2 are about authorizing and budgeting **per
device, after authentication**, and Home Assistant's warning is why the
per-address half below counts failures rather than requests.

## Principles

1. **A verified signature says who, not what.** Every write is checked against
   what that device may change. The rule already exists for track state
   (`MergeReportedTrackState`) and the Continue Playing shelf
   (`ExchangeAlbumProgress`); it is missing for playlists and plays.
2. **Strangers are budgeted by address, devices by fingerprint.** An address
   budget that every listener behind one proxy shares is a budget for none of
   them.
3. **Nothing unbounded is kept for a caller.** Every per-device table has a
   size cap as well as a lifetime.
4. **A status code says what to do next; a code in the body says why.**
5. **Only the server's pinned key can revoke a pairing.** A destructive client
   action is never taken on a message the client cannot authenticate.

## Step 1 — Authorize what a paired device may write

### Playlists belong to a listener

This is the album-progress model (`AlbumProgressLedger`). A **listener** is the
owner, meaning every admin device together, or one non-admin device on its own.
Each listener has its own set of playlists on the server.

| Route | Today | After |
|---|---|---|
| `GET /playlists` | Every playlist on the server | The caller's listener's playlists |
| `POST /playlists/apply` | Rewrites the whole server set | Rewrites the caller's listener's set |
| `Deleted` | Any playlist | Only the caller's listener's |
| A pushed id held by another listener | Overwrites it | Ignored, and returned in `Refused` |

- **Schema.** Add a migration in `Schema.cs` that gives `playlists` a
  `listener TEXT NOT NULL DEFAULT 'owner'` column. `'owner'` is the shared value
  for admin devices, as in `AlbumProgressLedger.OwnerShelf`; a non-admin device's
  value is its fingerprint. Existing server playlists become the owner's, which
  is correct: today there is no way to tell who made them. The client's own
  database never uses the column.
- **`PlaylistSyncMapper.ApplyPushedManifest`** takes the listener and composes
  the result from three parts: other listeners' playlists, untouched; this
  listener's playlists merged with the push; and pushed ids belonging to another
  listener, dropped and reported.
- **`POST /playlists/apply` answers `200` with `{ Refused: [ids] }`** instead of
  `204`. A client drops the refused ids from its own set. That is how a guest
  device already holding copies of the owner's playlists, from before this
  change, gets rid of them. Without it, the guest would push them again on every
  sync, forever.
- **`/info`'s `playlistsToken` becomes per listener** for a verified caller, so
  a guest editing a playlist does not make the owner's devices re-sync.
- **`SmartPlaylistRefresher`** keeps evaluating every smart playlist on the
  server, whoever owns it. A smart playlist's rules run against the server's
  library either way.

**Consequence, decided:** a guest no longer sees the owner's playlists, in the
same way it does not see the owner's Continue Playing shelf. If sharing comes
back, it comes back as an explicit, read-only publication by the owner, never as
write access.

### Plays are filed under the device that made them

- **`/plays` records a play under the caller's fingerprint**, by incrementing
  that device's entry in `Track.RemotePlayCounts`. This is the same attribution
  `/track-state` uses, applied to events instead of totals.
- **`LastPlayed` and the server's own `PlayCount` move only for an admin
  device**, by the rule `MergeReportedTrackState` already applies to
  single-valued fields.
- **Dedupe is keyed by `(fingerprint, EventId)`.** A report holds at most 500
  events, and an `EventId` is at most 64 characters.

**Consequence, open:** a guest browser tab's History is read from the
catalog's `LastPlayed`, so after a refresh it will not show what that guest
played. Per-listener history is the real fix. It is out of scope here and is
listed under "Not in this plan".

### Bounded tables

- **Stream tickets:** at most 32 outstanding per fingerprint. Minting a 33rd
  drops the oldest. Prune on redeem as well as on issue.
- **Pushed logs:** at most 20,000 lines per device, keeping the newest, and at
  most 5,000 lines per report.
- **Body caps per route** instead of one 20 MB cap for everything. Set each
  from the largest real body a 16k-track library produces, measured, with a 4×
  margin. Suggested starting points until measured:

| Route | Cap |
|---|---|
| `/playlists/apply` | 20 MB (unchanged) |
| `/track-state` | 8 MB |
| `/log/report` | 4 MB |
| `/plays`, `/album-progress`, `/cover-art/batch` | 256 KB |
| Admin JSON routes other than uploads | 1 MB |

**Done (2026-10-02).** What it turned out to be, where that differs from the
above:

- **The client does not drop refused playlists itself.** It logs them. Its
  planner already reads a playlist that is missing on the server, with a
  baseline and no local edit, as deleted there, so the next session drops it.
  Deleting on the strength of an unsigned answer would have given anything on
  the path a new way to remove a playlist.
- **Pushed logs are capped by size, not by 20,000 lines.** A phone's own
  archive runs to about 50 MB of these lines a week, which is a few hundred
  thousand lines, so 20,000 would have kept less than a day. The caps are
  128 MB per device, oldest day first, and 5,000 lines per report. A report
  over the line limit keeps its oldest lines, so the watermark advances and the
  rest follows. The client now sends its backlog in reports of at most 5,000
  lines or about 2 MB, up to ten a session. Before, it sent the whole backlog
  as one request, which a week-long backlog made larger than the server would
  ever take.
- **The body caps were measured, and two routes keep 20 MB.** A full
  `/track-state` restatement for 16,000 tracks is 5.3 MB with every owner field
  set, so `/track-state` keeps 20 MB alongside `/playlists/apply`.
  `/log/report` is 4 MB. Everything else is 256 KB: 500 play events are 82 KB
  and a full Continue Playing exchange is 93 KB. The admin routes were left at
  20 MB, because their real exposure is a body buffered before the signature
  is checked, and that is step 2's to close.
- **An admin's tab still counts into the library's own `PlayCount` and
  `LastPlayed`**, as every tab used to. Only a non-admin's finished plays move
  to its fingerprint. Event-id dedupe is per device, capped at 5,000 ids.
- **Found while doing it, for step 2:** a request whose body has no
  `Content-Length` (chunked) is verified as if its body were empty, and the
  handler then reads the real body, which nobody signed. Both group filters
  buffer only when a length is stated. Clients always state one, so step 2
  answers such a request `411`.

Tests, all passing:

- a guest cannot read, change or delete the owner's playlists, and gets the
  ids back in `Refused` (`SyncEndpointTests`);
- a guest's own playlists are kept apart from the owner's;
- a guest's playlist edit moves only its own `playlistsToken`
  (`DiscoveryEndpointTests`);
- a guest's plays move its own `RemotePlayCounts` entry and nothing else;
- an over-long report or event id is a `400`, an oversized body a `413`;
- a device's 33rd ticket evicts its first (`StreamTicketServiceTests`);
- a log push keeps its oldest 5,000 lines, and a device over its size cap loses
  its oldest days (`ClientLogStoreTests`);
- a large client backlog goes in several reports, each under the cap
  (`LibrarySyncLogPushTests`);
- the listener column round-trips, and a version-10 database gives every
  existing playlist to the owner (`StoreRoundTripTests`).

## Step 2 — Budgets per device, failures per address

The pre-authentication filter in each group becomes three checks, in order:

1. **Claimed fingerprint unknown** (no key on file, or no fingerprint at all).
   Refused with no body buffered and no signature checked. This costs a
   dictionary lookup, as the admin upload filter already does. These refusals
   are counted per source address. Past 30 a minute, further unknown-fingerprint
   requests from that address are dropped with a logged line, exactly as
   `LanGuard` drops outsiders.
2. **Known fingerprint, signature fails.** Counted per `(address, fingerprint)`.
   Past 10 a minute that pair gets a `429`. The same device on any other address
   is unaffected, so an attacker who knows a device's fingerprint cannot lock
   that device out by failing on its behalf.
3. **Verified.** Charged to the device's own budget for the plane, keyed by
   fingerprint. The three device planes keep today's numbers (bulk 60, art 600,
   media 240 a minute), as do the two admin planes (120 and 3000).

Listeners behind one proxy then share nothing but the failure counters, which
only a caller that is failing can spend. `TrustedProxies` still matters for
logs and `LanGuard`, but stops mattering for budgets.

The routes outside the groups:

- **`/info`:** 120 a minute per address. Unauthenticated callers are cheap,
  and paired clients poll every ~5s, which stays well under it.
- **`/stream-tickets`:** moves into the sync group's filter, charged to the
  media plane per device.
- **`pair-redeem`:** stays at 5 a minute per address.

**Done (2026-10-02).** One `RequestGate`, a DI singleton, now runs the same
checks for the sync group, the admin group, `/stream-tickets` and `/info`.
The static per-address limiters it replaced were shared by every host a test
run boots. Where it differs from the above:

- **It also refuses a body that does not state its length**, with `411` on
  every signed route. Step 1 found that a chunked body was verified as empty
  and then read, unsigned, by its handler.
- **A stranger past its budget gets a `429`, not a dropped connection.** A
  stranger on an allowed network is more often a tab that has not paired yet
  than an attacker, and a dropped connection reads to it as "server down".
  `LanGuard` still drops everyone outside the allowed networks.
- **`/info` is budgeted in two halves**, because it answers strangers rather
  than refusing them. An unsigned call, from a stranger or a client with no
  key yet, is charged to the address (120 a minute). A signed one goes through
  the failure budget and is then charged to the device. That device budget is
  240 a minute, not 60. A client polls once per address it knows the server by,
  every five seconds, and again on every network change. It reads a refusal as
  "unreachable", which is worse than the traffic.
- **`/stream-tickets` stays outside the sync group** but goes through the same
  gate on the media plane. It keeps its `401` for an unknown device until step
  3 settles what each code means.
- **A ticketed stream is charged to the device that minted the ticket.**
  `StreamTicketService.TryRedeem` now names it, which brings forward half of
  step 5's ticket change.
- **The upload pre-check, kept here and removed after step 5.** The upload
  routes answered `403` to a request that merely *claimed* a known non-admin
  fingerprint, before checking its signature, to spare buffering its body. An
  admin's fingerprint got `401` once the signature failed, so naming
  fingerprints with a junk signature told anyone which devices were admins,
  which `/info` is careful never to do. Once this step's gate had capped failed
  signatures, the pre-check spared little and spared nothing for an admin's
  fingerprint, so it went. `not-admin` is now said only to a request whose
  signature verified, and
  `A_failed_signature_answers_the_same_whatever_the_role_of_the_device_it_names`
  holds the upload routes to it.
- **An existing flaky test:** `The_log_is_readable_from_the_admin_api` fails
  about one run in three on the step-1 tree as well. Another test's log line
  lands between its two reads.

Tests, all passing (`RequestGateTests`):

- two devices behind one address do not share a budget;
- failing signatures for a device from one address do not lock it out from
  another;
- a stranger flood from an address does not refuse a paired device there;
- `/info` has a stranger budget that a paired device at the same address does
  not share;
- minting tickets is charged to the device's media budget;
- a body that does not state its length is refused.

## Step 3 — One error contract

Every refusal is `application/problem+json` (RFC 9457): `type`, `title`,
`status`, plus a `code` member. The status codes mean the same on every
surface: **`401` means authentication failed, `403` means authenticated but not
allowed.**

| Status | `code` | Meaning | Client does |
|---|---|---|---|
| `400` | `invalid-request` | Malformed body or query | Drop the request |
| `400` | `pairing-code-invalid` | Code wrong, expired or used | Ask for the code again |
| `401` | `device-unknown` | No key on file for the claimed fingerprint | Revocation, but only when signed (step 4) |
| `401` | `signature-invalid` | Key on file, signature wrong | Retry once with a fresh signature |
| `401` | `clock-skew` | Timestamp outside ±60s. Carries `serverTime` | Correct the clock offset, retry once |
| `401` | `nonce-reused` | Nonce already seen | Bug: sign every attempt afresh |
| `403` | `not-admin` | Paired, but this route is an admin's | Hide the control |
| `404` | `not-found` | No such track, upload or device | Per route |
| `409` | `conflict` | A different file is already at that path | Final for this file |
| `413` | `too-large` | Over the route's body cap | Drop the request |
| `422` | `corrupt` | Upload bytes do not match the promised hash | Send again |
| `429` | `rate-limited` | Over budget. `Retry-After` is set on every plane | Wait `Retry-After` |
| `503` | `scanning`, `unavailable` | Library scan running, or nowhere writable | Pause the batch |

On the sync group, `device-unknown` moves from `403` to `401`. On the admin
group, `401` stops conflating "unknown" with "bad signature", so a device with
a skewed clock is no longer told it is not paired. The move ships together with
the client change and has no fallback; CLAUDE.md is explicit that compatibility
is not a constraint.

The same change removes the last trace of the OpenSubsonic error shape.
`SeekableHttpStream.ProtocolErrorFor` exists to recognise an error envelope
delivered on a `200`, which is how the deleted adapter refused a replayed nonce.
`Flower.Server` never answers that way. The device checks'
`LoopbackMediaServer.RequiresFreshNonce` answers a repeat with `401
nonce-reused` instead, and `ProtocolErrorFor` is deleted. The device-checks
section of CLAUDE.md is updated to match.

Client side:

- `ServerAdminClient`, `LibrarySyncService`, `PlaylistSyncService` and
  `SeekableHttpStream` read `code`, not the bare status.
- `Classify` in `LibrarySyncService` maps codes to `SyncFailure`.

**Done (2026-10-02).** The contract is `FlowerProblem` in `Flower.Core`, so both
ends use one definition. The server writes it through `Problems`, and every
refusal under `/api` that leaves without a body is filled in by status in a
fallback. Where it differs from the above:

- **`SeekableHttpStream.ProtocolErrorFor` stays; only its OpenSubsonic half is
  gone.** The plan said to delete it, but its rule is "a 2xx with a textual body
  is not audio", and that also catches a captive portal or a proxy's sign-in
  page. Only the parsing of the old error envelope went. The device checks'
  not-audio check moved to `LoopbackMediaServer.ServesAPortalPage`, and
  `RequiresFreshNonce` now refuses with `401 nonce-reused`.
- **A clock-skew refusal corrects the clock rather than retrying everywhere.**
  `SignatureClock` holds an offset that both signers use, and a
  `SignatureClockHandler` in every client `PeerHttpClient` builds sets it from
  `serverTime`. So the next request goes through, and the sync loop's next tick
  is the retry. Only `PeerCredentialsHandler`, which signs its own media
  requests, sends the request again at once. Adding a retry to every call site
  would have meant re-signing requests from outside the code that signed them.
- **Found by the route-walking test:** minimal-API parameter binding runs
  before endpoint filters. So `PUT /library/uploads/{id}` without an `offset`
  answered a stranger `400` with the exception's text, before anyone was
  authenticated. The required bound parameters (`offset`, the ticket `id`)
  are optional now and checked after the gate. `ThrowOnBadRequest` is off, so
  any binding failure left is a coded `400`, not exception text.
- `fallback` codes never guess `device-unknown`. A bare 401 is read as
  `signature-invalid` and a bare 403 as `not-admin`, the readings that cost a
  client nothing.

Tests, all passing:

- every `/api` route in the routing table refuses a stranger with a coded
  problem, `device-unknown` everywhere a device signs (`RefusalContractTests`,
  server);
- an unmapped `/api` path is a coded `404`;
- a stale signature is `clock-skew` with the server's time;
- `device-unknown` is the only refusal that reads as revoked; `clock-skew`,
  `signature-invalid`, `nonce-reused` and a bare HTML `403` leave the pairing
  alone; a `clock-skew` refusal corrects the client's clock; a signed media
  request is sent again once in the corrected time (`RefusalContractTests`,
  client).

## Step 4 — Only the pinned key revokes

The server signs the responses a client acts on destructively, and the client
acts only on what it can verify.

- **What is signed:** every `/info` response, and every refusal whose `code`
  is `device-unknown`.
- **Headers:** `X-Flower-Server-Signature` and `X-Flower-Server-Timestamp`.
- **Signed over** the same `SignedRequestCanonicalizer` shape, with these
  fields:

```
RESPONSE
<request path>
<status>
lowercase-hex-sha256(body)
<server timestamp>
<the request's X-Flower-Nonce>
```

  Binding the request's nonce means an old "you are revoked" cannot be
  replayed at a device that has since been paired again.
- **Who checks:** the client verifies against the public key whose fingerprint
  it pinned at pairing. `/info` already serves that key, and `PairingEntry`
  already checks it against the pin.
- **Revocation** is then:
  - a verified `/info` with `trustsCaller: false`, or
  - a verified `401 device-unknown`.

  Anything unverified, whether a bare `403`, an unsigned `trustsCaller: false`,
  or a refusal whose signature fails, is treated as "unreachable" and logged
  once. It never unpairs.
- **An anonymous `/info` probe** sends no nonce and gets an unsigned answer.
  It carries no `trustsCaller` to act on, so nothing is lost.

This works the same over plain HTTP on a LAN, over the tailnet, and through a
tunnel that terminates TLS somewhere else. It also takes the destructive meaning
off status codes entirely, which is the real answer to `403` meaning two things.

**Done (2026-10-02)**, as described above. The signed form reuses
`SignedRequestCanonicalizer`, with `RESPONSE` as the method and the status as
the one query pair, so there is one canonical form, not two. Where it differs:

- **On `/info`, the key is checked against the answer's own fingerprint.**
  `NetworkDiscoveryService` does not hold the pin, so the comparison with the
  pinned fingerprint happens one step later, in
  `PeerSyncCoordinator.HandlePeerTrustChanged`, which already ignored any
  device that was not the paired server. Together the two checks mean "the
  paired server, and nobody else, said so". On the sync routes the key is
  `DiscoveredDevice.PublicKey` and the fingerprint is the device row's.
- **The browser head is left reading `trustsCaller` unsigned.** It decides only
  whether to show the pairing screen, and keeps its key. A tab's code comes
  from that same origin, so whatever could forge the answer could replace the
  page.

Tests, all passing:

- a client facing a bare `403` keeps its pairing (step 3's test, still
  passing);
- a `device-unknown` the paired server did not sign, unsigned or signed by
  another key, leaves the pairing alone; one it did sign reads as revoked
  (`RefusalContractTests`, client);
- an `/info` "no" is believed only when its own key signed it;
- a signed refusal does not verify as the answer to a different request;
- the real server's `/info` answer and `device-unknown` refusal verify against
  its key, and only for the nonce they answered (`RefusalContractTests`,
  server).

## Step 5 — Cleanups

- Delete the `u` branch in `MediaEndpoints.StreamPeer`. It dates from the
  adapter (`0d50354`), and nothing sends `u` any more.
- `StreamTicketService.TryRedeem` returns the fingerprint the ticket was minted
  for. `StreamPeer` logs that, rather than an `X-Flower-Fingerprint` header that
  a ticketed request is free to make up.
- Rewrite `docs/API.md`'s authentication, rate-limit and refusal sections from
  the code as it then stands. Fold this plan's status into it, and mark this
  file done.

**Done (2026-10-02).** The ticket half came early, in step 2, which needed
`TryRedeem` to name the minter in order to charge playback to it. The sync
filter now records that minter for the request, and `StreamPeer` logs either
the fingerprint whose signature verified or the ticket's minter, marked
"(by ticket)", never a header. `StreamLogTests` holds it to that with a forged
header and a `u=` on a ticketed request. `API.md` was kept current step by
step, and was read through once more against the code at the end.

## Not in this plan

- **Per-listener history**, which would let a guest browser tab keep its own
  History. See step 1.
- **Owner-published, read-only playlists for guests.**
- **Moving the signing scheme to RFC 9421** (HTTP Message Signatures). It is
  what Flower's scheme would be if it followed a standard, but nothing
  third-party speaks Flower's protocol, so there is nothing to interoperate
  with.
- **A rogue admin device.** An admin is the owner. The defence is revoking that
  device from another admin device, which exists today.
- **What a revoked guest leaves behind.** Its playlists stay on the server
  under its fingerprint, as its Continue Playing shelf already did. Nothing
  reads them, and revoking a device does not delete them.
- **A guest re-paired as an admin.** Its listener changes from its fingerprint
  to the owner's, so it no longer sees the playlists it kept as a guest. There
  is no route that changes a device's admin flag, so the only way here is
  re-pairing the same key with an admin code.

Sources for the comparisons:

- [Navidrome configuration options](https://www.navidrome.org/docs/usage/configuration/options)
- [Jellyfin users](https://jellyfin.org/docs/general/server/users/)
- [Home Assistant http integration](https://git.jeena.net/jeena/home-assistant.github.io/src/branch/rc/source/_components/http.markdown)
- [AWS error codes (Zenko reference)](https://zenko.readthedocs.io/en/2.3.2/reference/error_codes/aws_error_codes.html)
