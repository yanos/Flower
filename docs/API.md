# The Flower API

What `Flower.Server` answers over HTTP, and what a caller has to send to be
answered. A reference, written from the code as it stood in October 2026 —
`Flower.Server/Endpoints/` is the authority, and each section names the file it
was read from. `SYNC-PLAN.md` holds the reasoning behind most of it;
`SELF-HOSTING.md` is the operator's side.

There is one protocol and one server. A client never serves, there is no
OpenSubsonic surface any more, and nothing here is versioned for compatibility:
the `v1` in the paths is a name, not a promise (see CLAUDE.md, "No Users Yet").

## The surface at a glance

| Prefix | What it is | Who may call | JSON casing |
|---|---|---|---|
| `GET /api/localsend/v2/info` | Identity handshake | Anyone the network gate admits | camelCase |
| `POST /api/flower/v1/pair-redeem` | Turn a pairing code into a trusted device | Anyone holding a code | camelCase |
| `/api/flower/v1/*` | Catalog, playlists, state, art, audio | Any paired device | **PascalCase** |
| `POST /api/flower/v1/stream-tickets` | Mint a URL an `<audio>` element can open | Any paired device | camelCase |
| `/api/admin/*` | Devices, settings, library writes, logs | Paired devices flagged admin | camelCase |

The casing split is real and easy to trip over. The sync group writes whatever
the client's source-generated context writes — no naming policy, so `Songs`,
`DeviceFingerprint`, `Ids` — and reads case-insensitively. Everything else is
camelCase. Nulls are omitted everywhere except `/info`, which writes them — a
`null` there is an answer (`trustsCaller`), not an absence.

Anything else under `/api` is a `404` (`not-found`) from the single-page
fallback (`WebUiHosting`), which matches every method. That includes `HEAD` on the media
routes: they are `MapGet` on purpose, so a client finds a track's length with a
ranged GET rather than a HEAD.

## Before any route runs

**Listeners.** Plain HTTP on `4533`, TLS on `4534` (`Flower:HttpsPort`, `0`
turns it off). The TLS certificate is self-signed from the server's own device
key unless `CertificatePath`/`CertificateKeyPath` name a real one, so a paired
client validates it against the key it pinned rather than against an authority.

**The network gate** (`Program.cs`, `LanGuard`). Unless
`Flower:AllowPublicAccess` is on, a request whose source address is not
loopback, link-local, RFC1918, ULA, in `AllowedCidrs`, or (with
`TrustTailscaleRange`) in `100.64.0.0/10` has its connection **aborted** — no
status, no body. A refusal is still a reply, and a reply confirms something is
listening. `X-Forwarded-For` is believed only from `Flower:TrustedProxies`.

**Body size.** 20 MB process-wide (Kestrel). On the device routes each
route states its own cap, checked against `Content-Length` before anything is
read: 20 MB for `/playlists/apply` and `/track-state`, 4 MB for `/log/report`,
256 KB for everything else. Over it is a `413`. The admin routes keep the
20 MB ceiling, and `pair-redeem` caps at 4 KB.

**Length.** A request with a body must state its `Content-Length`. One that
does not (chunked on HTTP/1.1, an open HTTP/2 stream) gets `411` on every
signed route. The signature covers the body only when its length is stated.

**Rate limits** (`RequestGate`). A request that has verified is charged to its
own device's budget for its plane. Only failing callers are counted by address,
so devices behind one proxy, tunnel or CGNAT do not share a budget:

| Budget | Keyed by | Routes | Limit |
|---|---|---|---|
| Bulk | device | `/api/flower/v1/*` not listed below | 60 / 60s |
| Art | device | `/cover-art`, `/cover-art/batch` | 600 / 60s |
| Media | device | `/stream`, `/download`, `/stream-tickets` | 240 / 60s |
| Admin | device | `/api/admin/*` not listed below | 120 / 60s |
| Mirror | device | `/api/admin/library/uploads*`, `/move`, `/tags`, `/artwork` | 3000 / 60s |
| Info | device | `/info`, signed | 240 / 60s |
| Strangers | address | Any gated route, no fingerprint or none on file | 30 / 60s |
| Failures | address + device | Any route, a signature that failed | 10 / 60s |
| Anonymous info | address | `/info`, unsigned | 120 / 60s |
| Pairing | address | `pair-redeem` | 5 / 60s |

Every refusal is `429` with `Retry-After: 60`. A request a stream ticket
admitted is charged to the media budget of the device that minted the ticket.

