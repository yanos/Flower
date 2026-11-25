# Flower HTTP API

HTTP API for clients connecting to `Flower.Server`. Most routes require a paired device; exceptions are noted below. Flower uses its own protocol and doesn't support Subsonic clients.

**Compatibility:** The API may change between early releases. Check your app and server versions when integrating.

For server installation, use [Self-hosting](SELF-HOSTING.md). For the trust
model, see [Security](SECURITY.md). The route implementations are in
[`Flower.Server/Endpoints/`](../Flower.Server/Endpoints/).

[Routes](#routes) · [Limits](#before-any-route-runs) ·
[Authentication](#authentication-a-signed-request) · [Discovery](#discovery) ·
[Pairing](#pairing) · [Device API](#device-endpoints) ·
[Admin API](#administrator-endpoints) · [Change tokens](#change-tokens)

## Routes

| Prefix | What it is | Who may call | JSON casing |
|---|---|---|---|
| `GET /api/localsend/v2/info` | Identity handshake | Anyone the network gate admits | camelCase |
| `POST /api/flower/v1/pair-redeem` | Turn a pairing code into a trusted device | Anyone holding a code | camelCase |
| `/api/flower/v1/*` | Catalog, playlists, state, art, audio | Any paired device | **PascalCase** |
| `POST /api/flower/v1/stream-tickets` | Mint a URL an `<audio>` element can open | Any paired device | camelCase |
| `/api/admin/*` | Devices, settings, library writes, logs | Paired devices flagged admin | camelCase |
| `GET /licences` | `NOTICE` and the licence texts, as `text/plain` (`OpenSourceNotices`); the browser UI's "Open-Source Licences" link | Anyone the network gate admits | — |

**Important: JSON casing varies by route.** The device API returns
PascalCase (`Songs`, `DeviceFingerprint`, `Ids`) and reads case-insensitively.
Other endpoints write camelCase. Nulls are omitted except in `/info`, where
`null` can represent an unknown trust state.

Unknown paths under `/api` return `404 not-found` from the single-page app fallback (`WebUiHosting`), which handles every method. Media routes don't support `HEAD` because they use `MapGet`; to determine a track's length, send a ranged `GET`.

## Before any route runs

**Ports.** HTTP uses `4533`, HTTPS uses `4534` (`Flower:HttpsPort`, `0`
turns it off). The TLS certificate is self-signed from the server's own device
key unless `CertificatePath`/`CertificateKeyPath` name a real one, so a paired
client validates it against the key it pinned rather than against an authority.

**Network allow-list** (`Program.cs`, `LanGuard`). Unless
`Flower:AllowPublicAccess` is on, a request whose source address is not
loopback, link-local, RFC1918, ULA, in `AllowedCidrs`, or (with
`TrustTailscaleRange`) in `100.64.0.0/10` has its connection **aborted** — no
status, no body. `X-Forwarded-For` is believed only from `Flower:TrustedProxies`.

**Body size.** 20 MB process-wide (Kestrel). On the device routes each
route states its own cap, checked against `Content-Length` before anything is
read: 20 MB for `/playlists/changes` and `/track-state`, 4 MB for `/log/report`,
256 KB for everything else. Over it is a `413`. The admin routes keep the
20 MB ceiling, and `pair-redeem` caps at 4 KB.

**Length.** A request with a body must state its `Content-Length`. One that
does not (chunked on HTTP/1.1, an open HTTP/2 stream) gets `411` on every
signed route. The signature covers the body only when its length is stated.

**Rate limits** (`RequestGate`). Authenticated requests count against the individual device's allowance for each request category. Anonymous or failed requests use address-based limits; successfully paired devices behind a shared proxy, tunnel, or CGNAT do not share the same budget:

| Budget | Keyed by | Routes | Limit |
|---|---|---|---|
| Bulk | device | `/api/flower/v1/*` not listed below | 60 / 60s |
| Art | device | `/cover-art`, `/cover-art/batch`, `/artist-image`, `/server-icon` | 600 / 60s |
| Media | device | `/stream`, `/download`, `/stream-tickets` | 240 / 60s |
| Admin | device | `/api/admin/*` not listed below | 120 / 60s |
| Mirror | device | `/api/admin/library/uploads*`, `/move`, `/tags`, `/artwork` | 3000 / 60s |
| Info | device | `/info`, signed | 240 / 60s |
| Vibe | device | `/vibe/preview`, `/vibe/endless`, `/vibe-playlists/{id}/extend` | 60 / 60s |
| Strangers | address | Any gated route, no fingerprint or none on file | 30 / 60s |
| Failures | address + device | Any route, a signature that failed | 10 / 60s |
| Anonymous info | address | `/info`, unsigned | 120 / 60s |
| Pairing | address | `pair-redeem` | 5 / 60s |
| Wrong pairing codes | the whole server | `pair-redeem`, a code that is not good | 20 / 10 min, then every outstanding code is cancelled |

Requests that exceed these limits receive `429 rate-limited` with
`Retry-After: 60`. A request a stream ticket admitted is charged to the media
budget of the device that minted the ticket.

Unknown devices are rejected before body buffering. Failed signatures are
counted by address and fingerprint, so failures from another address don't
spend the real device's budget. Each budget is separate: artwork requests,
for example, don't consume the playback allowance.

## Authentication: a signed request

Devices authenticate with P-256 signatures, without passwords or login
sessions. The server stores the device's public key at pairing. Browser clients
use a non-extractable WebCrypto key and the same signatures
(`BrowserPeerCredentials`). Browser audio is the exception: `/stream` and
`/download` can accept a temporary bearer ticket.

Source: `Flower.Core/Services/` — `SignedRequestCanonicalizer`,
`SignatureVerifier`, `PeerSignatureAuth`, `DeviceSigningKey`, `NonceReplayGuard`.

### Request headers

| Name | Value |
|---|---|
| `X-Flower-Fingerprint` | First 32 hex characters of the lowercase SHA-256 of the raw public key |
| `X-Flower-PublicKey` | Base64 of the 65-byte uncompressed point (`0x04 ‖ X ‖ Y`). Read only by `pair-redeem`, and by `/info` for a fingerprint it has no key for |
| `X-Flower-Alias` | The device's name, user-typed. Kept without control or bidirectional-override characters, cut to 64 characters (`DeviceAlias`) |
| `X-Flower-Timestamp` | Unix seconds |
| `X-Flower-Nonce` | Any string unique per request, at most 64 characters; clients send 16 random bytes as hex |
| `X-Flower-Signature` | Base64 ECDSA signature, SHA-256, IEEE P1363 form (`r ‖ s`, 64 bytes) |

Each may be sent as a header or as a query parameter of the same name; the
header wins when both are present. The query form exists for URLs handed to
something that cannot set headers. **Header values are percent-encoded**
(`Uri.EscapeDataString`) because an alias is user text and headers are ASCII;
query values are not encoded a second time.

### What is signed

Six fields joined with `\n`, UTF-8:

```
METHOD
/absolute/path
canonical-query
lowercase-hex-sha256(body)
timestamp
nonce
```

The canonical query is every query pair **except** those whose key starts with
`X-Flower-`, each key and value percent-encoded, sorted ordinally by encoded key
then encoded value, joined `key=value` with `&`. An empty body hashes as the
SHA-256 of zero bytes. For example, `GET /api/flower/v1/stream?id=abc`:

```
GET
/api/flower/v1/stream
id=abc
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
1790899200
9f2c4e1ab07d53c68e1f0a2b3c4d5e6f
```

Excluding the `X-Flower-*` parameters is what makes the header and query
transports sign identical bytes. The body hash stops a captured POST being
replayed with another body; the query stops a captured GET being replayed
against another id.

### What is checked

1. The timestamp is within **60 seconds** of the server's clock, either way.
2. The `(fingerprint, nonce)` pair has not been seen (remembered for 120s).
   The nonce is recorded *before* the signature is checked, so a failed attempt
   burns it too — sign afresh for every attempt, including each of a probe, a
   body GET and a reopen of the same stream.
3. The signature verifies against the key **on file** for that fingerprint —
   never one offered on the request. `pair-redeem` is the single exception.

### What the server signs back

Two kinds of answer can end a pairing, and the server signs both: every `/info`
response, and every `401 device-unknown` refusal (`ServerResponseSignature`).
It signs with its own device key, whose hash is the fingerprint a client pinned
at pairing, over the same canonical form requests use:

```
RESPONSE
<the request's path>
status=<status>
<sha256 of the response body>
<the server's timestamp>
<the request's own X-Flower-Nonce>
```

The answer carries `X-Flower-Server-Signature` and `X-Flower-Server-Timestamp`.
It is signed only when the request carried a nonce, which every signed request
does. The request's nonce is what makes it the answer to *that* request: a
captured "you are revoked" verifies against nothing a client sends afterwards.
That is also why the timestamp is not checked against a clock.

A client believes `trustsCaller: false` only when the answer is signed by the
key it names and that key hashes to the fingerprint it names. It believes
`device-unknown` only when the key is the one it holds for the server it paired
with. Anything unsigned, or signed by any other key, is one failed request: a
bare `403` from a proxy, a forged answer from whatever took the server's old
DHCP lease. The browser head reads `trustsCaller` without this check. It only
decides whether to show the pairing screen, and a tab's code comes from that
same origin anyway.

### Errors

Every refusal under `/api` is an RFC 9457 problem document,
`application/problem+json`, with one member of Flower's own: `code`
(`FlowerProblem.cs`).

```json
{ "type": "urn:flower:problem:clock-skew", "title": "The request's clock is too far from the server's.",
  "status": 401, "code": "clock-skew", "serverTime": "2026-10-02T12:00:00+00:00" }
```

The status says what to do next and the code says why. Statuses mean the same
on every surface: `401` is "authentication failed", `403` is "authenticated, and
not allowed".

| Status | `code` | Meaning | Client does |
|---|---|---|---|
| `400` | `invalid-request` | Malformed body or query; `detail` says what | Drop the request |
| `400` | `pairing-code-invalid` | Code wrong, expired or used | Ask for the code again |
| `401` | `device-unknown` | No key on file for the claimed fingerprint | The one refusal that may mean revoked |
| `401` | `signature-invalid` | Key on file, signature wrong | Sign again |
| `401` | `clock-skew` | Timestamp outside ±60s; carries `serverTime` | Correct the clock (`SignatureClock`) |
| `401` | `nonce-reused` | Nonce already seen | A bug: sign every attempt afresh |
| `403` | `not-admin` | Paired, and the route is an administrator's | Hide the control |
| `404` | `not-found` | No such track, upload, device or route | Per route |
| `409` | `conflict` | A different file is already at that path | Final for this file |
| `409` | `vibe-off` | Vibe Playlists are off on this server | Say so; offer nothing |
| `409` | `vibe-not-ready` | Nothing is analysed yet | Try again later |
| `409` | `endless-play-off` | Endless Play is off on this server | Let the queue end |
| `411` | `length-required` | A body with no `Content-Length` | Send it with one |
| `413` | `too-large` | Over the route's body cap | Drop the request |
| `422` | `corrupt` | Upload bytes do not match the promised hash | Send again |
| `429` | `rate-limited` | Over a budget; `Retry-After` is set | Wait that long |
| `503` | `scanning` | A library scan is running | Pause; resume when the token moves |
| `503` | `cannot-write` | The music folder is read-only, or there is none | Stop the batch; tell the owner |
| `503` | `unavailable` | Something else about the server, or a bare `503` | Stop the batch |
| `500` | `server-error` | Something on the server failed | Report it |

Clients act on the code. A sync service treats `device-unknown` alone as "this
server revoked me", and only when the server signed it (above). A stale
timestamp after a laptop slept is `clock-skew`, and must never unpair anything. Every `HttpClient` that `PeerHttpClient` builds
reads `serverTime` off a `clock-skew` refusal and signs with the corrected clock
from then on. The media path, which signs its own requests, retries once at
once. A route that left with a bare status is given a problem by its status in
a fallback (`Problems.FillEmptyRefusalAsync`).

## Discovery

`DiscoveryEndpoints.cs`, `SyncProtocol.cs`.

The server advertises `_flowersync._tcp` over mDNS. Use the identity handshake
to identify and authenticate the discovered server.

### `GET /api/localsend/v2/info`

Authentication is optional; the network allow-list and rate limits still apply.
Unsigned requests return public identity fields. Verified requests return
paired-only fields and consume the device's budget. Responses to requests
with a nonce are signed.

```json
{
  "alias": "lambda",
  "version": "2.0",
  "deviceModel": null,
  "deviceType": "server",
  "fingerprint": "3f9a…",
  "publicKey": "BK7x…",
  "download": false,
  "trustsCaller": true,
  "libraryToken": "…",
  "listeningToken": "…",
  "addresses": ["https://192.168.1.40:4534", "http://192.168.1.40:4533"],
  "callerIsAdmin": false,
  "playlistsToken": "…",
  "importsMusicAppPlays": false,
  "iconEmoji": "🌻",
  "iconToken": null
}
```

| Field | Notes |
|---|---|
| `alias` | The name actually announced, which differs from the configured one when another server already had it |
| `fingerprint`, `publicKey` | What a client pins, and checks against a pairing invite's `fp` |
| `trustsCaller` | `true` verified; `false` no key on file for the claimed fingerprint, told only to a caller whose request is self-signed by that fingerprint's key (a revoked device still holds it), and believed by a client only when the answer is signed; `null` otherwise: no identity claimed, a fingerprint claimed without its key, or a signature that merely failed |
| `libraryToken` | Change token for the catalog. Paired clients poll this route about every 5s and sync when it moves. A play does not move it |
| `listeningToken` | Change token for the catalog's listening: plays, when last played, resume positions. Moves on every play, where `libraryToken` does not. **Verified administrators only**, `null` otherwise. An owner's device that sees only this one move asks `GET /listening` for the songs that moved |
| `playlistsToken` | Change token for the caller's listener's playlists: it moves with every revision issued for one of them, and with a Vibe Playlist's songs, so a device comes back whenever `GET /playlists` would answer differently. Per listener, so a guest's edit does not move the owner's. **Verified callers only**, `null` otherwise |
| `addresses` | Every origin the server believes it is reachable on, https first, the public one last. **Verified callers only**, `null` otherwise |
| `callerIsAdmin` | **Verified callers only**, `null` otherwise. A hint for showing admin controls; it grants nothing |
| `importsMusicAppPlays` | **Verified administrators only**, `null` otherwise. Whether this server reads play counts from a Music.app library of its own; an admin device that brings its own Music.app plays leaves them alone while this is `true`, so they are not counted twice |
| `iconEmoji` | The emoji the server shows as beside `alias`, `null` for none. Told to anyone, as `alias` is |
| `iconToken` | Names the server's icon picture, `null` for none. **Verified callers only**, `null` otherwise — only they can fetch it, from `GET /server-icon`. A server has an emoji or a picture, never both |
| `vibePlaylists`, `vibePercent` | **Verified callers only**, `null` otherwise. Whether this server makes Vibe Playlists: `off`, `analysing` with `vibePercent` of the library heard so far, or `ready`. A paired device offers New Vibe Playlist… only when it is not `off` |
| `endlessPlay` | **Verified callers only**, `null` otherwise. Whether this server offers Endless Play; a device's own switch is greyed while it is `false` |

A verified call also carries the device's current `X-Flower-Alias`, and this is
where a rename lands in the server's device list. A signed call's answer is
signed back (see "What the server signs back"); a client unpairs on
`trustsCaller: false` only when it is.

## Pairing

`PairingEndpoints.cs`, `PairingCodeService.cs`, `PairingUri.cs`.

An admin issues a code; a new device redeems it. Codes are five characters from
`ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, live ten minutes, are single-use, and are
kept in memory only — a restart drops them. Case, spaces and dashes are ignored
on the way in. The first code is printed to standard output at startup
(`--pairing-code` forces one). It isn't written through the application logger;
container runtimes can still capture standard output in their logs.

A code reaches a device in one of three renderings:

- the bare code, typed by hand;
- an invite, `flower://pair?host=<origin>&host=<origin>…&code=<code>&fp=<server fingerprint>`,
  scanned or pasted. One `host` per address the server can be reached at, in
  the order to try them; the device keeps whichever answers as `fp`. `fp` is
  what lets the device check which server it is about to trust — a bare code
  cannot — and, before pairing, it is also the only thing that can vouch for the
  server's self-signed https certificate, so it is what makes pairing from
  outside the server's network possible at all;
- a browser link, `<origin>/#pair=<code>&page=settings`.

### `POST /api/flower/v1/pair-redeem`

Self-signed: the request carries `X-Flower-PublicKey`, the fingerprint must be
that key's hash, and the signature is checked against the offered key. Also
`X-Flower-PairingCode`, and `X-Flower-Alias` (defaults to the fingerprint). The
body is empty.

| Status | Meaning |
|---|---|
| `200` | `{"fingerprint": "…", "isAdmin": false}` — the device is trusted. `isAdmin` comes from the code as issued, never from the caller |
| `400` | `pairing-code-invalid`. The 20th wrong code in ten minutes, from any address, also cancels every outstanding code |
| `401` | `signature-invalid`: the proof of possession failed. The code is **not** consumed |
| `413` | `too-large`: body over 4 KB |
| `429` | `rate-limited`: more than five attempts a minute from this address |

## Device endpoints

Base path: `/api/flower/v1`.

`SyncEndpoints.cs`, `MediaEndpoints.cs`. Every route needs a paired device's
signature; `/stream` and `/download` alternatively take a stream ticket.
PascalCase JSON.

| Method | Path | Purpose |
|---|---|---|
| GET | `/library` | The whole catalog |
| GET | `/playlists` | The caller's listener's playlists |
| POST | `/playlists/changes` | A session's playlist edits, each over the revision it is based on |
| GET | `/listening?since=` | The plays that moved since a listening token (admin devices) |
| POST | `/plays` | Play events from a client without durable counters, such as a browser |
| POST | `/track-state` | Play totals, stars and options from a client with storage |
| POST | `/album-progress` | Exchange the Continue Playing shelf |
| POST | `/log/report` | Push this device's recent log lines |
| GET | `/log/watermark` | What the server already holds of them |
| GET | `/cover-art?id=[&size=thumbnail]` | One picture |
| POST | `/cover-art/batch` | Up to 32 pictures, at one size |
| GET | `/artist-image?artist=` | A picture of an artist, looked up online |
| GET | `/server-icon` | The server's icon picture, or `404` when it has none |
| GET | `/stream?id=` | A track's bytes, ranged |
| GET | `/download?id=` | The same bytes, as a named file |
| POST | `/vibe/preview` | The songs a Vibe Playlist's sentence would make |
| POST | `/vibe-playlists/{id}/extend` | More songs for a Vibe Playlist's endless queue |
| POST | `/vibe/endless` | Songs like the last few, for a queue that ran out (Endless Play) |

### `GET /library`

Returns `LibrarySyncManifestDto`:

```json
{ "DeviceFingerprint": "3f9a…", "Songs": [ { "Id": "…", "Title": "…" } ], "Removed": ["…"], "ListeningToken": "…" }
```

Each song's `PlayCounts` holds plays counted in Flower, one entry per device
plus the server's own; its `ImportedPlayCount` is the library's Music.app count
(see `POST /track-state`), served apart so the admin device that reported it
does not get it back as the server's plays. A song's total is the sum of both.
`SkipCounts` and `ImportedSkipCount` are the same two for skips (Next pressed
in a song's first 30 seconds, `Track.SkipCount`); `SkipCounts` is left out for
a song nobody has skipped.

That is the owner's view. A device that is not an admin gets the same songs
with only its own entry in `PlayCounts` and `SkipCounts`, and without
`LastPlayed`, `ResumePositionSeconds`, `ImportedPlayCount` or
`ImportedSkipCount`, so a guest learns nothing about
anyone else's listening. See `SECURITY.md`.

The response's `ETag` is the library change token — the same value `/info`
reports as `libraryToken`. Send it back as `If-None-Match` for a `304` with no
body. Large catalogs can be several megabytes. Responses are cached per catalog /
listening token pair and audience (owner or individual guest).

The `ETag` names the catalog, not the listening in it: a play does not move
it, so a `304` can stand in front of play counts that have moved since. An
owner's device follows those through `GET /listening`, starting from the
body's `ListeningToken` (the listening as of this body). A guest's body has no
`ListeningToken`, and needs none: the only listening in it is its own.

`Songs` holds only tracks the server has a file for. Each is a `TrackDto`
(`LibraryContracts.cs`), whose comments are the field-by-field reference:
identity (`Id`, `AlbumId`, `ArtistId`, `CoverArt`), tags, technical fields,
`RelativePath` below the library folder, per-device `PlayCounts` keyed by
fingerprint, `DateAdded`, `LastPlayed`, `Starred`, the per-track
playback options, and three edit dates — `TagsEditedAt` (with the full `Tags`
when set), `ArtEditedAt`, `FileReplacedAt` — that tell a device holding its own
copy that the server's is newer.

`Removed` lists the ids of songs taken out on purpose. A device drops its own
copy only for a song *named* here, never for one merely absent from `Songs` — a
music folder that failed to mount lists nothing at all.

### `GET /playlists` and `POST /playlists/changes`

`GET` answers `PlaylistSyncManifestDto`: `DeviceFingerprint`, `Playlists` and
optional `Deleted`. A playlist is `Id`, `Name`, `UpdatedAt`, `Tracks`, optional
`Rules` for a smart playlist, optional `VibePrompt` and `VibeInstrumental` for a
Vibe Playlist, `IsFolder`, `ParentId`, `SortKey`, and the server's `Revision`
with `History`, the revisions it held before (oldest first, at most 63). A
track entry is `Title`/`Artists`/`Album`/`DurationSeconds`, the catalog `Id`
when the sender has one, and `EntryId`, the row's own id, which stays with it
through moves and across devices. A row whose song the receiver does not have
is kept and sent back, never dropped.

Only the server issues revisions: `max(last issued + 1, its UTC
milliseconds)`, for every change to a playlist's content however it was made.
`UpdatedAt` is for display and decides nothing. `Deleted` lists the playlists
deleted for this listener, each `{Id, Revision, History}`; it is the only thing
that makes a missing playlist a deletion. A playlist that is simply absent, as
after restoring a backup, is pushed back.

`POST /playlists/changes` takes every change a session has, in one request:

```json
{"Changes": [
  {"Id": "…", "BaseRevision": 1763990000123, "Playlist": { … }},
  {"Id": "…", "BaseRevision": 1763990000456, "Delete": true}
]}
```

`BaseRevision` is the server's revision the change is based on, `0` for a
playlist the device has no version of. Each change succeeds or fails on its
own, and the answer is `200` with one entry per change:

| `Outcome` | Meaning |
|---|---|
| `Applied` | Written, at `Revision` |
| `Stale` | Not written: the server's revision is no longer `BaseRevision`. `Current` (a playlist) or `CurrentDeletion` is what it holds now, both absent when it holds nothing |
| `Refused` | The id is another listener's. Never applied |

Playlists belong to a **listener** (`Listeners`): every admin device shares
the owner's set, and each other device has a set of its own. `GET` returns the
caller's listener's playlists only, and a change only ever writes them. A
refused id is recorded as deleted for the caller's listener over the
`BaseRevision` it named, so the device's next sync drops its copy the ordinary
way rather than on the strength of the answer.

### `GET /listening?since=`

Admin devices only (`403 not-admin` for anyone else, since which songs somebody
just played is what a guest's catalog withholds). Returns
`ListeningChangesDto`:

```json
{ "Token": "…", "Songs": [ { "Id": "…", "PlayCounts": { "3f9a…": 12 }, "LastPlayed": "…", "ResumePositionSeconds": 754 } ] }
```

`since` is a listening token: a catalog's `ListeningToken`, `/info`'s
`listeningToken`, or an earlier answer's `Token`. `Songs` is every song whose
listening moved after it, and only those, with the catalog's spelling of the
same fields (`PlayCounts`, `ImportedPlayCount`, `SkipCounts`,
`ImportedSkipCount`, `LastPlayed`, `ResumePositionSeconds`) plus the three playback options, which a device
applies together with the resume position. `Token` is where the answer leaves
the device. Ask from it next time.

An unknown or expired `since` token, including one from before a server restart,
returns `409 conflict`. Fetch `GET /library` to establish a new baseline.

### `POST /plays`

`{"Plays": [{"EventId", "TrackId", "At", "Skipped"}]}` → `204`.

Events, not totals, because a browser tab has no durable counter to state a
total from. `EventId` makes a retry safe: the server drops one the same device
has already sent, remembering up to 5,000 per device for six hours. An event
is a play heard, sent once it has been: the play count and `LastPlayed` move
together. An event with `"Skipped": true` is a skip instead, and moves the skip
count alone.

Who signed decides where a play lands. An admin device's plays move the
library's own `PlayCount` and `LastPlayed`. Any other device's plays go under
its own fingerprint in `PlayCounts`, and move nothing else. Skips land the same
way: the library's `SkipCount`, or the device's entry in `SkipCounts`. A
report holds at most 500 events, with ids of at most 64 characters; past either
limit it is a `400`.

### `POST /track-state`

`{"Tracks": [TrackStateDto]}` → `204`, with the resulting tokens in the
`X-Flower-Library-Token` and `X-Flower-Listening-Token` response headers. A
report of plays alone moves only the listening token. A star, an option or a
`DateAdded` moves the library token.

Totals, not events: `Count` is this device's own play count for the track —
plays counted in Flower — filed under the fingerprint the signature proved and
merged by max, so re-sending converges. `SkipCount` is the same for skips.
Everything else in the record — `LastPlayedAt`, `Starred`, the playback
options, `DateAdded`, `ImportedCount`, `ImportedSkipCount` — is a single-valued field on the shared library, and is
applied **only from an admin device**. A non-admin sending them still gets
`204`; the fields are dropped in the merge. `DateAdded` only ever moves
backwards. `ImportedCount` is the song's play count as read from Music.app on
the reporting device; the library keeps one such count per song and only ever
raises it, to the highest any admin device reports, so two Macs sharing one
Music.app history count it once. `ImportedSkipCount` is Music.app's skip
count, by the same rule.

Store the returned tokens so the next poll doesn't fetch changes the client
just reported.

### `POST /album-progress`

`{"Albums": [{"AlbumId", "TrackId", "PositionSeconds", "UpdatedAt"}]}` in, the
merged shelf out in the same shape. Last write wins per album; a null `TrackId`
is a dated removal marker. Idempotent, so a retry after a lost response is safe.

Which shelf is decided by the signature, never the body: every admin device
shares the owner's, every other device has its own. `400` for more than 500
entries, an empty or over-long id, or a negative or non-finite position.

### `POST /log/report` and `GET /log/watermark`

Report: `{"DeviceFingerprint", "Alias", "CapturedAt", "Entries": [{"Timestamp",
"Level", "SourceContext", "Message", "Exception"}]}`. Stored under the
fingerprint the signature proved — the body's `DeviceFingerprint` is ignored —
and merged into a seven-day history the owner reads back through the admin API.
One report adds at most 5,000 lines, its oldest, and the answer's watermark
names the last one kept, so the client sends the rest next; clients send at
most 5,000 lines or about 2 MB per report. A device holds at most 128 MB, and
past that its oldest days go first.

Both routes answer `{"LastEntryTimestamp", "LastEventId"}`: the newest line the
server retains for the caller, so the next push starts there. Both null means
send everything.

### `GET /cover-art?id=[&size=thumbnail]`

`id` is an album id (`al-…`) or a track id. For an album, the first track's
file that carries art supplies it, in disc and track order. Answers the image
bytes with their own MIME type, or `404`.

`size=thumbnail` answers the same picture shrunk to 192 px wide, keeping its
aspect (`CoverArtBatch.ThumbnailPixels`): a JPEG, or a PNG if the cover has
transparency. Art no wider than that, or that the server cannot decode, comes
back as it is. Any other `size` is a `400`. The server makes each thumbnail
once and keeps it beside the cover in `CoverArtCache`.

Every picture carries an `ETag` (a hash of its bytes) and
`Cache-Control: private, no-cache`; a request with a matching `If-None-Match`
gets a `304` and no body. Only a browser has a cache to use this with: the
native clients keep their own disk cache instead.

A song's `CoverArt` in the catalog is the id to ask for: its album's, unless
the song carries a picture that is not its album's, and then the track id of
the album's first song carrying that same picture (`AlbumCover.CoverArtId`).
So songs of one album with different covers each show theirs, and one picture
is one id to fetch: an album whose songs share one, and the songs given another
together.

The server remembers where each id's art was found (`CoverArtCache`, shared
with the batch route), along with every file and folder it looked in to find
it. Those are checked before the answer is used again, so a cover replaced,
added or deleted on disk is noticed at the next request.

### `POST /cover-art/batch`

`{"Ids": ["al-…", …], "Size": "thumbnail"}`, at most 32 ids. `Size` is optional
and works as `size` does on `GET /cover-art`: leave it out for full-size art.
`400` for a malformed body, an empty list, too many ids or an unknown size. The response is `application/x-flower-cover-art-batch`, a
length-prefixed frame, little-endian (`CoverArtBatch.cs`):

```
int32 count
count × { int32 idBytes, id (UTF-8), int32 blobBytes, blob }
```

A zero-length blob means the server has no picture for that id. The response stops at 8 MB. An absent id didn't fit: request it again.
A present id with an empty blob has no artwork.

### `POST /vibe/preview` and `POST /vibe-playlists/{id}/extend`

Available to every paired device, on the separate Vibe request budget.
Implementation: `VibeEndpoints`.

- `/vibe/preview` takes `{"Prompt": "...", "Instrumental": false}` (a
  sentence of at most 500 characters, else `400`) and answers
  `{"Tracks": [...]}`: the first 50 songs of the pool that sentence would make,
  as playlist entries. It saves nothing.
- `/vibe-playlists/{id}/extend` takes `{"Exclude": [ids]}`, the songs the
  device already added to its queue beyond the pool, and answers the same
  shape: up to 50 more songs, the next ones down the same ranking. The server
  stores nothing; the next pool rebuild cuts the pool where it always does.
  The playlist must be the caller's listener's, else `404`.

Both answer `409` `vibe-off` while the owner has Vibe Playlists off, and `409`
`vibe-not-ready` before any song is analysed. A Vibe Playlist's pool itself
reaches a device with its playlists: the server ranks it from the sentence
and installs it as the playlist's songs without moving `UpdatedAt`, and
`playlistsToken` counts those songs so a device comes back for them. The
sentence is never logged above Debug.

### `POST /vibe/endless`

Endless Play (`VibeEndpoints.Endless`, `docs/VIBE-PLAYLIST-PLAN.md`, "Endless
Play"): a queue that has run out asks for songs like its last few. Takes
`{"Seeds": [ids], "Exclude": [ids]}`, by the server's ids, at most 20 seeds
(none is `400`), and answers `{"Tracks": [...]}`: up to 50 songs nearest the
seeds taken together, best first, none of them a seed or excluded. Seeds the
server never analysed count for nothing, and with none analysed the answer is
empty. Any paired device may ask, on the Vibe budget. `409`
`endless-play-off` while the owner has Endless Play off, `409`
`vibe-not-ready` before any song is analysed.

### `GET /artist-image?artist=`

Returns an artist picture looked up through Deezer by the server
(`OnlineMetadataEngine`). `artist` is
the name as the catalog's `artists` field has it, compared without case.

- The image bytes with their own MIME type: a 1000×1000 JPEG, as Deezer has it.
- `404` when there is no picture to be had: the library has no songs by that
  artist (checked before anything is asked of the internet, so the route is
  not a lookup service for any name), Deezer has no photo of them, none of
  Deezer's artists of that name has an album or song the library has by them.
  A client may remember this for a while; the
  app does for a day.
- `503 unavailable` for a temporary lookup failure, such as a connection
  failure or refusal from Deezer. Retry later.

Found pictures and misses are kept under the data directory
(`ArtistPictures/`), a miss for 30 days. Many devices asking for one artist at
once share a single lookup, and the server's own requests to Deezer are spaced
to stay inside its quota of fifty in five seconds.

### `GET /stream?id=` and `GET /download?id=`

The file from disk with range processing on, under the track's own content
type. `/download` adds a `Content-Disposition` naming the file. `404` when the
id is unknown or its file is gone.

Both send a strong `ETag` naming this version of the file (its length and
modification time, to the tick) and honour `If-Range` with it. A tag or cover
edit replaces the file, even one being streamed, and moves its audio when the
tag changes size, so a client reopening a song partway through sends the tag
it was given. It gets `206` with the range when the file is the same, and
`200` with the whole new file when it changed (`SeekableHttpStream` then finds
its place in the new file).

Admitted by a device signature, or by `ticket=<ticket>` in the query — tried
first, valid only on these two routes, and only for the `id` it was minted for.

The catalogued suffix describes a file on the server's disk; treat it as a
hint about the bytes, not a fact: the decoder reads what the file is, whatever
it is called.

### `POST /api/flower/v1/stream-tickets?id=`

`StreamTicketEndpoints.cs`, `StreamTicketService.cs`. Signed by any paired
device, empty body. An `<audio>` element cannot attach a signature to the
request it issues, so the tab signs this instead:

```json
{ "ticket": "64 hex chars", "expiresAt": "2026-10-02T12:15:00+00:00",
  "url": "/api/flower/v1/stream?id=…&ticket=…" }
```

The ticket is a bearer token for one track. It expires after the track's
length plus fifteen minutes, capped at twelve hours of track length; unknown
tracks get fifteen minutes. Tickets live in memory and are revoked with the
issuing device. They allow repeated requests for probing and seeking. A device
can hold 32; issuing another evicts the oldest. Because it rides in the query string, a ticket appears in full in any
log that records request URLs; the shipped configuration does not log them
(`Microsoft.*` at Warning), and raising that to Information does. `400` without an id or with one over 64 characters; an authentication
failure is the same `401` and code as anywhere else.

## Administrator endpoints

Base path: `/api/admin`.

`AdminEndpoints.cs`. A paired device's signature **and** `IsAdmin` on that
device. camelCase JSON; refusals are problem documents like everywhere else, with
the server's own sentence in `detail`. A paired device that is not an admin gets
`403 not-admin`. There is no login route.

### Devices and pairing

| Method | Path | Answers |
|---|---|---|
| POST | `/pairing-codes?grantsAdmin=false` | `{code, expiresAt, grantsAdmin, invite, browserUrl}` |
| GET | `/devices` | `[{fingerprint, alias, approvedAt, isAdmin, lastSeenAt, hasLog}]` |
| DELETE | `/devices/{fingerprint}` | `204`. Also revokes that device's stream tickets. `400` if it is the caller's own |
| GET | `/devices/{fingerprint}/logs?limit=500` | `{fingerprint, alias, receivedAt, entries}`, or `404` if the device has pushed nothing |
| GET | `/logs?limit=500&after=` | `{lastSequence, entries}` — the server's own log; pass the previous `lastSequence` as `after` for a delta |

The invite's hosts are the origin the request arrived on (left out when
`AdvertisedHost` is set, or when it is loopback), then every origin `/info`
reports to a paired device — the public one among them when public access is on. A revoked device learns of it from `trustsCaller: false` on its next
`/info` poll, or a `401 device-unknown` on its next sync.

### Settings

`GET /settings` returns `ServerSettingsDto`; `PUT /settings` takes
`ServerSettingsUpdateDto` and returns the settings as they now are
(`ServerAdminContracts.cs`).

Editable: `alias`, `iconEmoji`, `advertisedHost`, `advertiseOnLan`,
`trustTailscaleRange`, `allowedCidrs`, `libraryPaths`, `integrateWithITunes`,
`syncPlayCountFromITunes`, `syncDateAddedFromITunes`, `allowPublicAccess`,
`aiFeaturesEnabled`, `vibeEnabled`, `endlessPlayEnabled`, `vibeClipsPerSong`. Every field is optional and an absent one is left alone. Read-only alongside
them: `dataDirectory`, `version`, `addresses`, `publicAddress`, `publicOrigin`,
`publicReachability`, `fingerprint`, `advertisedAs`, `appleMusicFolder`,
`iTunesLibraryDescription`, `iconToken`, `pathPolicy`, and `overridden` — settings pinned by
the environment or command line, each with what sets it. A write to an
overridden setting is silently skipped.

`pathPolicy` describes the server’s filesystem comparisons: `defaultIgnoresCase`,
`roots` and `rootParents`, the latter two lists of `{root, ignoresCase}`. Remote
settings use `rootParents` when comparing folder names, including a mount point
whose containing filesystem differs from the mounted filesystem. Clients do not
probe server paths on their own filesystem.

`iconEmoji` keeps the first character a reader would count as one, an emoji of
several code points included; empty removes it. A non-empty one removes the
icon picture, which is set apart from the other settings:

| Method | Path | Body | Answers |
|---|---|---|---|
| PUT | `/server-icon` | image bytes, at most 512 KB | `204`, and clears `iconEmoji` |
| DELETE | `/server-icon` | — | `204` |

The picture is kept as `server-icon` in the data directory. Its type is sniffed
from the bytes, as for `/cover-art`; a client sends a square PNG of at most 256
pixels (`ServerIconPicture`).

Changes are written to `flower-server.json` in the data directory.
`allowPublicAccess` and `allowedCidrs` take effect on the next request. An
`allowedCidrs` entry that is not a network (`10.8.0.0/24`) is `400`, and
nothing is saved.
Changing an iTunes switch starts a rescan; changing `libraryPaths` does not —
call `/library/rescan`.

### AI features: Vibe Playlists and Endless Play

`aiFeaturesEnabled` is the master switch over every AI feature (off by
default), and under it each feature's own: `vibeEnabled` (on by default) and
`endlessPlayEnabled` (off by default). A feature runs while its switch and the
master are on, and the songs are listened to while either feature runs. The
switches and `vibeClipsPerSong` take effect in the same request
(`SemanticAudioService`): listening starts with the model download, then the
analysis; the last feature off stops the job and deletes the model and every
analysis; a new clip setting rebuilds the index. `vibeClipsPerSong` is one of `3`, `6`, `12` or `0` (the
whole song), and anything else is `400`. See `docs/VIBE-PLAYLIST-PLAN.md`.

| Method | Path | Answers |
|---|---|---|
| GET | `/vibe/status` | `VibeStatusDto` |
| POST | `/vibe/retry` | `VibeStatusDto`, after starting again from an error |
| POST | `/vibe/pause` | `VibeStatusDto`, after pausing the listening |
| POST | `/vibe/resume` | `VibeStatusDto`, after resuming it |

A pause stops the analysis, and a model download still under way, at once;
searches go on with what was heard. It is written to `flower-server.json` as
`SemanticAudioPaused`, so the server stays paused across a restart, and is
`409` `conflict` when the environment or command line sets that.

`VibeStatusDto` (`ServerAdminContracts.cs`): `state` (`Off`, `Downloading`,
`Preparing`, `Analysing`, `UpToDate`, `WaitingForScan`, `Paused`, `Failed`), `download`
(`{bytesDone, bytesTotal}` while downloading), `songsDone`, `songsEligible`,
`songsSearchable`, `currentTitle` and `currentArtist` while analysing,
`secondsLeft`, `error`, `modelBytes`, `analysisBytes`, `costs` (each clip
setting's `{clipsPerSong, clips, bytes, seconds}` for this library, `seconds`
null until the server has measured itself), `failedCount`, and `failures`, the
first 200 songs that couldn't be analysed as `{title, artist, reason}`.
`WaitingForScan` means a library scan is running and the analysis waits it
out; `Paused` means the owner paused it.

### The library

| Method | Path | Body | Answers |
|---|---|---|---|
| GET | `/library` | — | `{rescanning, trackCount, lastCompletedAt, lastError, unwritableFolders, songsScanned, songsToScan}` |
| POST | `/library/rescan` | — | The same, immediately; poll `GET /library` for the end |
| POST | `/library/remove` | `{trackIds, deleteFiles}` | `{removed, filesTrashed, filesDeleted, filesNotDeleted}` |
| GET | `/library/removed` | — | `[{path, removedAt, stillOnDisk}]` |
| POST | `/library/removed/restore` | `{paths}` | `{restored}`, and starts a rescan |
| POST | `/library/removed/delete` | `{paths}` | `{deleted, notDeleted}` |
| PUT | `/cover-art?id=` | image bytes | `{written, total}` |
| DELETE | `/cover-art?id=` | — | `{written, total}` |

`songsScanned` of `songsToScan` is how far the scan in flight has got: song
files read, of all it found. Both are left out while no scan runs and while one
is still listing the folders. An administering device polls them into its status
bar after a rescan it started.

`unwritableFolders` is the library folders the last scan could not create a
file in. Read-only mounts are supported, but uploads and file edits require write access.

Removal takes a song out of the library and, without `deleteFiles`, leaves the
file on disk and excluded from scans. `/removed/delete` is the only route that
deletes such a file afterwards, and only paths already on the removed list are
touched: no route here accepts an arbitrary path to delete.

`/cover-art` writes into every file the matching read would have consulted, so
what you can see at an id and what you replace at it cannot come apart. The
image type is sniffed from the bytes; the `Content-Type` header is not trusted.
`404` for an unknown id, `500` if no file could be written, and a partial
success is a `200` with `written < total`.

`&to=folder` on either puts the cover in the album's folder instead, as
`cover.<type>` in place of any `cover.*` or `folder.*` there, and takes the
pictures out of the songs' files, which would otherwise win over it; a removal
there takes out both. A folder that also holds another album's songs gets no
cover file, and its songs get the picture in their files as without it.
`total` then counts every song in the folder, since the cover is theirs too.

### Mirroring — an admin device's files, sent to the server

`LibraryIngest.cs`, `TrackTags.cs`, `TrackArtwork.cs`; the client half is
`LibraryMirrorService`. `SYNC-PLAN.md`, "Ingest", is the design.

All of these answer `503` while a library scan is running, and a client waits
for the library token to move before resuming.

**Upload.** Send the file in chunks. Each chunk is buffered and its signature
verified before writing.

1. `POST /library/uploads` with
   `{relativePath, length, sha256, dateAdded, replacesTrackId?}` — the path
   below the device's library folder, `/`-separated, and the hash and length of
   the whole file. Answers `{uploadId, offset}`.
2. `PUT /library/uploads/{uploadId}?offset=<n>` with the next piece as the raw
   body and a `Content-Length`. At most 8 MB a piece; clients send 4 MB.
   Answers `{uploadId, offset}` while more is wanted.
3. The piece that completes the file answers with `uploadId` absent and
   `{trackId, dateAdded, libraryToken, tagsEditedAt?, artEditedAt?, fileReplacedAt?}`.

Use the server's returned `offset` for the next chunk. Sending another offset
returns the expected offset without writing, allowing interrupted uploads to resume. Pieces are
staged on disk under the file's hash for seven days, so a restarted server
picks an upload back up after a fresh step 1.

The file lands at the same relative path under the server's first library
folder. With `replacesTrackId` it replaces that song's file wherever it is, and
the song keeps its id.

| Status | Meaning | What to do |
|---|---|---|
| `400 invalid-request` | Bad path, unsupported type, piece that does not fit, not audio, no `offset` | Final for this file |
| `404 not-found` | No such upload in progress | Begin again from step 1 |
| `409 conflict` | A different file already sits at that path | Final; nothing is overwritten |
| `422 corrupt` | All bytes arrived and the hash does not match | Send it again |
| `503 scanning` | A library scan is running | Pause; resume when the token moves |
| `503 cannot-write` | The music folder is read-only, or there is none | Stop the whole batch, and tell the owner — a proxy in front of a server that is down also answers `503`, so only this code means the folder needs fixing |

**Move.** `POST /library/move` with `{trackId, relativePath}` →
`{relativePath, libraryToken}`. Sent only for a recorded move, never inferred
from comparing layouts. Same refusals as an upload.

**Tags.** `POST /library/tags` with
`{edits: [{trackId, editedAt, tags, fields}]}` → `{applied, notWritten, libraryToken}`.
`tags` is a `TrackTagsDto` and `fields` the `TrackTagFieldSet` flags naming which
of its values were edited. Only those are applied; the rest of the file's tags
stay as they are. The catalog's `tagFields` names every tag edited on a song, and
a device with its own copy of the file takes only those.
`notWritten` lists the ids whose file could not be written and are worth
sending again; every other edit is settled, including one the server already
holds something newer for.

**Artwork.** `PUT /library/artwork?ids=a,b,c&editedAt=<ISO 8601>` with the image
as the body, or `DELETE` with the same query for no picture at all →
`{applied, notWritten, libraryToken}`. The query is covered by the signature
like a body is.
`&to=folder` keeps it in the album's folder rather than the files, as for
`/cover-art` above: the device sends it wherever it keeps the cover itself.

Store each returned `libraryToken` so the client doesn't fetch its own write
as a new remote change.

## Change tokens

Clients use separate tokens to fetch only the data that changed:

- `libraryToken` — on `/info`, as `ETag` on `GET /library`, and echoed by every
  write that moves it.
- `playlistsToken` — on `/info`. It moves with a Vibe Playlist's songs as
  well as with every edit, since the server rebuilds a pool without one.
- `listeningToken` — administrator-only, on `/info`; use `GET /listening?since=`
  for play counts, last-played dates and resume positions that changed.
- A log watermark — per device, from the two `/log` routes.

A paired client polls `/info`, compares tokens, and opens a sync session only
when one has moved. A session can include a conditional `GET /library`, a listening delta,
playlist changes, a `/track-state` report, an `/album-progress` exchange and
a `/log/report`, according to what changed. A client that is refused with `429` backs off for a full
window rather than retrying into it.

## Where to read next

| Question | File |
|---|---|
| A route's exact behaviour | `Flower.Server/Endpoints/*.cs` |
| A wire shape | `Flower.Core/Services/*Contracts.cs`, `LibraryContracts.cs`, `TrackTags.cs` |
| Signing, from the calling side | `Flower/Services/SignedDeviceCredentials.cs`, `BrowserPeerCredentials.cs`, `Flower.Core/Services/PeerCredentialsHandler.cs` |
| The routes exercised end to end | `Tests/Flower.Server.Tests/` |
| Access controls and trust model | [Security](SECURITY.md) |
| Design history | [Development notes](README.md#technical-reference) |
