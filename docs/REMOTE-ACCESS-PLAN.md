# Reaching your own server from anywhere

How a Flower client finds and keeps hold of its paired server when the two are
not on the same network — the client-side half of the remote-access story whose
server half is in `SYNC-PLAN.md` and whose setup guide is `SELF-HOSTING.md`.

## The problem this exists to fix

The server side of remote access has been done for a while. `LanGuard` admits
Tailscale's `100.64.0.0/10`, stream tickets are bound to a track and a
fingerprint rather than to an IP, `/rest/stream` does range requests so seeking
works over a tunnel, and `TrustedProxies` handles the loopback hop that
`tailscale serve` introduces. The audio path is ready too: `TrackDecoder.
EnsureMedia` branches on `path.Contains("://")` and hands LibVLC a network
location, so a placeholder row streams on desktop and on a phone alike.

None of that mattered, because of one thing:

**Reachability was defined as "mDNS can see it right now."**

- `PairedServerReachability.Recompute` resolved the paired server by looking its
  fingerprint up in `NetworkDiscoveryService.KnownDevices`.
- `KnownDevices` was populated only by mDNS browse.
- mDNS is link-local multicast. It does not cross a subnet, and it does not
  reach into a tailnet.
- So off the home LAN, `PairedServerDevice` was null, `PeerTrackResolver`
  returned nothing, and `PlaylistControlViewModel` logged "no stream URL could be
  built" and refused to play.

Nothing persisted an address either — `AppSettings` held
`PairedServerFingerprint` and a display-only alias, no host and no port — so
there was nothing to fall back to. `DiscoveredDevice.EndPoint` was a hard-typed
`IPEndPoint`, so the type system could not even hold a hostname.

An earlier revision of `SELF-HOSTING.md` told the reader to "add the server by
address instead". No such feature existed. That line described this document's
subject as though it were already built.

## The shape of the fix: let the server say where it lives

The instinct to reach for first is a text box: let the user type the tailnet
address. That is the wrong primary path. The server already knows every address
it can be reached on, and the client already talks to it — so the address should
travel by itself, and typing one should be the bootstrap case only.

Every peer already performs an `/info` handshake before it is usable at all
(`SyncProtocol.InfoPath`, `NetworkDiscoveryService.ResolveAliasAsync`), and
`SyncInfoResponseDto` already carries the fingerprint, `IsServer` and
`TrustsCaller`. It gains one more field: the addresses the server believes it can
be reached on.

```
GET /api/localsend/v2/info
{ ..., "addresses": ["192.168.1.40:4533", "100.101.102.103:4533",
                     "basement.tail1234.ts.net:4533"] }
```

Sources, in order: the configured `AdvertisedHost` if there is one; then every
non-loopback, non-link-local unicast address on an up interface, with the bound
port.

**No Tailscale integration is involved, and deliberately so.** The server does
not shell out to the `tailscale` binary or read its state — it enumerates its own
interfaces with `NetworkInterface.GetAllNetworkInterfaces`, and a tailnet address
is simply the one that lands in `100.64.0.0/10`, the range `LanGuard` already
recognises. `SYNC-PLAN.md`'s "document, don't automate" decision stands
untouched; this is the server reporting its own network configuration, which it
is entitled to know.

Noise is tolerable. A Docker bridge on `172.17.x` will be reported and will never
answer, and a candidate that does not answer is simply skipped.

### The client learns and remembers

On every **successful** `/info` against the paired server, the client replaces
its stored candidate list for that server. Three consequences worth stating
plainly, because they are the whole point:

- **Pair once at home and the tailnet address arrives on its own.** The user
  types nothing.
- **It self-heals.** Install Tailscale on the server a week after pairing, and
  the next sync at home teaches the phone the new address. A DHCP change on the
  LAN corrects itself the same way.
- **Replaced, not merged.** An address the server has stopped reporting is
  dropped, so a stale entry cannot linger being probed forever.

### LAN ↔ tailnet: rank and race

`PairedServerReachability` stops meaning "mDNS sees it" and starts meaning "one
of these candidates answered `/info` with the fingerprint we paired with."
Candidates are ranked:

1. **A live mDNS sighting.** Definitive — the peer is on this link right now.
2. **Private/RFC1918 candidates.** The home LAN: direct, fast, no relay.
3. **CGNAT `100.64/10` candidates.** The tailnet. Works anywhere, but may be
   relayed through a DERP node, so it is a fallback rather than a preference.
4. **Named hosts.** `AdvertisedHost`, a MagicDNS name, anything added by hand.

Probe concurrently against the existing 3-second timeout and take the
best-ranked success. That ranking is what makes walking out of the house work:
at home rank 2 wins; on cellular it fails fast and rank 3 answers; walking back
in, rank 1 or 2 wins again and the client returns to the direct path without
anyone noticing.

The engine already exists. `PollKnownDevicesAsync` runs every 5 seconds
(`AliasPollInterval`) and already dials `/info` at every known peer. Re-probing
candidates when the paired server is *not* currently reachable slots into that
loop, so a network transition heals within about 15 seconds with no new
machinery. `NetworkChange.NetworkAddressChanged` — used nowhere in this repo
today — would cut that to near-instant and is worth adding as an optimisation,
but not as the mechanism: on iOS it is not dependable enough to be the only
thing standing between the user and a dead player.

### The pruning rule has to change with it

`NetworkDiscoveryService` removes a peer after `MaxConsecutiveResolveFailures`
(3) failed polls. For an mDNS peer that is correct — a fresh announcement brings
it back, and keeping a dead entry would only mean discovering, failing and
pruning the same thing forever.