The order matters. A stranger is refused before its body is read, since that
costs a lookup rather than a buffer and an ECDSA verify. A known device whose
signature fails is counted per address *and* fingerprint, so someone who knows
a device's fingerprint cannot lock that device out by failing on its behalf
from elsewhere. The planes are separate so that spending one cannot spend
another — a cover-art burst sharing a budget with audio is how an album once
stopped playing (`CITED-DECISIONS.md` #2b).

## Authentication: a signed request

There are no passwords, sessions or bearer tokens. A device holds a P-256
keypair and signs every request; the server holds the public key it captured
when the device paired. A browser tab is not an exception — it holds a
non-extractable WebCrypto key and signs the same way (`BrowserPeerCredentials`).

Source: `Flower.Core/Services/` — `SignedRequestCanonicalizer`,
`SignatureVerifier`, `PeerSignatureAuth`, `DeviceSigningKey`, `NonceReplayGuard`.

### What travels

| Name | Value |
|---|---|
| `X-Flower-Fingerprint` | First 32 hex characters of the lowercase SHA-256 of the raw public key |
| `X-Flower-PublicKey` | Base64 of the 65-byte uncompressed point (`0x04 ‖ X ‖ Y`). Read only by `pair-redeem` |
| `X-Flower-Alias` | The device's name, user-typed |
| `X-Flower-Timestamp` | Unix seconds |
| `X-Flower-Nonce` | Any string unique per request; clients send 16 random bytes as hex |
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

### How a refusal reads

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
| `411` | `length-required` | A body with no `Content-Length` | Send it with one |
| `413` | `too-large` | Over the route's body cap | Drop the request |
| `422` | `corrupt` | Upload bytes do not match the promised hash | Send again |
| `429` | `rate-limited` | Over a budget; `Retry-After` is set | Wait that long |
| `503` | `scanning` | A library scan is running | Pause; resume when the token moves |
| `503` | `unavailable` | Nowhere to write, or similar | Stop the batch |
| `500` | `server-error` | Something on the server failed | Report it |

Clients act on the code. A sync service treats `device-unknown` alone as "this
server revoked me". A stale timestamp after a laptop slept is `clock-skew`, and
must never unpair anything. Every `HttpClient` that `PeerHttpClient` builds
reads `serverTime` off a `clock-skew` refusal and signs with the corrected clock
from then on. The media path, which signs its own requests, retries once at
once. A route that left with a bare status is given a problem by its status in
a fallback (`Problems.FillEmptyRefusalAsync`). A test walks the routing table to
hold every route to this.

## Discovery

`DiscoveryEndpoints.cs`, `SyncProtocol.cs`.

The server advertises `_flowersync._tcp` over mDNS. That says only that
something is listening; the handshake says what.

### `GET /api/localsend/v2/info`

Ungated, and optionally signed. Unsigned it answers a stranger with the
server's identity; signed by a paired device it answers more.

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
  "addresses": ["https://192.168.1.40:4534", "http://192.168.1.40:4533"],
  "callerIsAdmin": false,
  "playlistsToken": "…"
}
```

| Field | Notes |
|---|---|
| `alias` | The name actually announced, which differs from the configured one when another server already had it |
| `fingerprint`, `publicKey` | What a client pins, and checks against a pairing invite's `fp` |
| `trustsCaller` | `true` verified; `false` no key on file for the claimed fingerprint; `null` no identity claimed, or a signature that merely failed |
| `libraryToken`, `playlistsToken` | Change tokens. Paired clients poll this route about every 5s and sync when one moves |
| `addresses` | Every origin the server believes it is reachable on, https first, the public one last. **Verified callers only**, `null` otherwise |
| `callerIsAdmin` | **Verified callers only**, `null` otherwise. A hint for showing admin controls; it grants nothing |

A verified call also carries the device's current `X-Flower-Alias`, and this is
where a rename lands in the server's device list.

## Pairing

`PairingEndpoints.cs`, `PairingCodeService.cs`, `PairingUri.cs`.

An admin issues a code; a new device redeems it. Codes are five characters from
`ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, live ten minutes, are single-use, and are
kept in memory only — a restart drops them. Case, spaces and dashes are ignored
on the way in. The first code is printed to the console at startup
(`--pairing-code` forces one), never logged.

A code reaches a device in one of three renderings:

- the bare code, typed by hand;
- an invite, `flower://pair?host=<host>&code=<code>&fp=<server fingerprint>`,
  scanned or pasted. `fp` is what lets the device check which server it is
  about to trust — a bare code cannot;
- a browser link, `<origin>/#pair=<code>&page=settings`.

### `POST /api/flower/v1/pair-redeem`

