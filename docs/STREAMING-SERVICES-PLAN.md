# Streaming Services (Spotify, Bandcamp, Apple Music) — Integration Plan

Source: investigation into "will it be possible to stream Spotify / Apple
Music / Bandcamp songs from Flower, provided we have the credentials?"

**Short answer up front, because it determines the entire shape of this plan:**
credentials are *not* the gating factor. Local files work because
`IAudioManager.Play(Track)` hands `track.Path` to LibVLC, which decodes
whatever bytes it's given. The sync feature works the same way —
`OpenSubsonicClient.GetStreamUrl(id)` returns a plain HTTP URL and LibVLC
plays it directly (the code comment there literally says *"LibVLC can also
[play these]"*). So the one question that decides feasibility for each
service is: **can we obtain a raw, DRM-free, LibVLC-decodable stream URL for a
track?** By that test the three services are not one feature — they're two
fundamentally different features:

| Service | Metadata/search API | Decodable stream URL? | Playback path | Platforms | Legitimacy |
|---|---|---|---|---|---|
| **Bandcamp** | No official API (scrape) | **Yes** — public `mp3-128` URLs | Existing LibVLC | All 5 | Grey (scraping, no DRM) |
| **Spotify** | Yes (Web API, OAuth2) | **No** — DRM, official SDKs only | Separate player / `librespot` sidecar | Varies | Red (ToS-violating for audio) |
| **Apple Music** | Yes (Apple Music API) | **No** — FairPlay DRM, MusicKit only | Separate native player | macOS/iOS only | Green, but Apple-only |

The rest of this doc explains why each row is what it is, then proposes an
architecture that lets Bandcamp land cheaply on the existing engine while
isolating the Spotify/Apple-Music complexity behind a clean seam rather than
contaminating the core player.

---

## Research summary (why this is shaped the way it is)

- **Flower's playback model is URL-in, and that's the whole leverage point.**
  `VlcAudioManager` (`Flower/Manager/VlcAudioManager.cs`) is the single
  `IAudioManager` implementation. `Play(Track)` builds a LibVLC `Media` from
  `track.Path`, already branching on `://` to use `FromType.FromLocation` for
  URIs (added for Android's `content://` MediaStore URIs). LibVLC will happily
  play an `http(s)://…mp3` URL with byte-range seeking. So any service that
  can yield such a URL needs **almost no new playback code** — it's a
  metadata/auth/URL-resolution problem, not an audio-engine problem.

- **The sync feature already built 80% of the "remote track" plumbing.**
  `Track.Path == null` + origin fields (`OriginDeviceFingerprint`,
  `OriginFileExtension`) already model "a track this device knows about but
  doesn't have the bytes for yet" (SYNC-PLAN.md Phase 3). `OpenSubsonicClient`
  already does OAuth-ish auth, metadata mapping (`Child`/`AlbumID3` DTOs), and
  `GetStreamUrl`/`DownloadTrackAsync`. A streaming service is conceptually the
  same shape as "another OpenSubsonic server" — a remote catalog you browse
  and stream/download from — **for the services that expose a stream URL at
  all.** This is the template Bandcamp follows almost exactly.

- **DRM is the hard wall, and two of the three services are behind it.**
  Spotify (encrypted Ogg Vorbis, Widevine/proprietary) and Apple Music
  (FairPlay) never expose decodable bytes to a third-party process. Their
  official playback SDKs decrypt and render internally and hand you *nothing*
  — no PCM callback, no file, no URL. That means LibVLC cannot be in the
  playback path for them at all; you must embed *their* player and treat it as
  a second, parallel audio engine. This is the single biggest architectural
  cost in the whole plan and is why they're deferred behind Bandcamp.

- **"Provided we have the credentials" understates two of the three.**
  Bandcamp public streaming needs *no* credentials. Apple Music needs both a
  paid **Apple Developer Program** membership ($99/yr, to mint the MusicKit
  developer JWT) *and* a per-user Music User Token *and* an active Apple Music
  subscription — and even with all three, playback is macOS/iOS-only. Spotify
  needs a registered app (OAuth client) *and* the user's Premium account, and
  the only way to get audio still violates Spotify's Developer Terms.

---

## Provider 1 — Bandcamp (recommended first; fits the existing engine)

**Why first:** it's the only one of the three where Flower's existing LibVLC
pipeline plays the actual audio, so it validates the whole provider
abstraction (below) end-to-end with the least new machinery and no DRM.

### What's available
- **No consumer API.** Bandcamp's developer API (`bandcamp.com/developer`)
  was always seller/label-oriented (band info, sales, merch) and has been
  largely deprecated/access-gated since the Epic acquisition (2022) and
  Songtradr sale (2023). There is no public search/catalog/stream API. Every
  third-party Bandcamp client works by **scraping**.
- **Streams are plain, DRM-free MP3.** An album/track page embeds a
  `data-tralbum` JSON blob whose `trackinfo[].file["mp3-128"]` field is a
  direct MP3 128 kbps URL (time-limited, signed, but public — the same URL the
  website's own player uses). These are directly playable by LibVLC with no
  decryption. Higher-quality streams are not exposed to the web player.
- **Purchased music is DRM-free downloadable** (MP3-320, FLAC, ALAC, etc.).
  Reaching a user's *purchased collection* requires an authenticated session:
  scrape `bandcamp.com/<fan-username>` (its `collection_items` internal JSON
  endpoint is what their own collection page uses), then hit each item's
  download page. This is the only part that needs credentials.

### Tech to use
- A `BandcampProvider` implementing the `IMusicProvider` seam (below).
  Search via scraping `bandcamp.com/search?q=…` (or the internal
  `bandcamp.com/api/fuzzysearch/1/autocomplete` used by the site's search
  box), album/track resolution by fetching the page and parsing
  `data-tralbum`. Plain `HttpClient` + `System.Text.Json`; no third-party
  Bandcamp library needed.
- Stream playback: resolve `mp3-128` URL → set it as the track's playable
  URL → hand to the **existing** `VlcAudioManager` unchanged. Because those
  URLs are time-limited, resolve them lazily *at play time*, not at
  browse/import time (unlike a local `Path`, a Bandcamp stream URL must not be
  cached in `library.json` — see the `ResolvePlayableUrlAsync` seam below).
- Optional "fan collection" import for logged-in users: reuse the placeholder-
  track model from sync (`Path == null`, a `BandcampProvider` origin tag)
  so purchased albums appear in the library and can be streamed or
  downloaded-to-local exactly like a synced peer's tracks.

### Limitations
- Scraping is brittle (page-shape changes break it) and against Bandcamp's
  ToS, though there's no DRM circumvention involved — the streams are the same
  bytes the public site serves.
- Stream quality is capped at 128 kbps MP3 for streaming (full quality only
  via actual purchase/download).
- Requires the artist to have made the track streamable (some are
  purchase-only, exposing only a clip).

---

## Provider 2 — Spotify (large; audio path is a separate engine and grey-area)

### What's available
- **Web API** (`api.spotify.com`): OAuth2 (Authorization Code + **PKCE** — the
  right flow for a desktop/mobile app with no server secret). Full catalog
  metadata, search, the user's saved library, playlists, and *playback
  control* of Spotify Connect devices. **It does not return audio.**
- **30-second previews are no longer reliable.** The Web API's `preview_url`
  (30s MP3 clips) is increasingly returned as `null` for newly-registered
  apps (Spotify restricted it in late 2024). So not even previews are a
  dependable free path anymore.
- **Web Playback SDK**: browser/JavaScript-only, plays through
  EME/Widevine in a `<web>` context, Premium required. Not embeddable in a
  native Avalonia/.NET process without shipping a full browser engine and a
  DRM CDM — impractical.
- **iOS/Android App Remote SDK**: *remote-controls the installed Spotify app*
  (which does the actual playback), returning no audio to Flower. Only works
  if the user has the Spotify app installed and running.
- **`libspotify`** (the old official C playback SDK) is **discontinued** and
  unavailable.
- **`librespot`** (open-source Rust reimplementation of the Spotify client;
  powers `spotifyd`, `go-librespot`, `raspotify`) *can* stream full-quality
  audio using Premium credentials, by speaking Spotify's protocol directly.
  This is the **only** way to get decodable audio into a custom app — and it
  is a **reverse-engineered client that violates Spotify's Developer Terms**
  (which forbid using their service to build a competing player or to
  circumvent playback). Ship-ability is the real blocker here, not difficulty.

### Tech to use (if pursued at all)
- **Metadata/browse/search/playlists**: Spotify Web API via OAuth2 PKCE, a
  `SpotifyProvider : IMusicProvider`. This part is legitimate and low-risk and
  could ship on its own as "browse your Spotify library in Flower" with
  playback handed off to Spotify Connect (below).
- **Playback, legitimate option — Spotify Connect remote control**: Flower
  becomes a *remote* for playback happening on an official Spotify client
  (desktop app, phone, speaker) via the Web API's player endpoints. No audio
  in Flower, no DRM issue, ToS-clean — but requires an official Spotify client
  to be the actual sound source, so it's "control," not "stream."
- **Playback, audio-in-Flower option — `librespot` sidecar**: bundle
  `librespot` as a subprocess that either (a) advertises itself as a Spotify
  Connect device Flower drives, or (b) pipes decoded PCM/an HTTP stream Flower
  feeds to a **second `IAudioManager`** (or to LibVLC as a local pipe). Big
  build/packaging surface (a Rust binary per platform), Premium-only, and
  ToS-violating. **Recommend against shipping this**; document it as the only
  technically-possible path so the tradeoff is explicit.

### Limitations
- No first-party way to get audio bytes into a non-browser, non-Apple app.
- Premium required for any playback (free tier can't even be remote-driven
  ad-free/on-demand).
- The only audio-in-app path (`librespot`) is a ToS violation and a
  per-platform native-binary dependency.

---

## Provider 3 — Apple Music (legitimate, but Apple-platform-only and a second engine)

### What's available
- **Apple Music API** (`api.music.apple.com`): full catalog + the user's
  library, playlists, search, recommendations. Auth needs **two** tokens:
  - a **Developer Token** — a JWT signed **ES256** with a MusicKit private key
    from a **paid Apple Developer Program** account ($99/yr). Flower would
    ship/refresh this.
  - a **Music User Token** — per-user, obtained through MusicKit's auth flow;
    requires the user to have an Apple Music subscription.
- **Playback is MusicKit-only, and MusicKit is Apple-only:**
  - **Native MusicKit** (Swift/ObjC framework, iOS 15+/macOS 12+) plays
    catalog audio through the OS. This is the clean path **on iOS and macOS**.
  - **MusicKit JS** plays via EME in a browser (same impracticality as
    Spotify's Web Playback SDK for a native app).
  - **No Windows/Linux playback exists at all**, and **no raw stream on any
    platform** (FairPlay DRM). LibVLC is never in the path.

### Tech to use
- **Metadata everywhere** (all 5 platforms): the Apple Music **API** is plain
  REST and works cross-platform with just the two tokens — an
  `AppleMusicProvider : IMusicProvider` for search/browse/library. This is the
  legitimate, portable half.
- **Playback on macOS/iOS only**, via a **native MusicKit player** wrapped as
  a **second `IAudioManager` implementation** (`MusicKitAudioManager`) behind
  the same interface, bridged from Swift/ObjC through the .NET iOS/macOS
  bindings. On Windows/Linux, Apple Music tracks are browse-only (greyed out,
  like an un-downloaded sync placeholder) — or, on macOS specifically, an
  alternative is ScriptingBridge/AppleScript control of the **Music.app**
  (the same automation approach `ITunesPlayCountImporter` already uses for the
  library XML export), which sidesteps the Developer Program token but makes
  Music.app the sound source (control, not embed).
- This overlaps directly with `SYNC-PLAN.md`'s decided stance ("iOS owns its
  files, no `MPMediaLibrary`/Apple Music integration"). That decision was
  about the *local library importer*; Apple Music *streaming* would be an
  additive, clearly-separate playback source, but the two must not be
  conflated in the UI.

### Limitations
- Playback is **macOS/iOS only**; Windows/Linux/Android get metadata but can
  never play a track.
- Requires a paid Apple Developer membership (to mint the dev token) on top of
  the user's own Apple Music subscription.
- Introduces the second-audio-engine problem (below) even in its cleanest
  form.

---

## The core architectural work (shared across all three)

Two seams, corresponding to the two-category split at the top.

### Seam 1 — `IMusicProvider` (metadata + URL resolution) — cheap, shared by all three

A provider abstraction over "a catalog you can search/browse and get a
playable handle from," generalizing what `OpenSubsonicClient` already does for
one server type:

```
IMusicProvider
  Task<IReadOnlyList<Track>> SearchAsync(string query)
  Task<IReadOnlyList<Track>> BrowseLibraryAsync()      // the user's saved/purchased items
  Task<string?> ResolvePlayableUrlAsync(Track track)   // null ⇒ not URL-playable (see Seam 2)
```

- **`Track` provenance**: add a `Source`/provider tag (local, syncPeer,
  bandcamp, spotify, appleMusic) alongside the existing `OriginDeviceFingerprint`
  pattern, plus a stable per-provider id. Keep `Path` meaning "local bytes
  exist"; a streaming track has `Path == null` until (optionally) downloaded,
  exactly like a sync placeholder.
- **Lazy, un-cached URL resolution**: `ResolvePlayableUrlAsync` is called at
  *play time*, and its result must **never** be persisted to `library.json`
  (Bandcamp/OpenSubsonic stream URLs are signed and time-limited; Spotify/Apple
  return `null` here because they aren't URL-playable at all). This is the one
  new hook `PlaylistControlViewModel`/`VlcAudioManager` need.
- Local, OpenSubsonic-sync, and **Bandcamp** all implement this fully and reuse
  `VlcAudioManager` untouched.

### Seam 2 — multiple `IAudioManager`s + an active-engine switch — expensive, only for DRM services

For Spotify(`librespot`)/Apple Music, `ResolvePlayableUrlAsync` returns `null`
and playback must go through *their* engine. This is the costly part, because
the entire app assumes a single `IAudioManager` singleton owns
position/seek/volume/`Playing`/`Paused`/`EndReached`:

- Introduce an `IAudioManager` **per playback backend** (`VlcAudioManager`,
  `MusicKitAudioManager`, a `LibrespotAudioManager`) and an
  `AudioManagerRouter` that picks the right one from `track.Source` and
  forwards the unified event surface upward so `PlaylistControlViewModel`
  doesn't change.
- Hard problems this creates: reconciling two clocks (LibVLC `Position`/`Time`
  vs. a native player's), gapless/queue handoff when consecutive tracks are on
  different engines, volume normalization, and lifecycle (a native SDK player
  running alongside LibVLC). This is genuinely large and is the reason
  Spotify/Apple-Music playback is *phase 3+*, not a quick win.

### Cross-cutting: credentials

- Secrets (Spotify OAuth tokens, Apple Music user token, Bandcamp session
  cookie) stored via the OS secret store, **not** `settings.json`: macOS
  Keychain, Windows DPAPI/Credential Manager, libsecret on Linux, and the
  platform keystores on mobile. New `ISecretStore` abstraction; ties into
  `CRASH-REPORTING-PLAN.md`'s "don't log secrets" concern.
- OAuth redirect handling on desktop: loopback `http://127.0.0.1:<port>`
  listener (Flower already runs an HTTP listener for sync —
  `SyncHttpServer` — so the pattern exists) or a custom URI scheme.

---

## Recommended sequencing

1. **`IMusicProvider` seam + `ResolvePlayableUrlAsync` hook** — refactor
   local + existing OpenSubsonic sync onto it first, changing no behavior. This
   is the enabling groundwork and de-risks everything after.
2. **Bandcamp (public streaming)** — first real streaming provider, rides the
   existing LibVLC engine, no credentials, no DRM. Proves the seam.
3. **Bandcamp (fan collection)** — logged-in purchased-library browse +
   download-to-local, reusing the sync placeholder/download flow.
4. **Spotify metadata + Spotify Connect control** — legitimate, ToS-clean:
   browse your Spotify library in Flower, hand playback to an official Spotify
   client. No second engine yet.
5. **Apple Music metadata (all platforms) + MusicKit playback (macOS/iOS)** —
   introduces Seam 2 (`MusicKitAudioManager`) in its cleanest, fully-legitimate
   form. Windows/Linux/Android: browse-only.
6. **(Explicitly optional / not recommended) Spotify audio-in-app via
   `librespot`** — documented for completeness; ToS-violating and a
   per-platform native-binary dependency. Decision point, not a default.

## Bottom line

- **Bandcamp: yes, and it fits Flower's existing engine** — the realistic
  "stream songs from Flower" win. Cost is scraping fragility, not DRM.
- **Apple Music: yes for metadata everywhere, playback only on macOS/iOS**,
  legitimately, at the price of a paid developer membership and a second audio
  engine.
- **Spotify: metadata and remote-control yes; actual audio-in-Flower only via
  a ToS-violating reverse-engineered client** — recommend browse+Connect-control
  and stop short of embedding audio.

"Having the credentials" only unlocks the *metadata and account* half. The
audio half is decided by DRM, and only Bandcamp is on the right side of it.