A remembered peer has no announcement to bring it back. Pruning one deletes the
only record of how to reach it. So remembered candidates are exempt: a
remembered peer that stops answering reads as **unreachable**, not as gone. The
IPv6 link-local exemption a few lines above in the same method is the same shape
and the precedent to follow — it, too, declines to prune for a "this will come
back" reason.

### Why this is safe

Only the **paired** server's addresses are ever persisted or probed, and a
candidate is accepted only when `/info` returns the fingerprint we actually
paired with — the check `SyncRolePolicy.MayRequestFrom` already performs.

That gate is load-bearing rather than incidental. Persisting an address list
handed over by an unauthenticated `/info` would let any peer on the network aim
the client's probes at hosts of its choosing.

## Scope

**Built here:**

1. `SyncInfoResponseDto.Addresses`, filled by both servers — `Flower.Server`
   (`DiscoveryEndpoints`) and the app's own `SyncHttpServer.HandleInfoAsync`, so
   a desktop acting as a server behaves the same way as the headless one.
2. Remembered candidates on `AppSettings`, refreshed on every successful `/info`.
3. Candidate-based `PairedServerReachability` with the ranking above,
   synthesising a `DiscoveredDevice` for a remembered peer rather than requiring
   an mDNS sighting.
4. Remembered peers exempt from failure-pruning.
5. **Manual add, as the bootstrap case only** — a server the client has never
   shared a LAN with. One text box and an Add button, in
   `Flower/Views/Mobile/SettingsView.axaml` above the `AvailableServers` list and
   in `Flower/Views/ServerPickerView.axaml`. Port defaults to **4533**,
   `Flower.Server`'s port — not `SyncProtocol.DefaultPort` (53317), which is the
   app-to-app one. Once added, the server pairs through the existing per-row Pair
   button.
6. Surfacing *how* the server is currently reached (LAN / tailnet / unreachable).
   Without it, a silent fallback to a relayed path is indistinguishable from a
   fast direct one, and "why is this suddenly slow" has no answer.

**Deliberately not built here:**

- **HTTPS on the native client.** All nine peer URLs are built as
  `$"http://{device.EndPoint}"`, so a `tailscale serve` HTTPS front end is
  unreachable from the app. Plain HTTP over WireGuard is the tested path and
  `SELF-HOSTING.md` presents it as a complete option, because WireGuard already
  encrypts it. The scheme rework — nine call sites, and `IPEndPoint` widened to
  hold a name — is its own change with its own reason to happen.
- **`flower://` invites, QR scanning, fingerprint pinning.** `PairingInvite`
  (`Flower.Core/Services/PairingUri.cs`) is produced by the server and parsed only
  in `Flower.Tests`. No client calls `TryParse`, no URL scheme is registered on
  either mobile platform, and the fingerprint pin its own remarks call
  load-bearing is never actually checked by anything. Real gaps, recorded here so
  they stop being invisible.
- **Transcoding.** There is none anywhere in the codebase, so streaming ships
  original files — a FLAC is around 1000 kbps against a home upload link. Fine
  for one listener on a decent connection, heavy on cellular. Relevant to how a
  remote listening test *feels*, so worth knowing before drawing conclusions from
  one.

## Verification

Unit coverage in `Flower.Tests`:

- A server reports its own non-loopback addresses; loopback and link-local are
  excluded.
- A client with no mDNS sighting at all reaches a remembered candidate.
- Ranking: with both a LAN and a CGNAT candidate answering, the LAN one is
  chosen; when it stops answering the CGNAT one takes over, and the server stays
  reachable throughout the switch.
- A remembered peer survives more than `MaxConsecutiveResolveFailures` failures;
  an mDNS peer still prunes after 3.
- Candidates are replaced from `/info`, and a dropped address stops being probed.
- A candidate answering with the *wrong* fingerprint is rejected.

The real test is by hand, because the thing being tested is a network
transition:

1. `sudo tailscale up` on the server; Tailscale on the phone, same account.
2. **At home, on Wi-Fi:** pair normally over mDNS, then confirm the phone has
   learned the `100.x` address without anything having been typed.
3. **Off Wi-Fi, on cellular:** play a track, seek mid-track (range requests
   survive the tunnel), let one track auto-advance (`ArmUpcoming`'s pre-resolve
   works remotely), and confirm the play count and last-played reach the server.
4. **Walk back onto Wi-Fi mid-playback** and confirm it returns to the LAN path
   without interrupting audio.
5. Kill every `Flower.Server` started along the way — see `CLAUDE.md`.

## Status

**Built.** Written up front, before any code, because the design question worth
getting right here — should the user type an address, or should the server say
where it lives — is the one that determines whether remote access is a feature
people configure or one that simply works.

Landed: `LocalAddresses` (shared, so both servers answer the same way),
`SyncInfoResponseDto.Addresses`, `DiscoveredDevice.IsRemembered`/`IsResponding`/
`Addresses`, `NetworkDiscoveryService.AddRememberedAsync` with the pruning
exemption and `ReachRank`, `AppSettings.PairedServerAddresses`/
`ManualServerAddresses`, candidate-based `PairedServerReachability` with
`ServerRoute`, restore-at-startup in `App.axaml.cs`, and the manual-add UI on
both `Flower/Views/Mobile/SettingsView.axaml` and
`Flower/Views/ServerPickerView.axaml`.

Verified: `Flower.Tests` 1068/1068, `Flower.Server.Tests` 165/165, iOS builds
for the simulator. A live server was confirmed to report its own LAN and ULA
addresses at the bound port, with no loopback or link-local entry.

**Not yet verified against a real tailnet or a real phone.** There is no
Tailscale on the development machine, so the `100.64/10` branch of `ReachRank`
and the whole LAN↔tailnet handover are covered by tests rather than by
observation. The hand-run in "Verification" above is what closes that, and it is
the thing to do before trusting any of this away from home.