Self-signed: the request carries `X-Flower-PublicKey`, the fingerprint must be
that key's hash, and the signature is checked against the offered key. Also
`X-Flower-PairingCode`, and `X-Flower-Alias` (defaults to the fingerprint). The
body is empty.

| Status | Meaning |
|---|---|
| `200` | `{"fingerprint": "…", "isAdmin": false}` — the device is trusted. `isAdmin` comes from the code as issued, never from the caller |
| `400` | `pairing-code-invalid` |
| `401` | `signature-invalid`: the proof of possession failed. The code is **not** consumed |
| `413` | `too-large`: body over 4 KB |
| `429` | `rate-limited`: more than five attempts a minute from this address |

## The device surface — `/api/flower/v1`

`SyncEndpoints.cs`, `MediaEndpoints.cs`. Every route needs a paired device's
signature; `/stream` and `/download` alternatively take a stream ticket.
PascalCase JSON.

| Method | Path | Purpose |
|---|---|---|
| GET | `/library` | The whole catalog |
| GET | `/playlists` | The server's playlists |
| POST | `/playlists/apply` | A merged playlist set, pushed back |
| POST | `/plays` | Play events from a head with no storage (a browser tab) |
| POST | `/track-state` | Plays, stars and options from a head with storage |
| POST | `/album-progress` | Exchange the Continue Playing shelf |
| POST | `/log/report` | Push this device's recent log lines |
| GET | `/log/watermark` | What the server already holds of them |
| GET | `/cover-art?id=` | One picture |
| POST | `/cover-art/batch` | Up to 32 pictures |
| GET | `/stream?id=` | A track's bytes, ranged |
| GET | `/download?id=` | The same bytes, as a named file |

### `GET /library`

Returns `LibrarySyncManifestDto`:

```json
{ "DeviceFingerprint": "3f9a…", "Songs": [ { "Id": "…", "Title": "…" } ], "Removed": ["…"] }
```

The response's `ETag` is the library change token — the same value `/info`
reports as `libraryToken`. Send it back as `If-None-Match` for a `304` with no
body. At a real library size this is megabytes; the serialized body is cached
per token.

`Songs` holds only tracks the server has a file for. Each is a `TrackDto`
(`LibraryContracts.cs`), whose comments are the field-by-field reference:
identity (`Id`, `AlbumId`, `ArtistId`, `CoverArt`), tags, technical fields,
`RelativePath` below the library folder, per-device `PlayCounts` keyed by
fingerprint, `DateAdded`, `LastPlayed`, `Starred`/`StarredAt`, the per-track
playback options, and three edit dates — `TagsEditedAt` (with the full `Tags`
when set), `ArtEditedAt`, `FileReplacedAt` — that tell a device holding its own
copy that the server's is newer.

`Removed` lists the ids of songs taken out on purpose. A device drops its own
copy only for a song *named* here, never for one merely absent from `Songs` — a
music folder that failed to mount lists nothing at all.

### `GET /playlists` and `POST /playlists/apply`

Both carry `PlaylistSyncManifestDto`: `DeviceFingerprint`, `Playlists`
(`Id`, `Name`, `UpdatedAt`, `Tracks`, optional `Rules` for a smart playlist),
and optional `Deleted` ids. A track entry is `Title`/`Artists`/`Album`/
`DurationSeconds` plus the catalog `Id` when the sender has one.

Playlists belong to a **listener** (`Listeners`): every admin device shares
the owner's set, and each other device has a set of its own. `GET` returns the
caller's listener's playlists only, and a push only ever writes them.

The caller pulls, merges locally, and pushes the result; the server runs no
conflict resolution of its own. It does not take the push wholesale either: a
playlist missing from it is kept unless `Deleted` names it, and a pushed copy
no newer than the one held is ignored. A pushed or deleted id that belongs to
another listener is not applied. The answer is `200` with
`{"Refused": [ids]}` naming those ids; it is empty for an honest push. The
client only logs it: its next sync drops those playlists the ordinary way.

### `POST /plays`

`{"Plays": [{"EventId", "TrackId", "At", "Started", "Completed"}]}` → `204`.

Events, not totals, because a browser tab has no durable counter to state a
total from. `EventId` makes a retry safe: the server drops one the same device
has already sent, remembering up to 5,000 per device for six hours. `Started`
and `Completed` are separate so a skipped track reaches History without
inflating its play count.

Who signed decides where a play lands. An admin device's plays move the
library's own `PlayCount` and `LastPlayed`. Any other device's finished plays
go under its own fingerprint in `PlayCounts`, and its starts move nothing. A
report holds at most 500 events, with ids of at most 64 characters; past either
limit it is a `400`.

### `POST /track-state`

