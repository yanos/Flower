# Security

Flower protects a shared music library through device pairing and role-based permissions. **Listeners** can stream music and manage their own playlists. **Administrators** can also manage the shared library and server.

This is a technical reference to Flower's protections and their limitations—not a replacement for keeping your device, network, and backups secure.

For setup, see [Self-hosting](SELF-HOSTING.md) and
[Remote access](REMOTE-SETUP.md). Exact request formats and limits are in the
[HTTP API reference](API.md).

## Server owner checklist

If you host a Flower server, these are the most important precautions:

- **Send pairing links privately.** Each invite is a temporary credential.
  App links also carry the server's identity, which a bare code can't verify.
- **Only grant administrator access to devices you trust.** Administrators can change the
  shared library, invite devices, and revoke other administrators.
- **Revoke lost devices.** This stops future server access, but can't erase
  music or logs already copied elsewhere.
- **Never expose port 4533 to the internet.** It serves unencrypted HTTP. Use HTTPS for public
  access, and configure `TrustedProxies` for a reverse proxy or tunnel.
- **Protect the data folder and backups.** They contain private keys, paired
  devices and logs. Logs can contain file names and network addresses.

## Network access

By default, Flower's network gate (`LanGuard`) allows loopback, private and link-local networks, plus
Tailscale's `100.64.0.0/10` range. Connections from other addresses are aborted
without an HTTP response. This is an application-level allow-list, not a
replacement for a firewall.

| Setting | Effect |
|---|---|
| `AllowedCidrs` | Add allowed networks |
| `TrustTailscaleRange` | Include or exclude `100.64.0.0/10` |
| `AllowPublicAccess` | Disable the network allow-list for a public deployment |
| `TrustedProxies` | Accept forwarded client addresses from named proxy networks only |

Public access doesn't bypass device authentication. It does expose the HTTP
and TLS services to internet traffic. Keep it off for Tailscale-only access.

Behind a proxy, network checks use the forwarded client IP only when the proxy
is trusted. Otherwise Flower sees the proxy as the caller and warns when it
ignores `X-Forwarded-For`. Trust only proxies you control. Malformed network
entries don't match; the settings API rejects invalid `AllowedCidrs` entries,
and startup logs invalid entries found in the configuration file.

## Pairing and device identity

Each app or browser has a P-256 key pair. The server stores its public key at
pairing; the private key stays on the device. A browser uses a non-extractable
WebCrypto key stored in its browser profile.

| Pairing control | Behaviour |
|---|---|
| Invite lifetime | Ten minutes, one use; unused invites are lost at server restart |
| Code | Five characters; accepted only with proof that the device holds its key |
| Per-address limit | Five redemption attempts per minute |
| Server-wide guessing limit | Twenty wrong codes in ten minutes cancel all outstanding invites |
| Administrator role | Granted by the invite, never chosen by the joining device |

A `flower://pair` link includes the server fingerprint and reachable addresses.
The app checks the fingerprint before trusting the server. A bare code uses
trust on first use: it authorises the device to the answering server without
proving to the device that this is the intended server.

After code-only pairing, compare **Verified as** in the app's server panel
with **This server's identity** on an independently trusted server settings
screen. If they differ, unpair and investigate before pairing again.

## Encrypted connections

The server provides HTTPS on port 4534 with a certificate based on its
persistent device key. Installed apps check it against the identity pinned
at pairing. They prefer HTTPS. Local and Tailscale routes can also use HTTP
under the client's `CleartextOrigins` rules; request signing authenticates
requests but doesn't encrypt their contents.

Browsers use normal browser certificate validation and need HTTPS or
`localhost` for pairing. For shared browser access, use a trusted certificate
or a proxy that provides one. At a bare IP address, an installed app requires
the server's pinned certificate; reach a TLS-terminating proxy by its DNS name.

A proxy or tunnel that terminates TLS can read the traffic passing through it.
Response signatures protect specific trust decisions; they don't provide
end-to-end encryption through that proxy.

## Requests and replay protection

Paired devices sign every API request using ECDSA with SHA-256. The signed
form includes the method, path, canonical query, body hash, timestamp and nonce.
Query names and values are percent-encoded before sorting to avoid ambiguous
separators.

- Timestamps must be within **60 seconds** of the server clock.
- A `(fingerprint, nonce)` pair is remembered for **120 seconds**, including
  failed signature attempts. Every retry needs a fresh nonce.
- Fingerprints and nonces are limited to **64 characters**.
- Bodies on signed routes must declare `Content-Length`; unstated lengths
  receive `411 length-required`.
- Body sizes are checked before buffering. Unknown devices are rejected
  without reading their bodies.

The server verifies with the key on file, rather than a key offered by the
request. Pairing and self-signed discovery checks are the explicit exceptions.

### Browser audio tickets

An HTML audio element can't attach request-signature headers. A paired
browser therefore signs a request for a temporary stream ticket.