`{"Tracks": [TrackStateDto]}` → `204`, with the resulting library token in the
`X-Flower-Library-Token` response header.

Totals, not events: `Count` is this device's own play count for the track,
filed under the fingerprint the signature proved and merged by max, so
re-sending converges. Everything else in the record — `LastPlayedAt`,
`Starred`, `StarredAt`, the playback options, `DateAdded` — is a single-valued
field on the shared library, and is applied **only from an admin device**. A
non-admin sending them still gets `204`; the fields are dropped in the merge.
`DateAdded` only ever moves backwards.

The response header exists to break a loop: reporting a play moves the library
token, and a client that did not recognise its own echo would refetch the whole
catalog for every play.

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

### `GET /cover-art?id=`

`id` is an album id (`al-…`) or a track id. For an album, the first track's
file that carries art supplies it. Answers the image bytes with their own MIME
type, or `404`.

### `POST /cover-art/batch`

`{"Ids": ["al-…", …]}`, at most 32. `400` for a malformed body, an empty list or
too many ids. The response is `application/x-flower-cover-art-batch`, a
length-prefixed frame, little-endian (`CoverArtBatch.cs`):

```
int32 count
count × { int32 idBytes, id (UTF-8), int32 blobBytes, blob }
```

A zero-length blob means the server has no picture for that id. The response
stops at 8 MB: ids that did not fit are simply absent, and the caller asks
again for those. The two cases are distinguishable only because a found-nothing
id is still present.

### `GET /stream?id=` and `GET /download?id=`

The file from disk with range processing on, under the track's own content
type. `/download` adds a `Content-Disposition` naming the file. `404` when the
id is unknown or its file is gone.

Admitted by a device signature, or by `ticket=<ticket>` in the query — tried
first, valid only on these two routes, and only for the `id` it was minted for.

The catalogued suffix describes a file on the server's disk; treat it as a
hint about the bytes, not a fact (CLAUDE.md, "Device Checks").

### `POST /api/flower/v1/stream-tickets?id=`

`StreamTicketEndpoints.cs`, `StreamTicketService.cs`. Signed by any paired
device, empty body. An `<audio>` element cannot attach a signature to the
request it issues, so the tab signs this instead:

```json
{ "ticket": "64 hex chars", "expiresAt": "2026-10-02T12:15:00+00:00",
  "url": "/api/flower/v1/stream?id=…&ticket=…" }
```

A ticket is a bearer token by necessity, so it is narrow: one track id, fifteen
minutes, in memory only, and revoked with the device that minted it. It is
deliberately **not** single-use — a media element makes a probe and then a
range request per seek. A device holds at most 32; minting another drops its
oldest. `400` without an id or with one over 64 characters; an authentication
failure is the same `401` and code as anywhere else.

## The admin surface — `/api/admin`

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

The invite's host is the one the request arrived on, unless `AdvertisedHost` is
set. A revoked device learns of it from `trustsCaller: false` on its next
`/info` poll, or a `401 device-unknown` on its next sync.

### Settings

`GET /settings` returns `ServerSettingsDto`; `PUT /settings` takes
`ServerSettingsUpdateDto` and returns the settings as they now are
(`ServerAdminContracts.cs`).

Editable: `alias`, `advertisedHost`, `advertiseOnLan`, `trustTailscaleRange`,
`allowedCidrs`, `libraryPaths`, `integrateWithITunes`,
`syncPlayCountFromITunes`, `syncDateAddedFromITunes`, `allowPublicAccess`.
Every field is optional and an absent one is left alone. Read-only alongside
them: `dataDirectory`, `version`, `addresses`, `publicAddress`, `publicOrigin`,
`publicReachability`, `fingerprint`, `advertisedAs`, `appleMusicFolder`,
`iTunesLibraryDescription`, and `overridden` — settings pinned by the
environment or command line, each with what sets it. A write to an overridden
setting is silently skipped.

Changes are written to `flower-server.json` in the data directory.
`allowPublicAccess` and `allowedCidrs` take effect on the next request.
Changing an iTunes switch starts a rescan; changing `libraryPaths` does not —
call `/library/rescan`.

### The library

| Method | Path | Body | Answers |
|---|---|---|---|
| GET | `/library` | — | `{rescanning, trackCount, lastCompletedAt, lastError, unwritableFolders}` |
| POST | `/library/rescan` | — | The same, immediately; poll `GET /library` for the end |
| POST | `/library/remove` | `{trackIds, deleteFiles}` | `{removed, filesTrashed, filesDeleted, filesNotDeleted}` |
| GET | `/library/removed` | — | `[{path, removedAt, stillOnDisk}]` |
| POST | `/library/removed/restore` | `{paths}` | `{restored}`, and starts a rescan |
| POST | `/library/removed/delete` | `{paths}` | `{deleted, notDeleted}` |
| PUT | `/cover-art?id=` | image bytes | `{written, total}` |
| DELETE | `/cover-art?id=` | — | `{written, total}` |

`unwritableFolders` is the library folders the last scan could not create a
file in. It is a statement rather than an error — a read-only mount is a
legitimate deployment — and what it warns of is the upload and tag routes below
being refused.

Removal takes a song out of the library and, without `deleteFiles`, leaves the
file on disk and excluded from scans. `/removed/delete` is the only route that
deletes such a file afterwards, and only paths already on the removed list are
touched: no route here accepts an arbitrary path to delete.

`/cover-art` writes into every file the matching read would have consulted, so
what you can see at an id and what you replace at it cannot come apart. The
image type is sniffed from the bytes; the `Content-Type` header is not trusted.
`404` for an unknown id, `500` if no file could be written, and a partial
success is a `200` with `written < total`.

### Mirroring — an admin device's files, sent to the server

`LibraryIngest.cs`, `TrackTags.cs`, `TrackArtwork.cs`; the client half is
`LibraryMirrorService`. `SYNC-PLAN.md`, "Ingest", is the design.

All of these answer `503` while a library scan is running, and a client waits
for the library token to move before resuming.

**Upload.** A file goes up in pieces, because a signed body is buffered whole
before its signature is checked.

1. `POST /library/uploads` with
   `{relativePath, length, sha256, dateAdded, replacesTrackId?}` — the path
   below the device's library folder, `/`-separated, and the hash and length of
   the whole file. Answers `{uploadId, offset}`.
2. `PUT /library/uploads/{uploadId}?offset=<n>` with the next piece as the raw
   body and a `Content-Length`. At most 8 MB a piece; clients send 4 MB.
   Answers `{uploadId, offset}` while more is wanted.
3. The piece that completes the file answers with `uploadId` absent and
   `{trackId, dateAdded, libraryToken, tagsEditedAt?, artEditedAt?, fileReplacedAt?}`.

`offset` in every answer is where the *server* got to. A piece sent at any
other offset is not an error: it is answered with the server's offset and
nothing is written, which is how a dropped connection resumes. Pieces are
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
| `503 unavailable` | Nowhere writable | Stop the whole batch |

**Move.** `POST /library/move` with `{trackId, relativePath}` → 
`{relativePath, libraryToken}`. Sent only for a recorded move, never inferred
from comparing layouts. Same refusals as an upload.

**Tags.** `POST /library/tags` with
`{edits: [{trackId, editedAt, tags}]}` → `{applied, notWritten, libraryToken}`.
`tags` is a whole `TrackTagsDto` — an edit replaces the song's tags as a set.
`notWritten` lists the ids whose file could not be written and are worth
sending again; every other edit is settled, including one the server already
holds something newer for.

**Artwork.** `PUT /library/artwork?ids=a,b,c&editedAt=<ISO 8601>` with the image
as the body, or `DELETE` with the same query for no picture at all →
`{applied, notWritten, libraryToken}`. The query is covered by the signature
like a body is.

Every `libraryToken` handed back is the token the caller's own change produced,
for the reason `/track-state` returns one: so a device does not mistake its own
write for news.

## Change tokens

Three opaque strings drive when a client talks to the server at all:

- `libraryToken` — on `/info`, as `ETag` on `GET /library`, and echoed by every
  write that moves it.
- `playlistsToken` — on `/info`.
- A log watermark — per device, from the two `/log` routes.

A paired client polls `/info`, compares tokens, and opens a sync session only
when one has moved. A session is roughly: `GET /library` (conditional), a
playlist pull and push, a `/track-state` report, an `/album-progress` exchange,
a `/log/report`. A client that is refused with `429` backs off for a full
window rather than retrying into it.

## Where to read next

| Question | File |
|---|---|
| A route's exact behaviour | `Flower.Server/Endpoints/*.cs` |
| A wire shape | `Flower.Core/Services/*Contracts.cs`, `LibraryContracts.cs`, `TrackTags.cs` |
| Signing, from the calling side | `Flower/Services/SignedDeviceCredentials.cs`, `BrowserPeerCredentials.cs`, `Flower.Core/Services/PeerCredentialsHandler.cs` |
| The routes exercised end to end | `Tests/Flower.Server.Tests/` |
| Why it is shaped this way | `docs/SYNC-PLAN.md`, `docs/CITED-DECISIONS.md` |