Tickets are bearer credentials limited to one track and its duration plus
fifteen minutes, with track duration capped at twelve hours. They live only in
server memory, are revoked with the issuing device, and are limited to 32 per
device. They allow repeated requests so seeking works. Ticketed requests
consume the issuing device's media budget.

Tickets appear in URL query strings. Flower's own logs omit queries; raising
ASP.NET Core's `Microsoft.*` logging from Warning to Information can record
those URLs. Review proxy access logs for the same risk.

## Listener permissions and privacy

A valid signature proves which device sent a request; it does not automatically grant every permission. Flower checks what the device is allowed to read or change for each operation.

| Resource | Administrator | Listener |
|---|---|---|
| Music | Read and change the shared library | Read and download |
| Playlists | Owner's shared playlist set | That listener device's own set |
| Continue Playing | Owner's shared shelf | That listener device's own shelf |
| Play / skip reports | Own device counts; owner fields where applicable | Own device counts only |
| Settings, device management and logs | Allowed | Refused |

A listener can't read, overwrite or delete another listener's playlists.
A playlist change must name the server revision it is based on; a stale change
is refused. Device-chosen timestamps don't decide which edit wins. Missing
songs and playlists aren't treated as deletions without explicit deletion
records.

The listener catalog excludes the owner's last-played dates, resume positions,
Music.app counts and other devices' play / skip counts. Stars remain visible
as the owner's library choices. Listening changes have a separate token and
an administrator-only endpoint, so a public catalog token doesn't reveal
when the owner plays a song.

Discovery reports paired-only details, such as addresses and roles, only to
verified devices. `trustsCaller: false` is returned only when an unknown device
proves it holds the key matching its claimed fingerprint. Anonymous claims
don't disclose that trust state.

## Request limits and storage bounds

Verified requests are limited by **device**, with separate budgets for sync,
artwork, media, administration, uploads, discovery and Vibe requests. One
listener's artwork requests can't consume another listener's playback budget.

Anonymous requests are limited by source address. Failed signatures are
limited by address and fingerprint, so failures from another address can't
use up the real device's budget there. IPv6 addresses are grouped by `/64`
for address limits, except link-local addresses.

Limits also apply to request bodies and stored data:

- **Bodies:** 20 MB overall; sync routes use 20 MB, 4 MB or 256 KB depending
  on the route. Pairing is capped at 4 KB.
- **Play events:** at most 500 per report; event IDs up to 64 characters;
  duplicate detection keeps at most 5,000 IDs per device for six hours.
- **Client logs:** at most 5,000 lines per report, retained for seven days
  and capped at 128 MB per device, oldest days removed first.
- **Stream tickets:** at most 32 per device; issuing another evicts the oldest.

Over-budget requests receive `429 rate-limited` with `Retry-After`.
See [API request limits](API.md#before-any-route-runs) for exact budgets.

## Errors and revocation

API errors use `application/problem+json` with a Flower `code`.
`401` means authentication failed; `403` means the verified device isn't
allowed to perform the operation. A bare status code never revokes an app's
pairing.

The server signs nonce-bearing discovery responses and `device-unknown`
errors with its own key. The signature binds the request path, response status,
body hash, timestamp and request nonce. A captured response can't revoke a
later pairing by being replayed against a new request.

An installed app unpairs only after verifying a response from its pinned
server that says either `trustsCaller: false` or `401 device-unknown`.
Unsigned or wrongly signed replies are treated as failed connections.
Clock skew and invalid signatures also leave pairing intact.

The browser player uses the trust response only to show its pairing screen
and keeps its key. Its code and responses come from the same web origin;
it relies on that origin's HTTPS security.

## Browser protections

The browser UI sends `Content-Security-Policy: frame-ancestors 'none'`,
`X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff`. This prevents
another site embedding an administrator's interface to trick them into
clicking privileged actions. Device aliases are cleaned of control and
bidirectional-override characters and limited to 64 characters before storage
or logging.

## Security limitations

- An administrator is trusted as the owner. Revoke a compromised administrator
  from another admin device.
- Revocation doesn't delete a listener's stored playlists or Continue Playing
  data from the server, and doesn't erase downloaded files on their device.
- A guest re-paired as an administrator switches to the owner's playlist set;
  its old guest playlists aren't automatically transferred.
- Guest browser history isn't persisted as a separate per-listener history.
- Authentication failure budgets can reveal coarse differences between a known
  and unknown fingerprint to a caller willing to exhaust both limits.
- Flower uses its own signing protocol. It doesn't implement RFC 9421 or
  third-party Subsonic authentication.

The implementation is covered by server tests for permissions, request limits,
replay and response signatures, plus client tests for authenticated revocation.
See [Tests](../Tests/README.md) and the [API reference](API.md) for details.

The numbered implementation steps referenced by source comments are preserved
in the [security review notes](SECURITY-REVIEW.md).
